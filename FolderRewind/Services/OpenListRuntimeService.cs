using FolderRewind.Models;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static class OpenListRuntimeService
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = OnboardingOperationBudgets.ServiceProbe };
    public static Uri Validate(OpenListRuntimeSettings settings)
    {
        var uri = RcloneConnectionService.ValidateWebDavUrl(settings.ServiceBaseUri);
        foreach (var path in new[] { settings.ExecutablePath, settings.WorkingDirectory, settings.DataDirectory, settings.ConfigFilePath }.Where(p => p.Length != 0))
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException(I18n.GetString("OpenList_AbsolutePaths"));
        if (settings.DataDirectory.Length != 0)
        {
            var protectedPaths = ConfigService.CurrentConfig.BackupConfigs.SelectMany(c => c.SourceFolders.Select(s => s.Path).Append(c.DestinationPath)).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            if (!BackupPathOverlapPolicy.Validate(settings.DataDirectory, protectedPaths).IsSafe)
                throw new ArgumentException(I18n.GetString("OpenList_DataOverlap"));
        }
        return uri;
    }
    public static async Task<OnboardingCheck> CheckAsync(OpenListRuntimeSettings settings, CancellationToken token = default)
    {
        var uri = Validate(settings);
        try
        {
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            return new("openlist.endpoint-observed", OnboardingCheckState.Unknown,
                I18n.Format("OpenList_EndpointObserved", (int)response.StatusCode), DateTimeOffset.UtcNow, ["openlist.management", "cloud.recheck"]);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return new("openlist.endpoint-unconfirmed", OnboardingCheckState.NeedsInput,
                I18n.GetString("OpenList_EndpointUnavailable"), DateTimeOffset.UtcNow, ["openlist.manual-start", "openlist.recheck"]);
        }
    }
    internal static async Task SaveAsync(OpenListRuntimeSettings draft)
    {
        _ = Validate(draft);
        var settings = ConfigService.CurrentConfig.GlobalSettings;
        var original = settings.OpenListRuntime;
        draft.AllowStartOnDemand = false; // 没有可验证本机授权/目录占用机制，保存不构成启动授权。
        await ConfigEditTransaction.ApplyAsync(() => settings.OpenListRuntime = draft,
            () => { if (ReferenceEquals(settings.OpenListRuntime, draft)) settings.OpenListRuntime = original; },
            () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"));
    }
}

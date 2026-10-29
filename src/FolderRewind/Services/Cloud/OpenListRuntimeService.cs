using FolderRewind.Models;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static class OpenListRuntimeService
{
    private static readonly OwnedCloudDependencyProcess ManagedProcess = new();
    internal static string MachineIdentity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "|" + Environment.UserName)));
    internal static bool HasMachineAuthorization(OpenListRuntimeSettings settings)
        => settings.Ownership == OpenListOwnership.ManagedLocal && settings.AuthorizationMachine == MachineIdentity
            && settings.AuthorizationSignature.Length == 64;
    private static async Task<string> SignatureAsync(OpenListRuntimeSettings settings, CancellationToken token)
    {
        _ = Validate(settings);
        foreach (var path in new[] { settings.ExecutablePath, settings.WorkingDirectory, settings.DataDirectory, settings.ConfigFilePath })
        {
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException(I18n.GetString("OpenList_AbsolutePaths"));
            RcloneCredentialRefresh.RejectReparsePoints(path);
        }
        if (!Directory.Exists(settings.WorkingDirectory) || !Directory.Exists(settings.DataDirectory) || !File.Exists(settings.ConfigFilePath))
            throw new IOException(I18n.GetString("OpenList_EnvironmentMissing"));
        var text = settings.ExecutablePath + "|" + settings.WorkingDirectory + "|" + settings.DataDirectory + "|" + settings.ConfigFilePath
            + "|" + settings.ServiceBaseUri + "|" + MachineIdentity;
        foreach (var file in new[] { settings.ExecutablePath, settings.ConfigFilePath })
        {
            await using var stream = File.OpenRead(file);
            text += "|" + Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
    internal static async Task AuthorizeAsync(OpenListRuntimeSettings draft, CancellationToken token)
    {
        if (!Validate(draft).IsLoopback) throw new ArgumentException(I18n.GetString("OpenList_LocalEndpointRequired"));
        draft.Ownership = OpenListOwnership.ManagedLocal;
        draft.AuthorizationSignature = await SignatureAsync(draft, token).ConfigureAwait(false);
        draft.AuthorizationMachine = MachineIdentity;
        var start = new ProcessStartInfo(draft.ExecutablePath) { WorkingDirectory = draft.WorkingDirectory,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("version");
        var version = await RcloneConnectionService.RunAsync(start, TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        if (!version.Contains("OpenList", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(I18n.GetString("OpenList_VersionUnconfirmed"));
    }
    internal static async Task<bool> IsAuthorizationCurrentAsync(OpenListRuntimeSettings settings, CancellationToken token)
        => HasMachineAuthorization(settings) && settings.AuthorizationSignature == await SignatureAsync(settings, token).ConfigureAwait(false);
    internal static async Task<IDisposable?> AcquireDependencyAsync(CancellationToken token, bool manual = false)
    {
        token.ThrowIfCancellationRequested();
        var settings = ConfigSnapshotMapper.Copy(ConfigService.CurrentConfig.GlobalSettings.OpenListRuntime);
        // External services are reached through the cloud connection itself. They do not require
        // a global process-management endpoint or grant Host any right to start or stop a process.
        if (!manual && settings.Ownership == OpenListOwnership.External) return null;
        var authorized = false;
        if (HasMachineAuthorization(settings) && (settings.AllowStartOnDemand || manual))
        {
            try { authorized = settings.AuthorizationSignature == await SignatureAsync(settings, token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return await ManagedProcess.AcquireAsync(settings.AuthorizationSignature, authorized, () =>
        {
            var start = new ProcessStartInfo(settings.ExecutablePath) { WorkingDirectory = settings.WorkingDirectory };
            start.ArgumentList.Add("server"); start.ArgumentList.Add("--data"); start.ArgumentList.Add(settings.DataDirectory);
            start.ArgumentList.Add("--config"); start.ArgumentList.Add(settings.ConfigFilePath); return start;
        }, ct => EndpointObservedAsync(settings, ct), TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
    }
    internal static bool CanStop => ManagedProcess.CanStop(ConfigService.CurrentConfig.GlobalSettings.OpenListRuntime.AuthorizationSignature);
    internal static bool OwnsCurrent => ManagedProcess.Owns(ConfigService.CurrentConfig.GlobalSettings.OpenListRuntime.AuthorizationSignature);
    internal static async Task AuthorizeAndStartAsync(OpenListRuntimeSettings draft, bool requireOwned, CancellationToken token,
        OpenListRuntimeSettings? expected = null)
    {
        if (requireOwned) OpenListInitializer.RequireAvailablePort(Validate(draft).Port);
        var settings = ConfigService.CurrentConfig.GlobalSettings;
        var previous = settings.OpenListRuntime;
        if (expected is not null && !OpenListEnvironmentEdits.SameSnapshot(previous, expected))
            throw new InvalidOperationException(I18n.GetString("OpenList_EditConflict"));
        await AuthorizeAsync(draft, token);
        draft.AllowStartOnDemand = true;
        var saved = await SaveAsync(draft, previous, token);
        using var lease = await AcquireDependencyAsync(token, manual: true);
        if (requireOwned && !OwnsCurrent)
        {
            // A different process may have taken the port between the preflight and startup.
            if (ReferenceEquals(settings.OpenListRuntime, saved))
                await ConfigEditTransaction.ApplyAsync(() => settings.OpenListRuntime = previous,
                    () => { if (ReferenceEquals(settings.OpenListRuntime, previous)) settings.OpenListRuntime = saved; },
                    () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"));
            throw new InvalidOperationException(I18n.GetString("CloudWizard_OpenListPortConflict"));
        }
    }
    internal static Task<IDisposable?> AcquireForConnectionAsync(bool dependsOnLocalOpenList, CancellationToken token)
        => dependsOnLocalOpenList ? AcquireDependencyAsync(token) : Task.FromResult<IDisposable?>(null);
    internal static Task StopAsync(CancellationToken token) => ManagedProcess.StopAsync(ConfigService.CurrentConfig.GlobalSettings.OpenListRuntime.AuthorizationSignature, token);
    internal static async Task<bool> EndpointObservedAsync(OpenListRuntimeSettings settings, CancellationToken token)
    {
        try { using var response = await Client.GetAsync(Validate(settings), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); return response.IsSuccessStatusCode; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) { return false; }
    }
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = OnboardingOperationBudgets.ServiceProbe };
    public static Uri Validate(OpenListRuntimeSettings settings)
    {
        ValidateDraft(settings);
        Uri uri;
        try { uri = RcloneConnectionService.ValidateWebDavUrl(settings.ServiceBaseUri); }
        catch (ArgumentException error) { throw new ArgumentException(I18n.GetString("OpenList_InvalidServiceUri"), error); }
        return uri;
    }
    internal static void ValidateDraft(OpenListRuntimeSettings settings)
    {
        if (settings.ServiceBaseUri.Length != 0)
        {
            try { _ = RcloneConnectionService.ValidateWebDavUrl(settings.ServiceBaseUri); }
            catch (ArgumentException error) { throw new ArgumentException(I18n.GetString("OpenList_InvalidServiceUri"), error); }
        }
        foreach (var path in new[] { settings.ExecutablePath, settings.WorkingDirectory, settings.DataDirectory, settings.ConfigFilePath }.Where(p => p.Length != 0))
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException(I18n.GetString("OpenList_AbsolutePaths"));
        {
            var protectedPaths = ConfigService.CurrentConfig.BackupConfigs.SelectMany(c => c.SourceFolders.Select(s => s.Path).Append(c.DestinationPath))
                .Where(p => !string.IsNullOrWhiteSpace(p)).Append(ConfigService.ConfigDirectory).ToArray();
            foreach (var path in new[] { settings.WorkingDirectory, settings.DataDirectory, settings.ConfigFilePath }.Where(p => p.Length != 0))
                if (!BackupPathOverlapPolicy.Validate(path, protectedPaths).IsSafe)
                    throw new ArgumentException(I18n.GetString("OpenList_DataOverlap"));
        }
    }

    internal static async Task<string> InspectAsync(OpenListRuntimeSettings settings, CancellationToken token)
    {
        ValidateDraft(settings);
        var version = "";
        if (File.Exists(settings.ExecutablePath))
        {
            try { version = await ToolExecutableVerifier.VerifyAsync(settings.ExecutablePath, "openlist", token); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception
                or ArgumentException or OperationCanceledException && !token.IsCancellationRequested) { }
        }
        token.ThrowIfCancellationRequested();
        var complete = File.Exists(settings.ExecutablePath) && Directory.Exists(settings.WorkingDirectory)
            && Directory.Exists(settings.DataDirectory) && File.Exists(settings.ConfigFilePath) && settings.ServiceBaseUri.Length != 0;
        var authorized = false;
        if (complete)
        {
            try { authorized = await IsAuthorizationCurrentAsync(settings, token); }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        }
        var observed = settings.ServiceBaseUri.Length != 0 && await EndpointObservedAsync(settings, token);
        var versionLine = version.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.TrimStart().StartsWith("Version:", StringComparison.OrdinalIgnoreCase));
        var displayVersion = versionLine is null ? "" : "OpenList " + versionLine.Trim()[8..].Trim();
        string State(bool ready) => I18n.GetString(ready ? "CloudEnvironment_Confirmed" : "CloudEnvironment_Pending");
        return I18n.Format("CloudEnvironment_Status", displayVersion.Length == 0 ? State(version.Length != 0) : displayVersion,
            State(version.Length != 0 && OpenListInitializer.Supports(version)), State(complete), State(authorized), State(observed));
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
    internal static async Task<OpenListRuntimeSettings> SaveAsync(OpenListRuntimeSettings draft,
        OpenListRuntimeSettings? expected = null, CancellationToken token = default)
    {
        ValidateDraft(draft);
        var settings = ConfigService.CurrentConfig.GlobalSettings;
        var original = settings.OpenListRuntime;
        var saved = ConfigSnapshotMapper.Copy(draft);
        if (!OpenListEnvironmentEdits.SameIdentity(original, saved) && saved.AuthorizationSignature == original.AuthorizationSignature)
            OpenListEnvironmentEdits.ClearAuthorization(saved);
        if (HasMachineAuthorization(saved))
        {
            try { if (!await IsAuthorizationCurrentAsync(saved, token)) OpenListEnvironmentEdits.ClearAuthorization(saved); }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            { OpenListEnvironmentEdits.ClearAuthorization(saved); }
        }
        else OpenListEnvironmentEdits.ClearAuthorization(saved);
        if (ManagedProcess.Owns(original.AuthorizationSignature) && (!OpenListEnvironmentEdits.SameIdentity(original, saved)
            || original.AuthorizationSignature != saved.AuthorizationSignature || original.Ownership != saved.Ownership))
            throw new CloudSetupInputException("OpenList_StopBeforeChange");
        await ConfigEditTransaction.ApplyAsync(() =>
            {
                if (!ReferenceEquals(settings, ConfigService.CurrentConfig.GlobalSettings)
                    || !OpenListEnvironmentEdits.SameSnapshot(settings.OpenListRuntime, expected ?? original))
                    throw new InvalidOperationException(I18n.GetString("OpenList_EditConflict"));
                settings.OpenListRuntime = saved;
            },
            () => { if (ReferenceEquals(settings.OpenListRuntime, saved)) settings.OpenListRuntime = original; },
            () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"), token);
        return saved;
    }
}

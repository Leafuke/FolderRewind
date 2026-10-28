using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed record CloudRepositoryDisplay(int SchemaVersion, string ConfigId, string ProjectName,
    string LatestVersionId, DateTimeOffset LatestVersionUtc, DateTimeOffset PublishedUtc);

internal static class CloudRepositoryDisplayService
{
    private static string Remote(RcloneExecutionContext context, HistoryConfigId id) => CloudRemotePath.Append(context.RemoteRoot,
        "_folderrewind/history", HistoryRepositoryPaths.EncodeConfigPathSegment(id), "display.v1.json");
    internal static CloudRepositoryDisplay Parse(byte[] bytes, HistoryConfigId id)
    {
        if (bytes.Length > 8192) throw new InvalidDataException("Repository display exceeds its budget.");
        var value = JsonSerializer.Deserialize(bytes, CloudDisplayJsonContext.Default.CloudRepositoryDisplay)
            ?? throw new InvalidDataException("Repository display is empty.");
        if (value.SchemaVersion != 1 || !id.Matches(value.ConfigId) || string.IsNullOrWhiteSpace(value.ProjectName)
            || value.ProjectName.Length > 256 || value.ProjectName.Any(char.IsControl) || !Guid.TryParse(value.LatestVersionId, out _)
            || value.LatestVersionUtc == default || value.PublishedUtc == default)
            throw new InvalidDataException("Repository display is invalid.");
        return value;
    }
    internal static async Task<CloudRepositoryDisplay?> ReadAsync(RcloneExecutionContext context, HistoryConfigId id, CancellationToken token)
    {
        try
        {
            return Parse(await RcloneConnectionService.RunBytesAsync(context.CreateStartInfo(["cat", Remote(context, id)]),
                OnboardingOperationBudgets.RemoteBrowse, token, 8192).ConfigureAwait(false), id);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException) { return null; }
    }
    internal static async Task PublishAsync(HistoryRuntime runtime, RcloneExecutionContext context, string name,
        System.Collections.Generic.IEnumerable<RepresentationId> representations, CancellationToken token)
    {
        SourceVersion? latest = null;
        foreach (var id in representations)
            if (await runtime.Query.GetRepresentationAsync(id, token).ConfigureAwait(false) is { } representation
                && await runtime.Query.GetVersionAsync(representation.VersionId, token).ConfigureAwait(false) is { } version
                && (latest is null || version.CreatedAtUtc > latest.CreatedAtUtc)) latest = version;
        if (latest is null) return;
        var cleanName = new string(name.Where(c => !char.IsControl(c)).Take(256).ToArray()).Trim();
        if (cleanName.Length == 0) cleanName = runtime.ConfigId.Value;
        var display = new CloudRepositoryDisplay(1, runtime.ConfigId.Value, cleanName, latest.VersionId.ToString(), latest.CreatedAtUtc, DateTimeOffset.UtcNow);
        var previous = await ReadAsync(context, runtime.ConfigId, token).ConfigureAwait(false);
        if (previous is not null && previous.LatestVersionUtc > display.LatestVersionUtc)
            display = display with { LatestVersionId = previous.LatestVersionId, LatestVersionUtc = previous.LatestVersionUtc };
        var temporary = Path.Combine(context.TaskDirectory, "display-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(display, CloudDisplayJsonContext.Default.CloudRepositoryDisplay), token).ConfigureAwait(false);
            context.TargetGuard?.Invoke();
            await RcloneConnectionService.RunAsync(context.CreateStartInfo(["copyto", temporary, Remote(context, runtime.ConfigId)]),
                OnboardingOperationBudgets.RemoteBrowse, token).ConfigureAwait(false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

[JsonSerializable(typeof(CloudRepositoryDisplay))]
internal partial class CloudDisplayJsonContext : JsonSerializerContext;

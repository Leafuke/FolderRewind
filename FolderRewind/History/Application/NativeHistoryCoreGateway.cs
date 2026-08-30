using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.Migration;
using FolderRewind.History.Legacy;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public static class NativeHistoryCoreGateway
{
    private static readonly HistoryRuntimeManager Runtimes = new();
    private static readonly ConcurrentDictionary<string, string> Failed = new(StringComparer.Ordinal);

    public static async Task InitializeAsync(AppConfig appConfig, string configDirectory, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LegacyHistoryRecord>? legacy = null;
        foreach (var config in appConfig.BackupConfigs.Where(item => item is not null))
        {
            try
            {
                if (config.HistoryRepositoryBinding is null)
                    legacy ??= LegacyHistoryReader.Read(Path.Combine(configDirectory, "history.json"));
                _ = await EnsureReadyAsync(config, configDirectory, legacy, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // EnsureReadyAsync records a per-configuration diagnostic. A failed legacy
                // migration must not prevent unrelated configurations from starting.
            }
        }
    }

    public static Task<HistoryRuntime> EnsureReadyAsync(
        BackupConfig config,
        CancellationToken cancellationToken = default)
        => EnsureReadyAsync(config, ConfigService.ConfigDirectory, legacy: null, cancellationToken);

    public static void EnsureReady(string configId)
    {
        var id = new HistoryConfigId(configId);
        if (Runtimes.TryGet(id, out _)) return;
        throw new InvalidOperationException(
            "Native History is not ready for this configuration. " + Failed.GetValueOrDefault(id.Value, "Initialization has not completed."));
    }

    public static HistoryRuntime GetRequiredRuntime(string configId)
    {
        var id = new HistoryConfigId(configId);
        if (Runtimes.TryGet(id, out var runtime) && runtime is not null) return runtime;
        EnsureReady(configId);
        throw new InvalidOperationException("Native History runtime lookup failed after readiness validation.");
    }

    public static bool TryGetRuntime(HistoryConfigId configId, out HistoryRuntime? runtime)
        => Runtimes.TryGet(configId, out runtime);

    public static async Task DetachActiveConfigAsync(
        string configId,
        CancellationToken cancellationToken = default)
    {
        var id = new HistoryConfigId(configId);
        Failed.TryRemove(id.Value, out _);
        var runtime = await Runtimes.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
        if (runtime is null)
            return;

        await using (await runtime.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            // Removing an active configuration only detaches its in-process runtime.
            // Repository packs, payloads, local replica state, and credentials stay on disk.
        }
        await runtime.DisposeAsync().ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<string>> ListBackupFilesAsync(
        string configId,
        SourceId sourceId,
        CancellationToken cancellationToken = default)
        => (await new HistoryPresentationQueryService(GetRequiredRuntime(configId))
                .QueryAsync(sourceId, cancellationToken: cancellationToken).ConfigureAwait(false))
            .Timeline.Select(item => item.FileName).Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static async Task<bool> SetVersionPinByFileAsync(
        string configId,
        SourceId sourceId,
        string fileName,
        bool pinned,
        CancellationToken cancellationToken = default)
    {
        var runtime = GetRequiredRuntime(configId);
        var snapshot = await new HistoryPresentationQueryService(runtime)
            .QueryAsync(sourceId, includeSuppressed: true, cancellationToken).ConfigureAwait(false);
        var match = snapshot.Timeline.FirstOrDefault(item =>
            StringComparer.OrdinalIgnoreCase.Equals(item.FileName, fileName));
        if (match is null) return false;
        await runtime.Annotations.SetPinAsync(
            new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, match.VersionId.Value),
            pinned,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    public static async Task<TimelineEntrySummary?> FindVersionByFileAsync(
        string configId,
        SourceId sourceId,
        string fileName,
        CancellationToken cancellationToken = default)
        => (await new HistoryPresentationQueryService(GetRequiredRuntime(configId))
                .QueryAsync(sourceId, includeSuppressed: true, cancellationToken).ConfigureAwait(false))
            .Timeline.FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.FileName, fileName));

    public static async Task<HistoryCommitBatch> CommitBackupAsync(
        BackupConfig config,
        IEnumerable<SourceCaptureResult> results,
        BackupInvocationKind kind,
        DateTimeOffset startedAtUtc,
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        var runtime = GetRequiredRuntime(config.Id);
        var workspace = (await runtime.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Value;
        long revision = workspace?.StateRevision ?? -1;
        var baselines = workspace?.SourceBaselines.ToDictionary(item => item.SourceId) ?? [];
        var normalized = new List<SourceCaptureResult>();
        foreach (var result in results)
        {
            baselines.TryGetValue(result.SourceId, out var baseline);
            var tips = baseline?.BaseVersionId is { } baseId
                ? await runtime.Query.GetMaterializationPolicyTipsAsync(baseId, cancellationToken).ConfigureAwait(false)
                : [];
            normalized.Add(new SourceCaptureResult(
                result.SourceId, result.Outcome, result.CaptureScope, result.StateFingerprint,
                result.ExistingVersionId, result.RepresentationCandidate, result.LocalReplicaCandidate,
                result.PayloadCandidate, revision, result.ExpectedBaseVersionId ?? baseline?.BaseVersionId, result.CleanupHandle,
                result.Diagnostics, tips.Select(item => item.UpdateId), result.BaselineCandidate));
        }
        var snapshot = new HistoryConfigSnapshot(
            runtime.ConfigId,
            config.SourceFolders.Select(folder => new HistoryConfigSourceSnapshot(
                Source(folder), new SourceDescriptorSnapshot(folder.DisplayName, folder.Path))));
        var committed = await runtime.Commit.CommitAsync(new HistoryCommitRequest(
            snapshot,
            new HistoryBackupInvocation(
                RunId.New(), startedAtUtc, DateTimeOffset.UtcNow, kind, HistoryProvenance.Native("app"), comment ?? string.Empty),
            workspace,
            normalized), cancellationToken).ConfigureAwait(false);
        foreach (var result in normalized.Where(item => item.BaselineCandidate is not null))
        {
            var version = committed.NewVersions.Single(item => item.SourceId == result.SourceId);
            var representation = committed.NewRepresentations.Single(item => item.VersionId == version.VersionId);
            try
            {
                await runtime.CaptureBaselines.SaveAsync(
                    result.SourceId,
                    result.BaselineCandidate!,
                    version.VersionId,
                    representation,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogService.LogWarning(
                    $"Native History committed, but the disposable capture baseline cache was not updated: {ex.Message}",
                    nameof(NativeHistoryCoreGateway));
            }
        }
        return committed;
    }

    public static async Task<SourceCaptureBaseline?> LoadCaptureBaselineAsync(
        string configId,
        SourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        var runtime = GetRequiredRuntime(configId);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var baseline = await runtime.CaptureBaselines.LoadAsync(sourceId, cancellationToken).ConfigureAwait(false);
            if (baseline is null) return null;
            var workspaceLoad = await runtime.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (workspaceLoad.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible)
                throw new InvalidOperationException($"Workspace recovery is required: {workspaceLoad.Diagnostic}");
            if (SourceCaptureBaselinePolicy.IsApplicableToWorkspace(baseline, workspaceLoad.Value))
            {
                return baseline;
            }

            if (await runtime.CaptureBaselines.RemoveAsync(
                    sourceId,
                    baseline.Revision,
                    cancellationToken).ConfigureAwait(false))
            {
                LogService.LogWarning(
                    $"Discarded stale capture baseline for Source '{sourceId}' because it no longer matches the active Workspace.",
                    nameof(NativeHistoryCoreGateway));
                return null;
            }
        }
        return null;
    }

    private static async Task<HistoryRuntime> EnsureReadyAsync(
        BackupConfig config,
        string configDirectory,
        IReadOnlyList<LegacyHistoryRecord>? legacy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var configId = new HistoryConfigId(config.Id);
        try
        {
            var runtime = await Runtimes.GetOrCreateAsync(
                configId,
                (_, token) => OpenRepositoryAsync(config, configDirectory, legacy, token),
                cancellationToken).ConfigureAwait(false);
            Failed.TryRemove(configId.Value, out _);
            return runtime;
        }
        catch (Exception ex)
        {
            Failed[configId.Value] = ex.Message;
            LogService.LogError(
                $"Native History initialization failed for config '{configId.Value}': {ex.Message}",
                nameof(NativeHistoryCoreGateway),
                ex);
            throw;
        }
    }

    private static async Task<FileHistoryRepository> OpenRepositoryAsync(
        BackupConfig config,
        string configDirectory,
        IReadOnlyList<LegacyHistoryRecord>? legacy,
        CancellationToken cancellationToken)
    {
        var configId = new HistoryConfigId(config.Id);
        var configLegacy = config.HistoryRepositoryBinding is null
            ? (legacy ?? LegacyHistoryReader.Read(Path.Combine(configDirectory, "history.json")))
                .Where(item => !string.IsNullOrWhiteSpace(item.ConfigId)
                    && new HistoryConfigId(item.ConfigId) == configId)
                .ToArray()
            : [];
        if (config.HistoryRepositoryBinding is null && configLegacy.Length > 0)
        {
            var sources = config.SourceFolders.Select(folder => new LegacyMigrationSourceSnapshot(
                Source(folder), folder.Path, folder.DisplayName,
                Path.Combine(config.DestinationPath, folder.DisplayName))).ToArray();
            var entries = configLegacy.Select(item => new LegacyHistoryEntrySnapshot(
                ResolveSource(config, item), item.FolderPath, item.FolderName, item.FileName,
                item.Timestamp, item.BackupType, item.Comment, item.IsImportant, item.IsPartialBackup,
                item.IsCloudArchived, SafeLegacyCloudLocator(item))).ToArray();
            var migration = await new LegacyHistoryMigrationService().MigrateAsync(
                new LegacyHistoryMigrationInput(
                    configDirectory,
                    configId,
                    sources,
                    entries,
                    LegacySmartMetadataReader.Read(config)),
                (version, _) => PersistBinding(config, version),
                cancellationToken).ConfigureAwait(false);
            if (!migration.IsReady || migration.Repository is null)
                throw new InvalidOperationException(migration.Diagnostic);
            return migration.Repository;
        }

        var binding = await new HistoryRepositoryBindingService(configDirectory).EnsureAsync(
            config,
            _ => PersistConfig(),
            cancellationToken).ConfigureAwait(false);
        if (!binding.IsReady || binding.Repository is null)
            throw new InvalidOperationException(binding.Diagnostic);
        return binding.Repository;
    }

    private static SourceId ResolveSource(BackupConfig config, LegacyHistoryRecord item)
    {
        var folder = item.FolderId is { } id
            ? config.SourceFolders.FirstOrDefault(value => Guid.TryParse(value.Id, out var parsed) && parsed == id)
            : config.SourceFolders.FirstOrDefault(value => StringComparer.OrdinalIgnoreCase.Equals(value.Path, item.FolderPath));
        return folder is null ? throw new InvalidDataException("Legacy history Source cannot be mapped deterministically.") : Source(folder);
    }

    private static SourceId Source(ManagedFolder folder)
        => Guid.TryParse(folder.Id, out var id) && id != Guid.Empty
            ? new SourceId(id)
            : throw new InvalidDataException("ManagedFolder has no stable SourceId.");

    private static string SafeLegacyCloudLocator(LegacyHistoryRecord item)
    {
        var value = (item.CloudArchiveRemotePath ?? string.Empty).Replace('\\', '/').Trim('/');
        return HistoryRepositoryPaths.IsSafeRepositoryRelativePath(value) ? value : string.Empty;
    }

    private static Task PersistBinding(BackupConfig config, int version)
    {
        config.HistoryRepositoryBinding = new HistoryRepositoryBinding { FormatVersion = version };
        return PersistConfig();
    }

    private static Task PersistConfig()
    {
        var result = ConfigService.SaveWithResult();
        if (!result.Success) throw result.Exception ?? new IOException(result.ErrorMessage);
        return Task.CompletedTask;
    }
}

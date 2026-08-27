using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.Models;
using FolderRewind.History.Legacy;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Services.Plugins.V3;

internal sealed record PluginV3ArtifactCommitResult(
    OperationOutcome Outcome,
    ArtifactId RootArtifactId,
    ArtifactGraphRevision GraphRevision,
    IReadOnlyList<PluginDiagnostic> Diagnostics);

internal static class PluginV3ArtifactService
{
    public static async ValueTask<OperationOutcome> ObserveCompletionAsync(
        string backupRunId,
        BackupConfig config,
        ManagedFolder folder,
        HistoryItem history,
        PluginV3ArtifactCommitResult commit,
        CancellationToken cancellationToken = default)
    {
        var observers = PluginV3RuntimeService.GetCompletionObservers();
        if (observers.Count == 0) return commit.Outcome;
        var configSnapshot = PluginV3ModelMapper.ToSnapshot(config);
        var folderId = Guid.Parse(folder.Id);
        var folderSnapshot = configSnapshot.Folders.Single(value => value.FolderId == folderId);
        var observed = await new BackupCompletionObserverCoordinator(PluginV3RuntimeService.Runtime)
            .ObserveAsync(
                new BackupCompletionSnapshot(
                    backupRunId,
                    configSnapshot,
                    folderSnapshot,
                    history.Id,
                    commit.RootArtifactId,
                    commit.GraphRevision,
                    commit.Outcome,
                    commit.Diagnostics,
                    CloudQueueCommitted: true),
                observers,
                cancellationToken)
            .ConfigureAwait(false);
        LegacyHistoryCapturePersistenceAdapter.ApplyOperationResult(
            history.Id,
            PluginV3ModelMapper.ToPersisted(observed.Outcome),
            observed.Diagnostics.Select(PluginV3ModelMapper.ToRecord).ToArray());
        return observed.Outcome;
    }

    public static async ValueTask<PluginV3ArtifactCommitResult> CommitBackupAsync(
        BackupConfig config,
        ManagedFolder folder,
        string historyItemId,
        string archivePath,
        string archiveFileName,
        bool isPartial,
        IReadOnlyList<PluginDiagnostic> operationDiagnostics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(folder);
        var destination = Path.GetFullPath(config.DestinationPath);
        var relativePath = Path.GetRelativePath(destination, Path.GetFullPath(archivePath)).Replace('\\', '/');
        if (relativePath.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("The generated Core Artifact is outside the configured destination.");
        }

        var store = new FileArtifactLedgerStore(destination);
        await store.RecoverAsync(cancellationToken).ConfigureAwait(false);
        var ledger = await store.RegisterCoreArtifactAsync(
            config.Id,
            Guid.Parse(folder.Id),
            historyItemId,
            relativePath,
            isPartial ? ArtifactCompleteness.Partial : ArtifactCompleteness.Complete,
            PluginV3ModelMapper.ToCaptureMode(config.Archive.Mode, archiveFileName),
            "capture-" + Guid.NewGuid().ToString("N"),
            cancellationToken).ConfigureAwait(false);
        var diagnostics = operationDiagnostics.ToList();
        var outcome = diagnostics.Any(value => value.Severity == DiagnosticSeverity.Warning)
            ? OperationOutcome.SuccessWithWarnings
            : OperationOutcome.Success;

        var policySettings = config.ArtifactTransformPolicy;
        if (policySettings is not null
            && !string.IsNullOrWhiteSpace(policySettings.Transformer?.PluginId)
            && !string.IsNullOrWhiteSpace(policySettings.Transformer.TransformerId))
        {
            var pluginId = new PluginId(policySettings.Transformer.PluginId);
            var policy = new ArtifactTransformPolicy(
                new ArtifactTransformerId(pluginId, policySettings.Transformer.TransformerId),
                policySettings.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
                policySettings.FailureBehavior == PersistedArtifactTransformFailureBehavior.RequireTransform
                    ? ArtifactTransformFailureBehavior.RequireTransform
                    : ArtifactTransformFailureBehavior.KeepPrimaryWithWarnings);
            var configSnapshot = PluginV3ModelMapper.ToSnapshot(config);
            var folderId = Guid.Parse(folder.Id);
            var folderSnapshot = configSnapshot.Folders.Single(value => value.FolderId == folderId);
            var compatible = LegacyHistoryCapturePersistenceAdapter.GetEntries(config, folder)
                .Where(item => !item.IsPartialBackup && item.ArtifactRootId.HasValue)
                .Select(item => item.Id)
                .ToArray();
            try
            {
                var transformed = await new ArtifactTransformCoordinator(PluginV3RuntimeService.Runtime, store)
                    .TransformAsync(
                        pluginId,
                        configSnapshot,
                        folderSnapshot,
                        historyItemId,
                        policy,
                        compatible,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                ledger = transformed.Ledger;
                diagnostics.AddRange(transformed.Diagnostics);
                outcome = Combine(outcome, transformed.Outcome);
                if (policy.FailureBehavior == ArtifactTransformFailureBehavior.RequireTransform
                    && !transformed.GraphCommitted)
                {
                    throw new InvalidOperationException("The required Artifact transform did not commit.");
                }
            }
            catch
            {
                if (policy.FailureBehavior == ArtifactTransformFailureBehavior.RequireTransform)
                {
                    await AbortAsync(store, historyItemId).ConfigureAwait(false);
                }
                throw;
            }
        }

        var root = ledger.HistoryRoots.Single(value =>
            string.Equals(value.HistoryItemId, historyItemId, StringComparison.Ordinal));
        LegacyHistoryCapturePersistenceAdapter.ApplyArtifactRoots(
            ledger.HistoryRoots.ToDictionary(value => value.HistoryItemId, value => value.RootArtifactId.Value, StringComparer.Ordinal),
            ledger.Revision.Value);
        return new PluginV3ArtifactCommitResult(outcome, root.RootArtifactId, ledger.Revision, diagnostics);
    }

    private static async ValueTask AbortAsync(
        FileArtifactLedgerStore store,
        string historyItemId)
    {
        try
        {
            await store.RemoveHistoryRootAsync(
                historyItemId,
                new ArtifactGraphRevision(Guid.NewGuid().ToString("N")),
                CancellationToken.None).ConfigureAwait(false);
            await store.GarbageCollectUnreachableAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Recovery will reconcile an uncommitted Host history root on next startup.
        }
    }

    private static OperationOutcome Combine(OperationOutcome current, OperationOutcome next)
    {
        if (next is OperationOutcome.Failed or OperationOutcome.Blocked or OperationOutcome.Canceled) return next;
        if (current == OperationOutcome.SuccessWithWarnings || next == OperationOutcome.SuccessWithWarnings)
            return OperationOutcome.SuccessWithWarnings;
        return current;
    }
}

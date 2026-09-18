using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed class HistoryRestoreService
{
    private readonly HistoryRuntime _history;
    private readonly RepresentationRuntime _representations;
    private readonly Func<CancellationToken, Task<IRepresentationEnvironment>> _environmentFactory;
    private readonly IHistoryRestoreMutationBackend _mutation;
    private readonly HistoryRestoreTransactionJournalStore _journals;
    private readonly Func<HistoryRestoreSourceBinding, string, CancellationToken, Task<bool>>? _prepareRestore;
    private readonly Func<CancellationToken, ValueTask<IAsyncDisposable>>? _finalGuard;

    public HistoryRestoreService(
        HistoryRuntime history,
        RepresentationRuntime representations,
        Func<CancellationToken, Task<IRepresentationEnvironment>> environmentFactory,
        IHistoryRestoreMutationBackend mutation,
        Func<HistoryRestoreSourceBinding, string, CancellationToken, Task<bool>>? prepareRestore = null,
        Func<CancellationToken, ValueTask<IAsyncDisposable>>? finalGuard = null)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _representations = representations ?? throw new ArgumentNullException(nameof(representations));
        _environmentFactory = environmentFactory ?? throw new ArgumentNullException(nameof(environmentFactory));
        _mutation = mutation ?? throw new ArgumentNullException(nameof(mutation));
        _journals = new HistoryRestoreTransactionJournalStore(history, mutation);
        _prepareRestore = prepareRestore;
        _finalGuard = finalGuard;
    }

    public async Task RecoverIncompleteAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await _history.MutationGate.EnterForRecoveryAsync(cancellationToken).ConfigureAwait(false);
        if (await _journals.RecoverIncompleteAsync(cancellationToken).ConfigureAwait(false))
        {
            await _history.RefreshLocalStateHealthAsync(cancellationToken).ConfigureAwait(false);
            _history.ChangeFeed.Publish(_history.ConfigId, HistoryChangeKind.LocalStateChanged);
        }
    }

    public async Task<HistoryRestoreResult> RestoreCheckpointAsync(
        CheckpointId checkpointId,
        IReadOnlyList<HistoryRestoreSourceBinding> mappedSources,
        HistoryWorkspace expectedWorkspace,
        HistoryCheckpointRestoreScope scope,
        HistoryRestoreApplyMode requestedMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mappedSources);
        ArgumentNullException.ThrowIfNull(expectedWorkspace);
        await RecoverIncompleteAsync(cancellationToken).ConfigureAwait(false);
        var checkpoint = await _history.Query.GetCheckpointAsync(checkpointId, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
            return Blocked("Checkpoint does not exist.");

        var duplicateSource = mappedSources
            .GroupBy(item => item.SourceId)
            .FirstOrDefault(group => group.Count() > 1);
        if (mappedSources.Count == 0 || duplicateSource is not null
            || mappedSources.Select(item => Path.GetFullPath(item.TargetDirectory))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != mappedSources.Count)
        {
            return Blocked("Checkpoint restore mappings must contain unique Sources and target directories.");
        }

        var restorable = checkpoint.Sources.Where(item => item.VersionId is not null).ToArray();
        var mappedIds = mappedSources.Select(item => item.SourceId).ToHashSet();
        if (mappedIds.Any(id => checkpoint.Sources.All(item => item.SourceId != id)))
            return Blocked("A mapped Source does not belong to the Checkpoint.");
        if (scope == HistoryCheckpointRestoreScope.CompleteCheckpoint
            && (!checkpoint.IsStructurallyComplete
                || !mappedIds.SetEquals(checkpoint.Sources.Select(item => item.SourceId))))
        {
            return Blocked("Complete Checkpoint restore requires an available mapping for every Source.");
        }
        if (scope == HistoryCheckpointRestoreScope.AvailableMappedSources
            && mappedIds.Any(id => restorable.All(item => item.SourceId != id)))
        {
            return Blocked("A mapped Checkpoint Source has no restorable Version.");
        }

        var prepared = new List<PreparedRestoreSource>();
        try
        {
            foreach (var binding in mappedSources)
            {
                var checkpointSource = checkpoint.Sources.Single(item => item.SourceId == binding.SourceId);
                if (checkpointSource.VersionId is null)
                    throw new InvalidOperationException("A mapped Checkpoint Source has no restorable Version.");
                var version = await _history.Query.GetVersionAsync(
                    checkpointSource.VersionId.Value,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Checkpoint SourceVersion is missing.");
                var effectiveMode = requestedMode;
                prepared.Add(await PrepareSourceAsync(
                    version,
                    binding,
                    RequiredFidelity(effectiveMode),
                    effectiveMode,
                    cancellationToken).ConfigureAwait(false));
            }
        }
        catch (Exception ex)
        {
            HistoryRestoreTransactionJournalStore.CleanupStaging(prepared.Select(item => item.StagingDirectory));
            return Blocked(ex.Message);
        }

        try
        {
            await using var guard = await EnterFinalGuardAsync(cancellationToken).ConfigureAwait(false);
            await using var lease = await _history.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            var current = await RequireExpectedWorkspaceAsync(expectedWorkspace, cancellationToken).ConfigureAwait(false);
            for (int i = 0; i < prepared.Count; i++)
                prepared[i] = await PrepareOrdinaryRestoreAsync(prepared[i], cancellationToken).ConfigureAwait(false);
            var restoredIds = prepared.Select(item => item.Binding.SourceId).ToHashSet();
            var baselines = current.SourceBaselines
                .Where(item => !restoredIds.Contains(item.SourceId))
                .Concat(prepared.Select(item => new WorkspaceSourceBaseline(
                    item.Binding.SourceId,
                    item.Version.VersionId,
                    item.Fidelity == MaterializationFidelity.Exact
                        && item.ApplyMode == HistoryRestoreApplyMode.Clean
                        ? WorkspaceBaselineRelation.Exact
                        : WorkspaceBaselineRelation.Derived)));
            var desired = new HistoryWorkspace(
                _history.ConfigId,
                checked(current.StateRevision + 1),
                current.ActiveBranchId,
                current.ActiveBranchUpdateId,
                baselines,
                current.CheckpointAncestryAnchorId);
            return await ExecuteMutationAsync(prepared, current, desired, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            HistoryRestoreTransactionJournalStore.CleanupStaging(prepared.Select(item => item.StagingDirectory));
            return Blocked(ex.Message);
        }
    }

    public async Task<HistoryRestoreResult> RestoreVersionAsync(
        VersionId versionId,
        HistoryRestoreSourceBinding source,
        HistoryWorkspace expectedWorkspace,
        HistoryRestoreApplyMode requestedMode,
        CancellationToken cancellationToken = default)
    {
        await RecoverIncompleteAsync(cancellationToken).ConfigureAwait(false);
        var version = await _history.Query.GetVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        if (version is null || version.SourceId != source.SourceId)
            return Blocked("Version does not exist or belongs to another Source.");

        PreparedRestoreSource prepared;
        try
        {
            var effectiveMode = requestedMode;
            prepared = await PrepareSourceAsync(
                version,
                source,
                RequiredFidelity(effectiveMode),
                effectiveMode,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Blocked(ex.Message);
        }

        try
        {
            await using var guard = await EnterFinalGuardAsync(cancellationToken).ConfigureAwait(false);
            await using var lease = await _history.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            var current = await RequireExpectedWorkspaceAsync(expectedWorkspace, cancellationToken).ConfigureAwait(false);
            prepared = await PrepareOrdinaryRestoreAsync(prepared, cancellationToken).ConfigureAwait(false);
            var relation = prepared.Fidelity == MaterializationFidelity.Exact
                && prepared.ApplyMode == HistoryRestoreApplyMode.Clean
                ? WorkspaceBaselineRelation.Exact
                : WorkspaceBaselineRelation.Derived;
            var baselines = current.SourceBaselines
                .Where(item => item.SourceId != source.SourceId)
                .Append(new WorkspaceSourceBaseline(source.SourceId, versionId, relation));
            var desired = new HistoryWorkspace(
                _history.ConfigId,
                checked(current.StateRevision + 1),
                current.ActiveBranchId,
                current.ActiveBranchUpdateId,
                baselines,
                current.CheckpointAncestryAnchorId);
            return await ExecuteMutationAsync(
                [prepared], current, desired, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            HistoryRestoreTransactionJournalStore.CleanupStaging([prepared.StagingDirectory]);
            return Blocked(ex.Message);
        }
    }

    internal async Task<PreparedRestoreSource> PrepareSourceAsync(
        SourceVersion version,
        HistoryRestoreSourceBinding source,
        MaterializationFidelity requiredFidelity,
        HistoryRestoreApplyMode applyMode,
        CancellationToken cancellationToken)
    {
        var allRepresentations = await _history.Query.GetAllRepresentationsAsync(cancellationToken).ConfigureAwait(false);
        var environment = await _environmentFactory(cancellationToken).ConfigureAwait(false);
        var assessment = await _representations.AssessVersionAsync(
            version.VersionId,
            allRepresentations,
            environment,
            AssessmentDepth.Deep,
            requiredFidelity,
            cancellationToken).ConfigureAwait(false);
        if (assessment.Readiness != HistoryReadiness.Ready || assessment.Selected is null)
            throw new InvalidOperationException($"Version {version.VersionId} is not Ready with {requiredFidelity} fidelity.");

        var staging = Path.Combine(
            _history.Repository.Paths.TransactionsRoot,
            "restore-staging",
            Guid.NewGuid().ToString("N"));
        try
        {
            await _representations.MaterializeAsync(
                assessment.Selected.RepresentationId,
                allRepresentations,
                environment,
                requiredFidelity,
                staging,
                cancellationToken).ConfigureAwait(false);
            return new PreparedRestoreSource(
                source with { EffectiveSourceBoundary = version.EffectiveSourceBoundary },
                version,
                assessment.Selected.Fidelity,
                applyMode,
                staging);
        }
        catch
        {
            HistoryRestoreTransactionJournalStore.CleanupStaging([staging]);
            throw;
        }
    }

    private async Task<PreparedRestoreSource> PrepareOrdinaryRestoreAsync(PreparedRestoreSource source, CancellationToken token)
        => _prepareRestore is not null
            && await _prepareRestore(source.Binding, source.StagingDirectory, token).ConfigureAwait(false)
                ? source with { Fidelity = MaterializationFidelity.Partial } : source;
    internal async ValueTask<IAsyncDisposable?> EnterFinalGuardAsync(CancellationToken token)
        => _finalGuard is null ? null : await _finalGuard(token).ConfigureAwait(false);

    internal async Task EnsureReadyAsync(
        VersionId versionId,
        MaterializationFidelity requiredFidelity,
        CancellationToken cancellationToken)
    {
        var allRepresentations = await _history.Query.GetAllRepresentationsAsync(cancellationToken).ConfigureAwait(false);
        var environment = await _environmentFactory(cancellationToken).ConfigureAwait(false);
        var assessment = await _representations.AssessVersionAsync(
            versionId,
            allRepresentations,
            environment,
            AssessmentDepth.Deep,
            requiredFidelity,
            cancellationToken).ConfigureAwait(false);
        if (assessment.Readiness != HistoryReadiness.Ready || assessment.Selected is null)
            throw new InvalidOperationException($"Version {versionId} is not Ready with {requiredFidelity} fidelity.");
    }

    internal async Task<VersionAssessment> AssessVersionAsync(
        VersionId versionId,
        MaterializationFidelity requiredFidelity,
        AssessmentDepth depth,
        CancellationToken cancellationToken)
    {
        var allRepresentations = await _history.Query.GetAllRepresentationsAsync(cancellationToken).ConfigureAwait(false);
        var environment = await _environmentFactory(cancellationToken).ConfigureAwait(false);
        return await _representations.AssessVersionAsync(
            versionId,
            allRepresentations,
            environment,
            depth,
            requiredFidelity,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<HistoryWorkspace> RequireExpectedWorkspaceAsync(
        HistoryWorkspace expected,
        CancellationToken cancellationToken)
    {
        var load = await _history.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (load.Value is null || !HistoryRestoreTransactionJournalStore.WorkspaceEquals(load.Value, expected))
            throw new InvalidOperationException("Workspace changed before restore apply.");
        return load.Value;
    }

    internal async Task<HistoryRestoreResult> ExecuteMutationAsync(
        IReadOnlyList<PreparedRestoreSource> prepared,
        HistoryWorkspace currentWorkspace,
        HistoryWorkspace desiredWorkspace,
        CancellationToken cancellationToken,
        HistoryCommitPack? commitPack = null,
        LocalReplicaCatalog? desiredCatalog = null,
        long expectedCatalogRevision = -1)
    {
        var transactionId = commitPack?.TransactionId ?? HistoryTransactionId.New();
        var journal = new HistoryRestoreTransactionJournal(
            transactionId,
            HistoryRestoreTransactionPhase.Prepared,
            currentWorkspace,
            desiredWorkspace,
            prepared.Select(item => item.StagingDirectory).ToImmutableArray(),
            [],
            [], [], commitPack is null ? null : new HistoryPackCodec().Encode(commitPack), desiredCatalog, expectedCatalogRevision);
        _journals.Save(journal);
        var snapshots = new List<HistoryRestoreRollbackSnapshot>();
        var applied = new List<SourceId>();
        var readLocks = new List<FileStream>();
        void ReleaseReads() { foreach (var file in readLocks) file.Dispose(); readLocks.Clear(); }
        async Task VerifyOriginalAsync()
        {
            foreach (var item in prepared.Where(p => p.ExpectedOriginalTreeDigest is not null))
            {
                var snapshot = snapshots.Single(s => s.SourceId == item.Binding.SourceId);
                var tree = snapshot.HadOriginalTarget
                    ? await Merge.MergeTreeManifest.ReadAsync(snapshot.RollbackDirectory,
                        FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(item.Binding), cancellationToken).ConfigureAwait(false)
                    : Merge.MergeTreeManifest.Empty;
                if (tree.Digest != item.ExpectedOriginalTreeDigest) throw new IOException("Working files changed after protection and before mutation.");
            }
        }
        try
        {
            foreach (var item in prepared)
                snapshots.Add(_mutation.PlanRollback(item.Binding, transactionId));
            journal = journal with
            {
                Phase = HistoryRestoreTransactionPhase.Mutating,
                RollbackSnapshots = snapshots.ToImmutableArray()
            };
            _journals.Save(journal);
            foreach (var snapshot in snapshots)
            {
                journal = journal with { StartedSources = journal.StartedSources.Add(snapshot.SourceId) };
                _journals.Save(journal);
                await _mutation.PrepareRollbackAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            foreach (var item in prepared.Where(p => p.ExpectedOriginalTreeDigest is not null))
            {
                var snapshot = snapshots.Single(s => s.SourceId == item.Binding.SourceId);
                if (!snapshot.HadOriginalTarget) continue;
                var directories = new Stack<string>(); directories.Push(snapshot.RollbackDirectory);
                while (directories.TryPop(out var directory))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Rollback tree contains a link.");
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Rollback tree contains a link.");
                        if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
                        else readLocks.Add(new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read));
                    }
                }
            }
            await VerifyOriginalAsync().ConfigureAwait(false);
            for (int index = 0; index < prepared.Count; index++)
            {
                var item = prepared[index];
                await _mutation.ApplyAsync(
                    item.Binding,
                    item.StagingDirectory,
                    item.ApplyMode,
                    snapshots[index],
                    cancellationToken).ConfigureAwait(false);
                applied.Add(item.Binding.SourceId);
                journal = journal with { AppliedSources = applied.ToImmutableArray() };
                _journals.Save(journal);
            }

            await VerifyOriginalAsync().ConfigureAwait(false);
            foreach (var item in prepared.Where(p => p.ExpectedResultTreeDigest is not null))
            {
                var snapshot = snapshots.Single(s => s.SourceId == item.Binding.SourceId);
                var boundary = FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(item.Binding);
                bool IncludeResult(string path) => (snapshot.NewTargetOwnershipMarker is null
                        || !StringComparer.Ordinal.Equals(path.Replace('\\', '/'), snapshot.NewTargetOwnershipMarker))
                    && boundary(path);
                if ((await Merge.MergeTreeManifest.ReadAsync(item.Binding.TargetDirectory,
                    IncludeResult, cancellationToken).ConfigureAwait(false)).Digest != item.ExpectedResultTreeDigest)
                    throw new IOException("Applied Merge state differs from the verified result.");
            }
            if (commitPack is not null)
                await _history.Repository.CommitAsync(commitPack, cancellationToken: cancellationToken).ConfigureAwait(false);
            journal = journal with { Phase = HistoryRestoreTransactionPhase.WorkspaceApplying };
            _journals.Save(journal);
            await _journals.CompleteLocalStateAsync(journal, commitPack is null ? cancellationToken : CancellationToken.None).ConfigureAwait(false);
            journal = journal with { Phase = HistoryRestoreTransactionPhase.WorkspaceApplied };
            _journals.Save(journal);
            ReleaseReads();
            return await CompleteCommittedAsync(journal, snapshots, applied).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReleaseReads();
            if (commitPack is not null)
            {
                try
                {
                    if (_journals.IsPackCommitted(journal))
                    {
                        await _journals.CompleteLocalStateAsync(journal, CancellationToken.None).ConfigureAwait(false);
                        return await CompleteCommittedAsync(journal, snapshots, applied).ConfigureAwait(false);
                    }
                }
                catch (Exception recovery)
                {
                    return new(HistoryRestoreStatus.CommittedRecoveryRequired,
                        $"Durable Merge requires local recovery: {recovery.Message}", false, applied.ToImmutableArray());
                }
            }
            var workspaceLoad = await _history.WorkspaceStore.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            if (workspaceLoad.Value is not null
                && HistoryRestoreTransactionJournalStore.WorkspaceEquals(workspaceLoad.Value, desiredWorkspace))
            {
                var committed = await CompleteCommittedAsync(journal, snapshots, applied).ConfigureAwait(false);
                return committed with
                {
                    Status = HistoryRestoreStatus.CommittedWithPostActionWarning,
                    Diagnostic = string.IsNullOrWhiteSpace(committed.Diagnostic)
                        ? $"Restore committed; post-commit recovery handled: {ex.Message}"
                        : committed.Diagnostic
                };
            }

            bool rollbackFailed = false;
            foreach (var snapshot in snapshots.Where(s => journal.StartedSources.Contains(s.SourceId)).Reverse())
            {
                try { await _mutation.RollbackAsync(snapshot, CancellationToken.None).ConfigureAwait(false); }
                catch { rollbackFailed = true; }
            }
            HistoryRestoreTransactionJournalStore.CleanupStaging(journal.StagingDirectories);
            if (!rollbackFailed)
                _journals.Save(journal with { Phase = HistoryRestoreTransactionPhase.Complete });
            return new(
                rollbackFailed
                    ? HistoryRestoreStatus.MutationFailedRecoveryRequired
                    : HistoryRestoreStatus.MutationFailedRolledBack,
                ex.Message,
                false,
                applied.ToImmutableArray());
        }
    }

    private async Task<HistoryRestoreResult> CompleteCommittedAsync(
        HistoryRestoreTransactionJournal journal,
        IReadOnlyList<HistoryRestoreRollbackSnapshot> snapshots,
        IReadOnlyList<SourceId> applied)
    {
        try
        {
            foreach (var snapshot in snapshots)
                await _mutation.CommitAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
            HistoryRestoreTransactionJournalStore.CleanupStaging(journal.StagingDirectories);
            _journals.Save(journal with { Phase = HistoryRestoreTransactionPhase.Complete });
        }
        catch (Exception ex)
        {
            // The Workspace is already durable. Keep the journal incomplete so startup recovery
            // can retry idempotent snapshot cleanup without reverting committed Source data.
            HistoryRestoreTransactionJournalStore.CleanupStaging(journal.StagingDirectories);
            return new(
                HistoryRestoreStatus.CommittedWithPostActionWarning,
                $"Restore committed; rollback snapshot cleanup is deferred: {ex.Message}",
                true,
                applied.ToImmutableArray());
        }

        try
        {
            await _history.EnsureIndexCurrentAsync(CancellationToken.None).ConfigureAwait(false);
            await _history.RefreshLocalStateHealthAsync(CancellationToken.None).ConfigureAwait(false);
            _history.ChangeFeed.Publish(_history.ConfigId, HistoryChangeKind.LocalStateChanged);
            return new(HistoryRestoreStatus.Committed, string.Empty, true, applied.ToImmutableArray());
        }
        catch (Exception ex)
        {
            return new(
                HistoryRestoreStatus.CommittedWithPostActionWarning,
                $"Restore committed; local health refresh is deferred: {ex.Message}",
                true,
                applied.ToImmutableArray());
        }
    }

    private static HistoryRestoreResult Blocked(string diagnostic)
        => new(HistoryRestoreStatus.BlockedBeforeMutation, diagnostic, false, []);

    private static MaterializationFidelity RequiredFidelity(HistoryRestoreApplyMode applyMode)
        => applyMode == HistoryRestoreApplyMode.Clean
            ? MaterializationFidelity.Exact
            : MaterializationFidelity.Partial;

    internal sealed record PreparedRestoreSource(
        HistoryRestoreSourceBinding Binding,
        SourceVersion Version,
        MaterializationFidelity Fidelity,
        HistoryRestoreApplyMode ApplyMode,
        string StagingDirectory, string? ExpectedOriginalTreeDigest = null, string? ExpectedResultTreeDigest = null);
}

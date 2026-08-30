using FolderRewind.History.Application;
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

namespace FolderRewind.History.Retention;

public sealed class HistoryRetentionExecutor
{
    private readonly HistoryRuntime _history;
    private readonly HistoryRetentionPlanner _planner;
    private readonly RepresentationRuntime _representations;
    private readonly Func<CancellationToken, Task<IRepresentationEnvironment>> _environmentFactory;
    private readonly IHistoryCompactionBackend _compaction;
    private readonly IHistoryLocalPayloadStore _payloads;
    private readonly IHistoryArtifactGarbageCollector _artifactGarbageCollector;
    private readonly HistoryPackCodec _codec;

    public HistoryRetentionExecutor(
        HistoryRuntime history,
        HistoryRetentionPlanner planner,
        RepresentationRuntime representations,
        Func<CancellationToken, Task<IRepresentationEnvironment>> environmentFactory,
        IHistoryCompactionBackend compaction,
        IHistoryLocalPayloadStore payloads,
        IHistoryArtifactGarbageCollector artifactGarbageCollector,
        HistoryPackCodec? codec = null)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _representations = representations ?? throw new ArgumentNullException(nameof(representations));
        _environmentFactory = environmentFactory ?? throw new ArgumentNullException(nameof(environmentFactory));
        _compaction = compaction ?? throw new ArgumentNullException(nameof(compaction));
        _payloads = payloads ?? throw new ArgumentNullException(nameof(payloads));
        _artifactGarbageCollector = artifactGarbageCollector
            ?? throw new ArgumentNullException(nameof(artifactGarbageCollector));
        _codec = codec ?? new HistoryPackCodec();
    }

    public async Task<HistoryRetentionExecutionResult> ExecuteAsync(
        HistoryRetentionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanExecute)
            return Result(HistoryRetentionExecutionStatus.Blocked, string.Join(" ", plan.Blockers));

        await using var lease = await _history.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        var current = await _planner.PlanAsync(
            plan.Request,
            ImmutableDictionary<VersionId, RepresentationId>.Empty,
            cancellationToken).ConfigureAwait(false);
        if (!current.CanExecute || !StringComparer.Ordinal.Equals(current.StateFingerprint, plan.StateFingerprint))
            return Result(HistoryRetentionExecutionStatus.StalePlan, "Retention protection roots or policy tips changed after planning.");

        var replacementIds = new List<RepresentationId>();
        var preferred = new Dictionary<VersionId, RepresentationId>();
        ImmutableArray<LocalReplicaId> removedRegistrations = [];
        long deletedBytes = 0;
        string operationRoot = Path.Combine(
            _history.Repository.Paths.TransactionsRoot,
            $"retention-{plan.PlanId}");
        try
        {
            foreach (var compact in plan.Compactions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var version = await _history.Query.GetVersionAsync(compact.VersionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Compaction Version {compact.VersionId} is missing.");
                var allRepresentations = await _history.Query.GetAllRepresentationsAsync(cancellationToken).ConfigureAwait(false);
                var environment = await _environmentFactory(cancellationToken).ConfigureAwait(false);
                var materialized = Path.Combine(operationRoot, compact.VersionId.ToString(), "materialized");
                await _representations.MaterializeAsync(
                    compact.SelectedRepresentationId,
                    allRepresentations,
                    environment,
                    plan.Request.RequiredFidelity,
                    materialized,
                    cancellationToken).ConfigureAwait(false);

                var outputRoot = Path.Combine(
                    _history.Repository.Paths.RepositoryRoot,
                    "payloads",
                    compact.ProposedReplacementRepresentationId.ToString());
                var payload = await _compaction.CreateFullAsync(
                    version,
                    materialized,
                    compact.ProposedReplacementRepresentationId,
                    outputRoot,
                    cancellationToken).ConfigureAwait(false);
                ValidatePayload(payload, outputRoot);
                var replacement = new VersionRepresentation(
                    compact.ProposedReplacementRepresentationId,
                    version.VersionId,
                    RepresentationKind.CoreFull,
                    payload.Format,
                    [],
                    MaterializationFidelity.Exact,
                    payload.LogicalSha256,
                    payload.StateFingerprint ?? version.StateFingerprint,
                    payload.Metadata);
                var verification = await _compaction.DeepVerifyAsync(
                    replacement,
                    payload.PayloadPath,
                    cancellationToken).ConfigureAwait(false);
                if (!verification.Success)
                    throw new InvalidDataException($"Replacement deep verification failed: {verification.Diagnostic}");

                await CommitReplacementAndRegisterAsync(replacement, payload, cancellationToken).ConfigureAwait(false);
                replacementIds.Add(replacement.RepresentationId);
                preferred[version.VersionId] = replacement.RepresentationId;
            }

            var finalPlan = await _planner.PlanAsync(plan.Request, preferred, cancellationToken).ConfigureAwait(false);
            if (!finalPlan.CanExecute
                || !finalPlan.Compactions.IsEmpty
                || !SameProtectionRoots(plan, finalPlan)
                || preferred.Any(pair => finalPlan.ProtectedClosures.All(
                    closure => closure.VersionId != pair.Key
                        || closure.SelectedRepresentationId != pair.Value)))
            {
                throw new InvalidOperationException(
                    "Protected roots could not be re-assessed through every committed replacement Representation.");
            }
            var plannedDeletionIds = plan.LocalPayloadDeletions.Select(item => item.LocalReplicaId).ToHashSet();
            if (finalPlan.LocalPayloadDeletions.Any(item => !plannedDeletionIds.Contains(item.LocalReplicaId)))
                throw new InvalidOperationException("Post-compaction deletion set exceeds the approved dry-run plan.");

            cancellationToken.ThrowIfCancellationRequested();
            removedRegistrations = await RemoveLocalRegistrationsAsync(
                finalPlan.LocalPayloadDeletions,
                finalPlan.ExpectedCatalogRevision,
                CancellationToken.None).ConfigureAwait(false);
            if (!removedRegistrations.IsEmpty)
            {
                await _history.RefreshLocalStateHealthAsync(CancellationToken.None).ConfigureAwait(false);
                _history.ChangeFeed.Publish(_history.ConfigId, HistoryChangeKind.LocalStateChanged);
            }
            foreach (var deletion in finalPlan.LocalPayloadDeletions)
            {
                try
                {
                    await _payloads.DeleteAsync(deletion.ResolvedPath, CancellationToken.None).ConfigureAwait(false);
                    deletedBytes = checked(deletedBytes + deletion.EstimatedBytes);
                }
                catch (Exception ex)
                {
                    return new HistoryRetentionExecutionResult(
                        HistoryRetentionExecutionStatus.OrphanedLocalBytes,
                        $"Local registration was removed safely, but payload bytes remain orphaned: {ex.Message}",
                        replacementIds.ToImmutableArray(),
                        removedRegistrations,
                        deletedBytes);
                }
            }

            await _artifactGarbageCollector.GarbageCollectAsync(
                finalPlan.ProtectedArtifactRootIds.ToHashSet(),
                CancellationToken.None).ConfigureAwait(false);
            await _history.RefreshLocalStateHealthAsync(CancellationToken.None).ConfigureAwait(false);
            _history.ChangeFeed.Publish(_history.ConfigId, HistoryChangeKind.LocalStateChanged);
            return new HistoryRetentionExecutionResult(
                HistoryRetentionExecutionStatus.Succeeded,
                string.Empty,
                replacementIds.ToImmutableArray(),
                removedRegistrations,
                deletedBytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new HistoryRetentionExecutionResult(
                HistoryRetentionExecutionStatus.Failed,
                ex.Message,
                replacementIds.ToImmutableArray(),
                removedRegistrations,
                deletedBytes);
        }
        finally
        {
            CleanupOperationDirectory(operationRoot);
        }
    }

    private async Task CommitReplacementAndRegisterAsync(
        VersionRepresentation replacement,
        HistoryCompactionPayload payload,
        CancellationToken cancellationToken)
    {
        var catalogLoad = await _history.LocalReplicaCatalogStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (catalogLoad.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible)
            throw new InvalidDataException("Local Replica Catalog requires recovery.");
        var currentCatalog = catalogLoad.Value;
        var expectedRevision = currentCatalog?.CatalogRevision ?? LocalReplicaCatalogStore.MissingRevision;
        var entry = new LocalReplicaCatalogEntry(
            replacement.RepresentationId,
            LocalReplicaId.New(),
            LocalReplicaLocator.ControlledAbsolute(payload.PayloadPath),
            DateTimeOffset.UtcNow);
        var updatedCatalog = new LocalReplicaCatalog(
            _history.ConfigId,
            checked(expectedRevision + 1),
            (currentCatalog?.Entries ?? []).Append(entry));
        var pack = new HistoryCommitPack(
            PackId.New(),
            HistoryTransactionId.New(),
            DateTimeOffset.UtcNow,
            [_codec.CreateObject(replacement)]);
        var journal = HistoryTransactionJournal.Prepared(
            pack.TransactionId,
            pack.PackId,
            [HistoryLocalStateJournalRecovery.CreateCatalogIntent(updatedCatalog, expectedRevision)],
            [payload.PayloadPath]);
        await _history.Repository.CommitAsync(pack, journal, cancellationToken).ConfigureAwait(false);
        try
        {
            var recovery = new HistoryLocalStateJournalRecovery(
                _history.WorkspaceStore,
                _history.LocalReplicaCatalogStore);
            await recovery.ApplyCommittedStateAsync(journal, cancellationToken).ConfigureAwait(false);
            _history.Repository.Journals.Save(journal with { Phase = HistoryTransactionPhase.LocalStateApplied });
            _history.Repository.Journals.Save(journal with { Phase = HistoryTransactionPhase.Complete });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Replacement metadata is durable, but local registration requires transaction recovery.",
                ex);
        }
        await _history.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        await _history.RefreshLocalStateHealthAsync(CancellationToken.None).ConfigureAwait(false);
        _history.ChangeFeed.Publish(
            _history.ConfigId,
            HistoryChangeKind.TransactionCommitted,
            [replacement.RepresentationId.ToString()]);
        _history.ChangeFeed.Publish(_history.ConfigId, HistoryChangeKind.LocalStateChanged);
    }

    private async Task<ImmutableArray<LocalReplicaId>> RemoveLocalRegistrationsAsync(
        IReadOnlyList<HistoryLocalPayloadDeletion> deletions,
        long expectedCatalogRevision,
        CancellationToken cancellationToken)
    {
        if (deletions.Count == 0) return [];
        var load = await _history.LocalReplicaCatalogStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var catalog = load.Value
            ?? throw new InvalidDataException("Local Replica Catalog is missing before GC.");
        if (load.Status != DeviceLocalStateStatus.Valid || catalog.CatalogRevision != expectedCatalogRevision)
            throw new DeviceLocalStateConflictException("Local Replica Catalog changed after retention re-assessment.");
        var ids = deletions.Select(item => item.LocalReplicaId).ToHashSet();
        if (!ids.IsSubsetOf(catalog.Entries.Select(item => item.LocalReplicaId).ToHashSet()))
            throw new DeviceLocalStateConflictException("A planned local registration no longer exists.");
        var updated = new LocalReplicaCatalog(
            _history.ConfigId,
            checked(catalog.CatalogRevision + 1),
            catalog.Entries.Where(item => !ids.Contains(item.LocalReplicaId)));
        await _history.LocalReplicaCatalogStore.SaveAsync(
            updated,
            catalog.CatalogRevision,
            cancellationToken).ConfigureAwait(false);
        return ids.OrderBy(item => item.ToString(), StringComparer.Ordinal).ToImmutableArray();
    }

    private static bool SameProtectionRoots(HistoryRetentionPlan before, HistoryRetentionPlan after)
        => before.ProtectedCheckpoints.SequenceEqual(after.ProtectedCheckpoints)
            && before.ProtectedVersions.SequenceEqual(after.ProtectedVersions)
            && before.ProtectedOperationRepresentations.SequenceEqual(after.ProtectedOperationRepresentations);

    private static void ValidatePayload(HistoryCompactionPayload payload, string durableOutputDirectory)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var outputRoot = Path.GetFullPath(durableOutputDirectory);
        var payloadPath = string.IsNullOrWhiteSpace(payload.PayloadPath)
            ? string.Empty
            : Path.GetFullPath(payload.PayloadPath);
        var relative = string.IsNullOrEmpty(payloadPath)
            ? string.Empty
            : Path.GetRelativePath(outputRoot, payloadPath);
        if (string.IsNullOrWhiteSpace(payload.Format)
            || string.IsNullOrEmpty(payloadPath)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !File.Exists(payloadPath))
        {
            throw new InvalidDataException("Compaction backend did not produce a durable replacement payload file.");
        }
        if (payload.Size < 0 || new FileInfo(payloadPath).Length != payload.Size)
            throw new InvalidDataException("Compaction payload size does not match the durable file.");
    }

    private static void CleanupOperationDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { }
    }

    private static HistoryRetentionExecutionResult Result(
        HistoryRetentionExecutionStatus status,
        string diagnostic)
        => new(status, diagnostic, [], [], 0);
}

using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.Index;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed record HistoryConfigSourceSnapshot(
    SourceId SourceId,
    SourceDescriptorSnapshot Descriptor,
    EffectiveSourceBoundarySnapshot? EffectiveSourceBoundary = null)
{
    public EffectiveSourceBoundarySnapshot Boundary =>
        EffectiveSourceBoundary ?? EffectiveSourceBoundarySnapshot.All;
}

public sealed record HistoryConfigSnapshot
{
    public HistoryConfigSnapshot(
        HistoryConfigId configId,
        IEnumerable<HistoryConfigSourceSnapshot> sources,
        string defaultBranchName = "main")
    {
        ConfigId = configId;
        Sources = sources is null
            ? throw new ArgumentNullException(nameof(sources))
            : [.. sources.OrderBy(source => source.SourceId.ToString(), StringComparer.Ordinal)];
        if (Sources.GroupBy(source => source.SourceId).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Config snapshot contains duplicate SourceId values.", nameof(sources));
        }

        DefaultBranchName = string.IsNullOrWhiteSpace(defaultBranchName)
            ? "main"
            : defaultBranchName.Trim();
    }

    public HistoryConfigId ConfigId { get; }
    public ImmutableArray<HistoryConfigSourceSnapshot> Sources { get; }
    public string DefaultBranchName { get; }
}

public sealed record HistoryBackupInvocation(
    RunId RunId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    BackupInvocationKind Kind,
    HistoryProvenance Provenance,
    string Comment = "");

public sealed record HistoryBranchCreationIntent
{
    public HistoryBranchCreationIntent(BranchId branchId, string name)
    {
        BranchId = branchId;
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Branch name cannot be empty.", nameof(name))
            : name.Trim();
    }

    public BranchId BranchId { get; }
    public string Name { get; }
}

public enum HistoryCommitIntent
{
    AdvanceBranch = 0,
    IndependentRecoveryPoint = 1
}

public sealed record HistorySafetySnapshotIntent(SafetySnapshotReason Reason);

public sealed record HistoryCommitRequest
{
    public HistoryCommitRequest(
        HistoryConfigSnapshot configSnapshot,
        HistoryBackupInvocation invocation,
        HistoryWorkspace? expectedWorkspace,
        IEnumerable<SourceCaptureResult> sourceCaptureResults,
        HistoryBranchCreationIntent? branchCreationIntent = null,
        HistoryCommitIntent intent = HistoryCommitIntent.AdvanceBranch,
        IEnumerable<SourceId>? affectedSourceIds = null,
        HistorySafetySnapshotIntent? safetySnapshotIntent = null)
    {
        ConfigSnapshot = configSnapshot ?? throw new ArgumentNullException(nameof(configSnapshot));
        Invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
        ExpectedWorkspace = expectedWorkspace;
        BranchCreationIntent = branchCreationIntent;
        Intent = intent;
        SafetySnapshotIntent = safetySnapshotIntent;
        SourceCaptureResults = sourceCaptureResults is null
            ? throw new ArgumentNullException(nameof(sourceCaptureResults))
            : [.. sourceCaptureResults];
        if (SourceCaptureResults.IsEmpty)
        {
            throw new ArgumentException("A backup commit requires at least one Source result.", nameof(sourceCaptureResults));
        }
        if (SourceCaptureResults.GroupBy(result => result.SourceId).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Backup commit contains duplicate Source results.", nameof(sourceCaptureResults));
        }
        AffectedSourceIds = affectedSourceIds is null
            ? [.. SourceCaptureResults.Select(result => result.SourceId)]
            : [.. affectedSourceIds.Distinct()];
        if (!AffectedSourceIds.ToHashSet().SetEquals(SourceCaptureResults.Select(result => result.SourceId)))
        {
            throw new ArgumentException(
                "Affected Source roster must exactly match the supplied capture results.",
                nameof(affectedSourceIds));
        }
        if (Intent == HistoryCommitIntent.IndependentRecoveryPoint && BranchCreationIntent is not null)
        {
            throw new ArgumentException("An independent recovery point cannot create a Branch.", nameof(branchCreationIntent));
        }
        if (SafetySnapshotIntent is not null && Intent != HistoryCommitIntent.IndependentRecoveryPoint)
        {
            throw new ArgumentException(
                "SafetySnapshot creation requires IndependentRecoveryPoint intent.",
                nameof(safetySnapshotIntent));
        }
    }

    public HistoryConfigSnapshot ConfigSnapshot { get; }
    public HistoryBackupInvocation Invocation { get; }
    public HistoryWorkspace? ExpectedWorkspace { get; }
    public ImmutableArray<SourceCaptureResult> SourceCaptureResults { get; }
    public HistoryBranchCreationIntent? BranchCreationIntent { get; }
    public HistoryCommitIntent Intent { get; }
    public ImmutableArray<SourceId> AffectedSourceIds { get; }
    public HistorySafetySnapshotIntent? SafetySnapshotIntent { get; }
}

public sealed record HistoryCommitBatch(
    HistoryCommitPack Pack,
    BackupRun Run,
    ImmutableArray<SourceVersion> NewVersions,
    ImmutableArray<VersionRepresentation> NewRepresentations,
    ImmutableArray<VersionMetadataSnapshot> NewMetadataSnapshots,
    ConfigurationCheckpoint? NewCheckpoint,
    SafetySnapshot? NewSafetySnapshot,
    BranchUpdate? NewBranchUpdate,
    HistoryWorkspace? UpdatedWorkspace,
    LocalReplicaCatalog? UpdatedLocalReplicaCatalog,
    bool IndexRefreshSucceeded);

public sealed class HistoryCommitConflictException(string message) : Exception(message);

public sealed class HistoryCommitRecoveryRequiredException(
    PackId committedPackId,
    string message,
    Exception innerException)
    : Exception(message, innerException)
{
    public PackId CommittedPackId { get; } = committedPackId;
}

public sealed record HistoryBoundaryRecaptureRequirement(
    SourceId SourceId,
    string PreviousBoundaryFingerprint,
    string CurrentBoundaryFingerprint);

/// <summary>
/// The only Native Backup writer for Run, Version, Representation, Checkpoint and BranchUpdate facts.
/// All authoritative facts enter one immutable Commit Pack; device-local Workspace and replica catalog
/// changes are recovered idempotently from the transaction journal after the pack is durable.
/// </summary>
public sealed class HistoryCommitCoordinator
{
    private readonly HistoryRuntime _runtime;
    private readonly HistoryPackCodec _codec;

    public HistoryCommitCoordinator(HistoryRuntime runtime, HistoryPackCodec? codec = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _codec = codec ?? new HistoryPackCodec();
    }

    /// <summary>
    /// 在捕获前执行只读预检查：发现未参与本次捕获（unrequested）但有效边界已发生变更（boundary drift）的备份源。
    /// 如果存在此类源，则子集备份无法安全 Carry Forward 其历史版本，需要先通过完整备份重新建立基线。
    /// </summary>
    public async Task<IReadOnlyList<HistoryBoundaryRecaptureRequirement>> FindRequiredBoundaryRecapturesAsync(
        HistoryConfigSnapshot snapshot,
        IReadOnlyCollection<SourceId> plannedCaptureSources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(plannedCaptureSources);

        if (snapshot.ConfigId != _runtime.ConfigId)
        {
            throw new ArgumentException("Config snapshot does not belong to this History Runtime.", nameof(snapshot));
        }

        var workspaceLoad = await _runtime.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var workspace = workspaceLoad.Value;
        if (workspace is null)
        {
            return Array.Empty<HistoryBoundaryRecaptureRequirement>();
        }

        var baselineMap = workspace.SourceBaselines.ToDictionary(item => item.SourceId);
        var plannedSet = plannedCaptureSources.ToHashSet();
        var requirements = new List<HistoryBoundaryRecaptureRequirement>();

        foreach (var source in snapshot.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plannedSet.Contains(source.SourceId))
            {
                continue;
            }

            if (baselineMap.TryGetValue(source.SourceId, out var baseline))
            {
                VersionId? reliableBaseline = ReliableVersion(baseline);
                if (reliableBaseline is { } reliableVersionId)
                {
                    var version = await _runtime.Query.GetVersionAsync(reliableVersionId, cancellationToken).ConfigureAwait(false);
                    if (version is not null)
                    {
                        var boundary = source.Boundary;
                        if (!StringComparer.Ordinal.Equals(version.EffectiveSourceBoundaryFingerprint, boundary.Fingerprint))
                        {
                            requirements.Add(new HistoryBoundaryRecaptureRequirement(
                                source.SourceId,
                                version.EffectiveSourceBoundaryFingerprint,
                                boundary.Fingerprint));
                        }
                    }
                }
            }
        }

        return requirements;
    }

    public async Task<HistoryCommitBatch> CommitAsync(
        HistoryCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (request.ConfigSnapshot.ConfigId != _runtime.ConfigId)
            {
                throw new HistoryCommitConflictException("Config snapshot does not belong to this History Runtime.");
            }
            if (request.Invocation.CompletedAtUtc < request.Invocation.StartedAtUtc)
            {
                throw new ArgumentException("Backup completion time cannot precede its start time.", nameof(request));
            }
            if (request.Invocation.Provenance is null)
            {
                throw new ArgumentException("Backup invocation provenance is required.", nameof(request));
            }
        }
        catch
        {
            await CleanupUncommittedCaptureAsync(request.SourceCaptureResults).ConfigureAwait(false);
            throw;
        }

        IAsyncDisposable lease;
        try
        {
            lease = await _runtime.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await CleanupUncommittedCaptureAsync(request.SourceCaptureResults).ConfigureAwait(false);
            throw;
        }

        HistoryCommitPack? preparedPack = null;
        await using var acquiredLease = lease;
        try
        {
            await _runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (await _runtime.Query.GetRunAsync(request.Invocation.RunId, cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new HistoryCommitConflictException($"Backup Run {request.Invocation.RunId} is already committed.");
            }
            var workspaceLoad = await _runtime.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var catalogLoad = await _runtime.LocalReplicaCatalogStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var currentWorkspace = ValidateExpectedWorkspace(request.ExpectedWorkspace, workspaceLoad);
            var currentCatalog = ValidateCatalog(catalogLoad);
            if (request.BranchCreationIntent is not null)
            {
                await ValidateNewBranchIdentityAsync(request.BranchCreationIntent, cancellationToken).ConfigureAwait(false);
            }
            await ValidateExpectedCaptureStateAsync(request, currentWorkspace, cancellationToken).ConfigureAwait(false);

            var batch = await BuildBatchAsync(
                request,
                currentWorkspace,
                currentCatalog,
                cancellationToken).ConfigureAwait(false);
            preparedPack = batch.Pack;
            var intents = new List<HistoryLocalStateIntent>(2);
            if (batch.UpdatedWorkspace is not null)
            {
                intents.Add(HistoryLocalStateJournalRecovery.CreateWorkspaceIntent(
                    batch.UpdatedWorkspace,
                    currentWorkspace?.StateRevision ?? HistoryWorkspaceStore.MissingRevision));
            }
            if (batch.UpdatedLocalReplicaCatalog is not null)
            {
                intents.Add(HistoryLocalStateJournalRecovery.CreateCatalogIntent(
                    batch.UpdatedLocalReplicaCatalog,
                    currentCatalog?.CatalogRevision ?? LocalReplicaCatalogStore.MissingRevision));
            }

            var journal = HistoryTransactionJournal.Prepared(
                batch.Pack.TransactionId,
                batch.Pack.PackId,
                intents);
            await _runtime.Repository.CommitAsync(batch.Pack, journal, cancellationToken).ConfigureAwait(false);
            var recovery = new HistoryLocalStateJournalRecovery(
                _runtime.WorkspaceStore,
                _runtime.LocalReplicaCatalogStore);
            try
            {
                await recovery.ApplyCommittedStateAsync(journal, cancellationToken).ConfigureAwait(false);
                _runtime.Repository.Journals.Save(journal with { Phase = HistoryTransactionPhase.LocalStateApplied });
                _runtime.Repository.Journals.Save(journal with { Phase = HistoryTransactionPhase.Complete });
            }
            catch (Exception ex)
            {
                throw new HistoryCommitRecoveryRequiredException(
                    batch.Pack.PackId,
                    "History facts are durable, but device-local state requires journal recovery.",
                    ex);
            }
            await _runtime.RefreshLocalStateHealthAsync(CancellationToken.None).ConfigureAwait(false);

            bool indexRefreshSucceeded;
            try
            {
                var packs = await _runtime.Repository.ReadAllPacksAsync(cancellationToken).ConfigureAwait(false);
                await _runtime.Index.RebuildAsync(packs, cancellationToken).ConfigureAwait(false);
                indexRefreshSucceeded = true;
            }
            catch (Exception)
            {
                // The index is disposable derived state. The installed pack remains the authority.
                indexRefreshSucceeded = false;
            }

            _runtime.ChangeFeed.Publish(
                _runtime.ConfigId,
                HistoryChangeKind.TransactionCommitted,
                batch.Pack.Objects.Select(item => item.Id));
            if (batch.UpdatedWorkspace is not null || batch.UpdatedLocalReplicaCatalog is not null)
            {
                _runtime.ChangeFeed.Publish(_runtime.ConfigId, HistoryChangeKind.LocalStateChanged);
            }
            if (indexRefreshSucceeded)
            {
                _runtime.ChangeFeed.Publish(_runtime.ConfigId, HistoryChangeKind.IndexRebuilt);
            }

            return batch with { IndexRefreshSucceeded = indexRefreshSucceeded };
        }
        catch
        {
            bool packIsDurable = preparedPack is not null
                && File.Exists(_runtime.Repository.Paths.GetPackPath(preparedPack.PackId));
            if (!packIsDurable)
            {
                await CleanupUncommittedCaptureAsync(request.SourceCaptureResults).ConfigureAwait(false);
            }
            throw;
        }
    }

    private async Task<HistoryCommitBatch> BuildBatchAsync(
        HistoryCommitRequest request,
        HistoryWorkspace? workspace,
        LocalReplicaCatalog? catalog,
        CancellationToken cancellationToken)
    {
        var now = request.Invocation.CompletedAtUtc.ToUniversalTime();
        var baselineMap = workspace?.SourceBaselines.ToDictionary(item => item.SourceId)
            ?? new Dictionary<SourceId, WorkspaceSourceBaseline>();
        var resultMap = request.SourceCaptureResults.ToDictionary(result => result.SourceId);
        if (request.Intent == HistoryCommitIntent.IndependentRecoveryPoint)
        {
            var configuredSources = request.ConfigSnapshot.Sources.Select(source => source.SourceId).ToHashSet();
            if (!configuredSources.SetEquals(request.AffectedSourceIds)
                || request.SourceCaptureResults.Any(result => result.Outcome is
                    SourceCaptureOutcome.Unavailable
                    or SourceCaptureOutcome.Failed
                    or SourceCaptureOutcome.Canceled
                    or SourceCaptureOutcome.Blocked))
            {
                throw new HistoryCommitConflictException(
                    "An independent recovery point requires a reliable Exact result for every configured Source.");
            }
        }
        var currentBranch = await ResolveCurrentBranchAsync(workspace, cancellationToken).ConfigureAwait(false);
        var currentCheckpoint = currentBranch?.TargetCheckpointId is { } checkpointId
            ? await _runtime.Query.GetCheckpointAsync(checkpointId, cancellationToken).ConfigureAwait(false)
                ?? throw new HistoryCommitConflictException("Active Branch target Checkpoint is missing from the index.")
            : null;

        var versions = ImmutableArray.CreateBuilder<SourceVersion>();
        var representations = ImmutableArray.CreateBuilder<VersionRepresentation>();
        var metadataSnapshots = ImmutableArray.CreateBuilder<VersionMetadataSnapshot>();
        var localEntries = new List<LocalReplicaCatalogEntry>();
        var checkpointSources = ImmutableArray.CreateBuilder<CheckpointSource>();
        var runSources = ImmutableArray.CreateBuilder<BackupRunSourceResult>();
        var nextBaselines = ImmutableArray.CreateBuilder<WorkspaceSourceBaseline>();

        foreach (var source in request.ConfigSnapshot.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            baselineMap.TryGetValue(source.SourceId, out var baseline);
            resultMap.TryGetValue(source.SourceId, out var capture);
            VersionId? reliableBaseline = ReliableVersion(baseline);
            var reliableVersion = reliableBaseline is { } reliableVersionId
                ? await RequireVersionForSourceAsync(reliableVersionId, source.SourceId, cancellationToken).ConfigureAwait(false)
                : null;
            var boundary = source.Boundary;
            var boundaryChanged = reliableVersion is not null
                && !StringComparer.Ordinal.Equals(
                    reliableVersion.EffectiveSourceBoundaryFingerprint,
                    boundary.Fingerprint);
            if (boundaryChanged && capture?.Outcome != SourceCaptureOutcome.Captured)
            {
                throw new HistoryCommitConflictException(
                    $"Source {source.SourceId} boundary changed and requires a self-contained recapture.");
            }
            VersionId? finalVersionId = reliableBaseline;
            var finalBoundary = reliableVersion?.EffectiveSourceBoundary ?? boundary;
            var disposition = CheckpointSourceDisposition.CarriedForward;
            var runOutcome = BackupRunSourceOutcome.CarriedForward;
            var nextRelation = baseline?.Relation ?? WorkspaceBaselineRelation.Unknown;

            if (capture is not null)
            {
                switch (capture.Outcome)
                {
                    case SourceCaptureOutcome.Captured:
                        await ValidateCapturedResultAsync(capture, baseline, cancellationToken).ConfigureAwait(false);
                        if (!StringComparer.Ordinal.Equals(
                            capture.EffectiveSourceBoundary.Fingerprint,
                            boundary.Fingerprint))
                        {
                            throw new HistoryCommitConflictException(
                                "Captured Effective Source Boundary does not match the authoritative Config snapshot.");
                        }
                        if (boundaryChanged
                            && capture.RepresentationCandidate!.DependencyRepresentationIds.Length > 0)
                        {
                            throw new HistoryCommitConflictException(
                                "A boundary-changing capture must use a self-contained Representation.");
                        }
                        var versionId = VersionId.New();
                        var parents = reliableBaseline is { } parent ? new[] { parent } : Array.Empty<VersionId>();
                        var version = new SourceVersion(
                            versionId,
                            request.ConfigSnapshot.ConfigId,
                            source.SourceId,
                            parents,
                            now,
                            request.Invocation.RunId,
                            capture.CaptureScope,
                            CaptureOutcome.Captured,
                            capture.Diagnostics,
                            source.Descriptor,
                            capture.StateFingerprint,
                            request.Invocation.Provenance,
                            capture.EffectiveSourceBoundary);
                        HistoryDomainValidator.ValidateNative(version);
                        var representation = capture.RepresentationCandidate!.ToFact(versionId);
                        versions.Add(version);
                        representations.Add(representation);
                        foreach (var candidate in capture.VersionMetadataCandidates)
                        {
                            metadataSnapshots.Add(VersionMetadataSnapshot.Create(
                                versionId,
                                candidate.ProducerPluginId,
                                candidate.SchemaId,
                                candidate.SchemaVersion,
                                candidate.Payload,
                                version.CreatedAtUtc));
                        }
                        localEntries.Add(new LocalReplicaCatalogEntry(
                            capture.LocalReplicaCandidate!.RepresentationId,
                            capture.LocalReplicaCandidate.LocalReplicaId,
                            capture.LocalReplicaCandidate.Locator,
                            capture.LocalReplicaCandidate.CapturedAtUtc));
                        finalVersionId = versionId;
                        finalBoundary = capture.EffectiveSourceBoundary;
                        disposition = CheckpointSourceDisposition.Captured;
                        runOutcome = BackupRunSourceOutcome.Captured;
                        nextRelation = capture.CaptureScope == CaptureScope.FullSource
                            ? WorkspaceBaselineRelation.Exact
                            : WorkspaceBaselineRelation.Derived;
                        break;
                    case SourceCaptureOutcome.Reused:
                        var reused = await RequireReusableVersionAsync(capture, source.SourceId, cancellationToken).ConfigureAwait(false);
                        if (!StringComparer.Ordinal.Equals(
                            reused.EffectiveSourceBoundaryFingerprint,
                            boundary.Fingerprint))
                        {
                            throw new HistoryCommitConflictException(
                                "A reused Version cannot cross an Effective Source Boundary change.");
                        }
                        finalVersionId = reused.VersionId;
                        finalBoundary = reused.EffectiveSourceBoundary;
                        disposition = CheckpointSourceDisposition.Reused;
                        runOutcome = BackupRunSourceOutcome.Reused;
                        nextRelation = reused.CaptureScope == CaptureScope.FullSource
                            ? WorkspaceBaselineRelation.Exact
                            : WorkspaceBaselineRelation.Derived;
                        break;
                    case SourceCaptureOutcome.NoChanges:
                        if (reliableBaseline is null)
                        {
                            throw new HistoryCommitConflictException(
                                "NoChanges cannot be committed without a reliable Workspace baseline.");
                        }
                        var unchangedVersion = await RequireVersionForSourceAsync(
                            reliableBaseline.Value,
                            source.SourceId,
                            cancellationToken).ConfigureAwait(false);
                        if (!StringComparer.Ordinal.Equals(
                            unchangedVersion.EffectiveSourceBoundaryFingerprint,
                            boundary.Fingerprint))
                        {
                            throw new HistoryCommitConflictException(
                                "NoChanges cannot carry a Version across an Effective Source Boundary change.");
                        }
                        if (!string.IsNullOrWhiteSpace(capture.StateFingerprint)
                            && !StringComparer.Ordinal.Equals(capture.StateFingerprint, unchangedVersion.StateFingerprint))
                        {
                            throw new HistoryCommitConflictException(
                                "NoChanges state fingerprint does not match its Workspace baseline Version.");
                        }
                        disposition = CheckpointSourceDisposition.Reused;
                        runOutcome = BackupRunSourceOutcome.Reused;
                        if (capture.CaptureScope == CaptureScope.PartialSource)
                        {
                            // 局部 NoChanges 只证明操作范围未变，不能证明整个工作目录仍等于基线。
                            nextRelation = WorkspaceBaselineRelation.Derived;
                        }
                        break;
                    case SourceCaptureOutcome.Unavailable:
                        disposition = CheckpointSourceDisposition.Unavailable;
                        runOutcome = BackupRunSourceOutcome.Unavailable;
                        break;
                    case SourceCaptureOutcome.Failed:
                    case SourceCaptureOutcome.Canceled:
                    case SourceCaptureOutcome.Blocked:
                        disposition = CheckpointSourceDisposition.Failed;
                        runOutcome = BackupRunSourceOutcome.Failed;
                        break;
                    default:
                        throw new HistoryCommitConflictException($"Unsupported Source capture outcome {capture.Outcome}.");
                }
            }

            if (request.Intent == HistoryCommitIntent.IndependentRecoveryPoint
                && capture?.Outcome != SourceCaptureOutcome.Captured
                && finalVersionId is { } recoveryVersionId
                && !await HasDeclaredExactClosureAsync(recoveryVersionId, cancellationToken).ConfigureAwait(false))
            {
                throw new HistoryCommitConflictException(
                    $"Recovery Source {source.SourceId} has no declared Exact representation closure.");
            }

            checkpointSources.Add(new CheckpointSource(
                source.SourceId,
                source.Descriptor,
                finalVersionId,
                disposition,
                finalBoundary));
            runSources.Add(new BackupRunSourceResult(
                source.SourceId,
                runOutcome,
                finalVersionId,
                capture?.Diagnostics ?? []));
            nextBaselines.Add(new WorkspaceSourceBaseline(
                source.SourceId,
                finalVersionId,
                finalVersionId is null ? WorkspaceBaselineRelation.Unknown : nextRelation));
        }

        if (resultMap.Keys.Any(sourceId => request.ConfigSnapshot.Sources.All(source => source.SourceId != sourceId)))
        {
            throw new HistoryCommitConflictException("A Source result is absent from the Config snapshot roster.");
        }

        bool vectorChanged = !StateVectorsEqual(currentCheckpoint?.Sources ?? [], checkpointSources);
        bool hasReliableState = checkpointSources.Any(source => source.VersionId is not null);
        bool createCheckpoint = hasReliableState && (versions.Count > 0 || vectorChanged);
        ConfigurationCheckpoint? checkpoint = createCheckpoint
            ? new ConfigurationCheckpoint(
                CheckpointId.New(),
                request.ConfigSnapshot.ConfigId,
                now,
                request.Invocation.RunId,
                request.Invocation.Provenance,
                checkpointSources)
            : null;
        SafetySnapshot? safetySnapshot = null;
        if (request.SafetySnapshotIntent is not null)
        {
            if (checkpoint is null || !checkpoint.IsStructurallyComplete)
            {
                throw new HistoryCommitConflictException(
                    "SafetySnapshot requires a newly committed structurally complete checkpoint.");
            }
            safetySnapshot = new SafetySnapshot(
                SafetySnapshotId.New(),
                checkpoint.CheckpointId,
                now,
                request.SafetySnapshotIntent.Reason);
        }

        BranchUpdate? branchUpdate = null;
        HistoryWorkspace? updatedWorkspace = null;
        var branchTargetCheckpoint = checkpoint ?? (request.BranchCreationIntent is not null ? currentCheckpoint : null);
        if (branchTargetCheckpoint is not null && request.Intent == HistoryCommitIntent.AdvanceBranch)
        {
            var branchId = request.BranchCreationIntent?.BranchId
                ?? workspace?.ActiveBranchId
                ?? BranchId.New();
            bool fromHistoricalState = currentCheckpoint is not null
                && !WorkspaceMatchesCheckpoint(workspace!, currentCheckpoint);
            branchUpdate = new BranchUpdate(
                BranchUpdateId.New(),
                branchId,
                request.BranchCreationIntent is null && workspace?.ActiveBranchUpdateId is { } parentUpdateId
                    ? new[] { parentUpdateId }
                    : [],
                request.BranchCreationIntent?.Name
                    ?? currentBranch?.Name
                    ?? request.ConfigSnapshot.DefaultBranchName,
                branchTargetCheckpoint.CheckpointId,
                isDeleted: false,
                now,
                request.BranchCreationIntent is not null || workspace?.ActiveBranchId is null
                    ? BranchUpdateReason.Created
                    : fromHistoricalState
                        ? BranchUpdateReason.BackupFromHistoricalState
                        : BranchUpdateReason.Backup);
            HistoryDomainValidator.ValidateNative(branchUpdate);
            updatedWorkspace = new HistoryWorkspace(
                request.ConfigSnapshot.ConfigId,
                checked((workspace?.StateRevision ?? HistoryWorkspaceStore.MissingRevision) + 1),
                branchId,
                branchUpdate.UpdateId,
                nextBaselines);
        }
        else if (checkpoint is not null && request.Intent == HistoryCommitIntent.IndependentRecoveryPoint)
        {
            // 独立恢复点更新本机可靠基线，但不伪造隐藏 Branch，也不改变当前 Branch anchor。
            updatedWorkspace = new HistoryWorkspace(
                request.ConfigSnapshot.ConfigId,
                checked((workspace?.StateRevision ?? HistoryWorkspaceStore.MissingRevision) + 1),
                workspace?.ActiveBranchId,
                workspace?.ActiveBranchUpdateId,
                nextBaselines);
        }

        var checkpointForRun = checkpoint?.CheckpointId ?? currentCheckpoint?.CheckpointId;
        var runOutcomeValue = DetermineRunOutcome(
            request.SourceCaptureResults,
            checkpoint,
            checkpointSources);
        var run = new BackupRun(
            request.Invocation.RunId,
            request.ConfigSnapshot.ConfigId,
            request.Invocation.StartedAtUtc,
            request.Invocation.CompletedAtUtc,
            request.Invocation.Kind,
            runOutcomeValue,
            runSources,
            checkpointForRun,
            request.SourceCaptureResults.SelectMany(result => result.Diagnostics));

        LocalReplicaCatalog? updatedCatalog = null;
        if (localEntries.Count > 0)
        {
            var allEntries = (catalog?.Entries ?? []).Concat(localEntries).ToImmutableArray();
            if (allEntries.GroupBy(entry => entry.LocalReplicaId).Any(group => group.Count() > 1))
            {
                throw new HistoryCommitConflictException("Local Replica identity already exists in the catalog.");
            }
            updatedCatalog = new LocalReplicaCatalog(
                request.ConfigSnapshot.ConfigId,
                checked((catalog?.CatalogRevision ?? LocalReplicaCatalogStore.MissingRevision) + 1),
                allEntries);
        }

        var facts = new List<object>();
        facts.AddRange(versions);
        facts.AddRange(representations);
        facts.AddRange(metadataSnapshots);
        if (checkpoint is not null) facts.Add(checkpoint);
        if (safetySnapshot is not null) facts.Add(safetySnapshot);
        if (branchUpdate is not null) facts.Add(branchUpdate);
        facts.Add(run);
        var comment = request.Invocation.Comment?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(comment))
        {
            var annotations = versions
                .Select(version => new HistoryAnnotationUpdate(
                    AnnotationUpdateId.New(),
                    new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, version.VersionId.Value),
                    HistoryAnnotationKind.Comment,
                    [],
                    comment,
                    now))
                .Append(new HistoryAnnotationUpdate(
                    AnnotationUpdateId.New(),
                    new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Run, run.RunId.Value),
                    HistoryAnnotationKind.Comment,
                    [],
                    comment,
                    now))
                .ToArray();
            HistoryDomainValidator.ValidateAnnotationGraph(annotations);
            facts.AddRange(annotations);
        }
        var objects = facts
            .Select(fact => _codec.CreateObject(fact))
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToImmutableArray();
        var pack = new HistoryCommitPack(
            PackId.New(),
            HistoryTransactionId.New(),
            now,
            objects);
        return new HistoryCommitBatch(
            pack,
            run,
            versions.ToImmutable(),
            representations.ToImmutable(),
            metadataSnapshots.ToImmutable(),
            checkpoint,
            safetySnapshot,
            branchUpdate,
            updatedWorkspace,
            updatedCatalog,
            IndexRefreshSucceeded: false);
    }

    private async Task ValidateExpectedCaptureStateAsync(
        HistoryCommitRequest request,
        HistoryWorkspace? workspace,
        CancellationToken cancellationToken)
    {
        long expectedRevision = workspace?.StateRevision ?? HistoryWorkspaceStore.MissingRevision;
        var baselines = workspace?.SourceBaselines.ToDictionary(item => item.SourceId)
            ?? new Dictionary<SourceId, WorkspaceSourceBaseline>();
        foreach (var result in request.SourceCaptureResults)
        {
            if (result.ExpectedWorkspaceRevision != expectedRevision)
            {
                throw new HistoryCommitConflictException(
                    $"Source {result.SourceId} prepared against Workspace revision {result.ExpectedWorkspaceRevision}, current revision is {expectedRevision}.");
            }

            baselines.TryGetValue(result.SourceId, out var baseline);
            if (result.ExpectedBaseVersionId != baseline?.BaseVersionId)
            {
                throw new HistoryCommitConflictException($"Source {result.SourceId} baseline changed after capture preparation.");
            }

            if (result.ExpectedBaseVersionId is { } baseVersionId)
            {
                var currentTips = await _runtime.Query.GetMaterializationPolicyTipsAsync(
                    baseVersionId,
                    cancellationToken).ConfigureAwait(false);
                var actualTipIds = currentTips.Select(tip => tip.UpdateId).OrderBy(id => id.ToString(), StringComparer.Ordinal);
                var expectedTipIds = result.ExpectedMaterializationPolicyTipIds.OrderBy(id => id.ToString(), StringComparer.Ordinal);
                if (!actualTipIds.SequenceEqual(expectedTipIds))
                {
                    throw new HistoryCommitConflictException($"Source {result.SourceId} materialization policy tips changed after capture preparation.");
                }
            }
            else if (!result.ExpectedMaterializationPolicyTipIds.IsEmpty)
            {
                throw new HistoryCommitConflictException("Policy tips were supplied without an expected base Version.");
            }
        }
    }

    private async Task<BranchUpdate?> ResolveCurrentBranchAsync(
        HistoryWorkspace? workspace,
        CancellationToken cancellationToken)
    {
        if (workspace?.ActiveBranchId is not { } branchId
            || workspace.ActiveBranchUpdateId is not { } updateId)
        {
            return null;
        }

        var update = await _runtime.Query.GetBranchUpdateAsync(updateId, cancellationToken).ConfigureAwait(false)
            ?? throw new HistoryCommitConflictException("Workspace ActiveBranchUpdateId does not exist.");
        if (update.BranchId != branchId || update.IsDeleted)
        {
            throw new HistoryCommitConflictException("Workspace active Branch identity is stale or deleted.");
        }

        var tips = await _runtime.Query.GetBranchTipsAsync(branchId, cancellationToken).ConfigureAwait(false);
        if (tips.All(tip => tip.UpdateId != updateId))
        {
            throw new HistoryCommitConflictException("Workspace ActiveBranchUpdateId is no longer a current Branch tip.");
        }
        return update;
    }

    private async Task ValidateNewBranchIdentityAsync(
        HistoryBranchCreationIntent intent,
        CancellationToken cancellationToken)
    {
        var updates = await _runtime.Query.GetAllBranchUpdatesAsync(cancellationToken).ConfigureAwait(false);
        if (updates.Any(update => update.BranchId == intent.BranchId))
        {
            throw new HistoryCommitConflictException("New BranchId already exists.");
        }
        var tips = HistoryBranchProjection.Build(updates);
        if (tips.Any(branch => !branch.IsDeleted
                               && branch.Tips.Any(tip => string.Equals(
                                   tip.Name,
                                   intent.Name,
                                   StringComparison.OrdinalIgnoreCase))))
        {
            throw new HistoryCommitConflictException($"Active Branch name '{intent.Name}' already exists on this device.");
        }
    }

    private static HistoryWorkspace? ValidateExpectedWorkspace(
        HistoryWorkspace? expected,
        DeviceLocalStateLoadResult<HistoryWorkspace> actual)
    {
        if (actual.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible)
        {
            throw new HistoryCommitConflictException($"Workspace recovery is required: {actual.Diagnostic}");
        }
        if (actual.Status == DeviceLocalStateStatus.Missing)
        {
            if (expected is not null)
            {
                throw new HistoryCommitConflictException("Workspace disappeared after capture preparation.");
            }
            return null;
        }
        if (expected is null || actual.Value is null || !WorkspaceEquals(expected, actual.Value))
        {
            throw new HistoryCommitConflictException("Workspace changed after capture preparation.");
        }
        return actual.Value;
    }

    private static LocalReplicaCatalog? ValidateCatalog(
        DeviceLocalStateLoadResult<LocalReplicaCatalog> catalog)
    {
        if (catalog.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible)
        {
            throw new HistoryCommitConflictException($"Local Replica Catalog recovery is required: {catalog.Diagnostic}");
        }
        return catalog.Value;
    }

    private static bool WorkspaceEquals(HistoryWorkspace left, HistoryWorkspace right)
        => left.ConfigId == right.ConfigId
            && left.StateRevision == right.StateRevision
            && left.ActiveBranchId == right.ActiveBranchId
            && left.ActiveBranchUpdateId == right.ActiveBranchUpdateId
            && left.SourceBaselines
                .OrderBy(item => item.SourceId.ToString(), StringComparer.Ordinal)
                .SequenceEqual(right.SourceBaselines.OrderBy(item => item.SourceId.ToString(), StringComparer.Ordinal));

    private static bool WorkspaceMatchesCheckpoint(
        HistoryWorkspace workspace,
        ConfigurationCheckpoint checkpoint)
    {
        var workspaceVector = workspace.SourceBaselines
            .Select(item => (item.SourceId, VersionId: ReliableVersion(item)))
            .OrderBy(item => item.SourceId.ToString(), StringComparer.Ordinal);
        var checkpointVector = checkpoint.Sources
            .Select(item => (item.SourceId, item.VersionId))
            .OrderBy(item => item.SourceId.ToString(), StringComparer.Ordinal);
        return workspaceVector.SequenceEqual(checkpointVector);
    }

    private static bool StateVectorsEqual(
        IEnumerable<CheckpointSource> current,
        IEnumerable<CheckpointSource> final)
        => current
            .Select(item => (item.SourceId, item.VersionId, item.EffectiveSourceBoundary.Fingerprint))
            .OrderBy(item => item.SourceId.ToString(), StringComparer.Ordinal)
            .SequenceEqual(final
                .Select(item => (item.SourceId, item.VersionId, item.EffectiveSourceBoundary.Fingerprint))
                .OrderBy(item => item.SourceId.ToString(), StringComparer.Ordinal));

    private static VersionId? ReliableVersion(WorkspaceSourceBaseline? baseline)
        => baseline is not null && baseline.Relation != WorkspaceBaselineRelation.Unknown
            ? baseline.BaseVersionId
            : null;

    private async Task<SourceVersion> RequireReusableVersionAsync(
        SourceCaptureResult capture,
        SourceId sourceId,
        CancellationToken cancellationToken)
    {
        var existingVersionId = capture.ExistingVersionId
            ?? throw new HistoryCommitConflictException("Reused capture has no existing VersionId.");
        return await RequireVersionForSourceAsync(existingVersionId, sourceId, cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateCapturedResultAsync(
        SourceCaptureResult capture,
        WorkspaceSourceBaseline? baseline,
        CancellationToken cancellationToken)
    {
        if (capture.RepresentationCandidate is null
            || capture.LocalReplicaCandidate is null
            || capture.PayloadCandidate is null)
        {
            throw new HistoryCommitConflictException("Captured Source result is incomplete for Native History.");
        }
        if (capture.RepresentationCandidate.IsLegacyBridgeCandidate)
        {
            throw new HistoryCommitConflictException("Legacy bridge candidates cannot enter Native History.");
        }
        if (capture.PayloadCandidate.State != CapturePayloadState.VerifiedFinal
            || capture.LocalReplicaCandidate.PayloadState != CapturePayloadState.VerifiedFinal)
        {
            throw new HistoryCommitConflictException("Native History accepts only verified final payloads.");
        }
        if (capture.CaptureScope == CaptureScope.FullSource
            && capture.RepresentationCandidate.Fidelity != MaterializationFidelity.Exact)
        {
            throw new HistoryCommitConflictException("A FullSource capture requires an Exact representation.");
        }
        if (capture.CaptureScope == CaptureScope.PartialSource
            && capture.RepresentationCandidate.Fidelity != MaterializationFidelity.Exact)
        {
            throw new HistoryCommitConflictException(
                "A Native partial capture must still produce an Exact logical Version closure.");
        }
        if (!StringComparer.Ordinal.Equals(
                capture.RepresentationCandidate.StateFingerprint,
                capture.StateFingerprint))
        {
            throw new HistoryCommitConflictException("Version and Representation state fingerprints disagree.");
        }
        var dependencies = capture.RepresentationCandidate.DependencyRepresentationIds;
        bool selfContainedCore = capture.RepresentationCandidate.Kind is
            RepresentationKind.CoreFull or RepresentationKind.CoreRolling;
        if (selfContainedCore && !dependencies.IsEmpty)
        {
            throw new HistoryCommitConflictException("A self-contained Core representation cannot declare dependencies.");
        }
        if (capture.RepresentationCandidate.Kind == RepresentationKind.CoreSmartDelta
            && dependencies.IsEmpty)
        {
            throw new HistoryCommitConflictException("A Smart delta representation requires a base dependency.");
        }
        if (!dependencies.IsEmpty)
        {
            var reliableBaseVersion = ReliableVersion(baseline)
                ?? throw new HistoryCommitConflictException(
                    "A dependent representation requires an Exact or Derived Workspace baseline.");
            var baseVersion = await RequireVersionForSourceAsync(
                reliableBaseVersion,
                capture.SourceId,
                cancellationToken).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(
                baseVersion.EffectiveSourceBoundaryFingerprint,
                capture.EffectiveSourceBoundary.Fingerprint))
            {
                throw new HistoryCommitConflictException(
                    "A dependent representation cannot cross Effective Source Boundaries.");
            }
            foreach (var dependencyId in dependencies)
            {
                var dependency = await _runtime.Query.GetRepresentationAsync(
                    dependencyId,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new HistoryCommitConflictException(
                        $"Representation dependency {dependencyId} does not exist.");
                if (dependency.VersionId != reliableBaseVersion)
                {
                    throw new HistoryCommitConflictException(
                        "Representation dependency does not belong to the expected base Version.");
                }
                if (dependency.Fidelity != MaterializationFidelity.Exact)
                {
                    throw new HistoryCommitConflictException(
                        "A Native Exact patch requires an Exact dependency representation.");
                }
            }
        }
        if (capture.LocalReplicaCandidate.Locator.Kind != LocalReplicaLocatorKind.ControlledAbsolutePath
            || !StringComparer.OrdinalIgnoreCase.Equals(
                capture.LocalReplicaCandidate.Locator.Resolve(),
                Path.GetFullPath(capture.PayloadCandidate.AbsolutePath)))
        {
            throw new HistoryCommitConflictException("Local Replica locator does not identify the captured payload.");
        }
        if (!File.Exists(capture.PayloadCandidate.AbsolutePath))
        {
            throw new HistoryCommitConflictException("Verified capture payload is missing.");
        }
        var actualSize = new FileInfo(capture.PayloadCandidate.AbsolutePath).Length;
        if (capture.PayloadCandidate.ExpectedSize is { } expectedSize && actualSize != expectedSize)
        {
            throw new HistoryCommitConflictException("Verified capture payload size changed before commit.");
        }
        if (capture.PayloadCandidate.ExpectedStorageSha256 is { } expectedHash)
        {
            using var stream = File.OpenRead(capture.PayloadCandidate.AbsolutePath);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!StringComparer.Ordinal.Equals(expectedHash.ToLowerInvariant(), actualHash))
            {
                throw new HistoryCommitConflictException("Verified capture payload hash changed before commit.");
            }
        }
    }

    private async Task<SourceVersion> RequireVersionForSourceAsync(
        VersionId versionId,
        SourceId sourceId,
        CancellationToken cancellationToken)
    {
        var version = await _runtime.Query.GetVersionAsync(versionId, cancellationToken).ConfigureAwait(false)
            ?? throw new HistoryCommitConflictException($"Workspace baseline Version {versionId} does not exist.");
        if (version.ConfigId != _runtime.ConfigId || version.SourceId != sourceId)
        {
            throw new HistoryCommitConflictException("Workspace baseline belongs to another Config or Source.");
        }
        return version;
    }

    private async Task<bool> HasDeclaredExactClosureAsync(
        VersionId versionId,
        CancellationToken cancellationToken)
    {
        var representations = await _runtime.Query.GetAllRepresentationsAsync(cancellationToken).ConfigureAwait(false);
        var graph = representations.ToDictionary(item => item.RepresentationId);
        return representations
            .Where(item => item.VersionId == versionId)
            .Any(root => IsExact(root, new HashSet<RepresentationId>()));

        bool IsExact(VersionRepresentation representation, HashSet<RepresentationId> visiting)
        {
            if (representation.Fidelity != MaterializationFidelity.Exact
                || !visiting.Add(representation.RepresentationId))
            {
                return false;
            }

            foreach (var dependencyId in representation.DependencyRepresentationIds)
            {
                if (!graph.TryGetValue(dependencyId, out var dependency)
                    || !IsExact(dependency, visiting))
                {
                    return false;
                }
            }

            visiting.Remove(representation.RepresentationId);
            return true;
        }
    }

    private static BackupRunOutcome DetermineRunOutcome(
        IReadOnlyCollection<SourceCaptureResult> captures,
        ConfigurationCheckpoint? checkpoint,
        IEnumerable<CheckpointSource> checkpointSources)
    {
        bool hasFailure = captures.Any(result => result.Outcome is
            SourceCaptureOutcome.Failed or SourceCaptureOutcome.Canceled or SourceCaptureOutcome.Blocked);
        bool hasUnavailable = captures.Any(result => result.Outcome == SourceCaptureOutcome.Unavailable);
        if (!hasFailure && !hasUnavailable && checkpoint is null)
        {
            return BackupRunOutcome.NoChange;
        }
        var projectedSources = checkpointSources.ToArray();
        bool hasReliableVersion = projectedSources.Any(source => source.VersionId is not null);
        bool hasIncompleteCoverage = projectedSources.Any(source => source.VersionId is null);
        if (!hasReliableVersion && (hasFailure || hasUnavailable))
        {
            return BackupRunOutcome.Failed;
        }
        return hasFailure || hasUnavailable || hasIncompleteCoverage
            ? BackupRunOutcome.Partial
            : BackupRunOutcome.Completed;
    }

    private static async Task CleanupUncommittedCaptureAsync(
        IEnumerable<SourceCaptureResult> captures)
    {
        foreach (var handle in captures
                     .Select(capture => capture.CleanupHandle)
                     .Where(handle => handle is not null)
                     .Distinct())
        {
            try
            {
                await handle!.CleanupAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Cleanup is best-effort; the failed transaction never gains durable History facts.
            }
        }
    }
}

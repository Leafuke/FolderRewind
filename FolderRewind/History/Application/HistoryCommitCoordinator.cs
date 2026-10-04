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
        HistorySafetySnapshotIntent? safetySnapshotIntent = null,
        bool protect = false,
        Func<VersionId, CancellationToken, Task<bool>>? validateProtectedReuse = null)
    {
        ConfigSnapshot = configSnapshot ?? throw new ArgumentNullException(nameof(configSnapshot));
        Invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
        ExpectedWorkspace = expectedWorkspace;
        BranchCreationIntent = branchCreationIntent;
        Intent = intent;
        SafetySnapshotIntent = safetySnapshotIntent;
        Protect = protect;
        ValidateProtectedReuse = validateProtectedReuse;
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
    public bool Protect { get; }
    public Func<VersionId, CancellationToken, Task<bool>>? ValidateProtectedReuse { get; }
}

public sealed record HistoryCommitBatch(
    HistoryCommitPack Pack,
    BackupRun Run,
    ImmutableArray<SourceVersion> NewVersions,
    ImmutableArray<VersionRepresentation> NewRepresentations,
    ImmutableArray<VersionMetadataSnapshot> NewMetadataSnapshots,
    ImmutableArray<SourceCheckpoint> NewCheckpoints,
    SafetySnapshot? NewSafetySnapshot,
    ImmutableArray<BranchUpdate> NewBranchUpdates,
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
    private readonly HistoryExactCheckpointAdmission _admission;

    public HistoryCommitCoordinator(HistoryRuntime runtime, HistoryPackCodec? codec = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _codec = codec ?? new HistoryPackCodec();
        _admission = new HistoryExactCheckpointAdmission(_runtime);
    }

    /// <summary>
    /// 检查工作区健康及请求来源的基线引用。未请求来源不阻断独立来源捕获。
    /// </summary>
    public async Task<IReadOnlyList<HistoryBoundaryRecaptureRequirement>> FindRequiredBoundaryRecapturesAsync(
        HistoryConfigSnapshot snapshot,
        IReadOnlyCollection<SourceId> plannedCaptureSources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(plannedCaptureSources);
        try { HistoryRestoreTransactionJournalStore.RequireRecovered(_runtime.Repository.Paths.TransactionsRoot); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { throw new HistoryCommitConflictException($"Workspace recovery is required before capture: {ex.Message}"); }

        if (snapshot.ConfigId != _runtime.ConfigId)
        {
            throw new ArgumentException("Config snapshot does not belong to this History Runtime.", nameof(snapshot));
        }

        if (_runtime.Health.HasFlag(HistoryRuntimeHealth.WorkspaceRecoveryRequired))
        {
            throw new HistoryCommitConflictException("History Workspace requires journal recovery before preflight.");
        }

        var workspaceLoad = await _runtime.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (workspaceLoad.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible)
        {
            throw new HistoryCommitConflictException(
                $"History Workspace state is {workspaceLoad.Status}: {workspaceLoad.Diagnostic}");
        }
        var workspace = workspaceLoad.Value;
        if (workspace is null)
        {
            return Array.Empty<HistoryBoundaryRecaptureRequirement>();
        }

        foreach (var sourceId in plannedCaptureSources.Distinct())
        {
            if (workspace.GetSourceState(sourceId).BaseVersionId is { } versionId)
                await RequireVersionForSourceAsync(versionId, sourceId, cancellationToken).ConfigureAwait(false);
        }

        return Array.Empty<HistoryBoundaryRecaptureRequirement>();
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
                await ValidateNewBranchIdentityAsync(request.BranchCreationIntent, request.AffectedSourceIds.Single(), cancellationToken).ConfigureAwait(false);
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
                await _runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
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
            if (request.SourceCaptureResults.Any(result => result.Outcome is
                    SourceCaptureOutcome.Unavailable
                    or SourceCaptureOutcome.Failed
                    or SourceCaptureOutcome.Canceled
                    or SourceCaptureOutcome.Blocked))
            {
                throw new HistoryCommitConflictException(
                    "An independent recovery point requires a reliable Exact result for every affected Source.");
            }
        }
        if (request.Protect && (request.AffectedSourceIds.Length != 1
            || request.SourceCaptureResults.Any(c => c.CaptureScope != CaptureScope.FullSource
                || c.EffectiveSourceBoundary.ScopeMode != EffectiveBoundaryScopeMode.All
                || c.EffectiveSourceBoundary.FilterMode != EffectiveBoundaryFilterMode.Blacklist
                || !c.EffectiveSourceBoundary.FilterRules.IsEmpty)))
            throw new HistoryCommitConflictException("Protected backup requires one complete, unfiltered source; partial scope is not supported.");
        var protectionAnnotations = new List<HistoryAnnotationUpdate>();
        var versions = ImmutableArray.CreateBuilder<SourceVersion>();
        var representations = ImmutableArray.CreateBuilder<VersionRepresentation>();
        var metadataSnapshots = ImmutableArray.CreateBuilder<VersionMetadataSnapshot>();
        var localEntries = new List<LocalReplicaCatalogEntry>();
        var checkpoints = ImmutableArray.CreateBuilder<SourceCheckpoint>();
        var branchUpdates = ImmutableArray.CreateBuilder<BranchUpdate>();
        var admissions = new List<HistoryDiagnostic>();
        var runSources = ImmutableArray.CreateBuilder<BackupRunSourceResult>();
        var nextBaselines = ImmutableArray.CreateBuilder<WorkspaceSourceBaseline>();

        foreach (var source in request.ConfigSnapshot.Sources.Where(source => resultMap.ContainsKey(source.SourceId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            baselineMap.TryGetValue(source.SourceId, out var baseline);
            resultMap.TryGetValue(source.SourceId, out var capture);
            var state = baseline ?? new WorkspaceSourceBaseline(source.SourceId, null, WorkspaceBaselineRelation.Unknown);
            var currentBranch = await ResolveCurrentBranchAsync(state, cancellationToken).ConfigureAwait(false);
            var ancestryId = state.CheckpointAncestryAnchorId ?? currentBranch?.TargetCheckpointId;
            var currentCheckpoint = ancestryId is { } anchor
                ? await _runtime.Query.GetCheckpointAsync(anchor, cancellationToken).ConfigureAwait(false)
                    ?? throw new HistoryCommitConflictException("Workspace Source ancestry checkpoint is missing.") : null;
            if (currentCheckpoint is not null && currentCheckpoint.SourceId != source.SourceId)
                throw new HistoryCommitConflictException("Workspace ancestry crosses Source identity.");
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

            bool captured = runOutcome == BackupRunSourceOutcome.Captured;
            bool successful = runOutcome is BackupRunSourceOutcome.Captured or BackupRunSourceOutcome.Reused;
            if (request.Protect)
            {
                if (!successful || finalVersionId is not { } protectedId)
                    throw new HistoryCommitConflictException("Protected backup has no successful version.");
                if (!captured)
                {
                    var existing = await RequireVersionForSourceAsync(protectedId, source.SourceId, cancellationToken).ConfigureAwait(false);
                    if (existing.CaptureScope != CaptureScope.FullSource || request.ValidateProtectedReuse is null
                        || !await request.ValidateProtectedReuse(protectedId, cancellationToken).ConfigureAwait(false))
                        throw new HistoryCommitConflictException("Protected reuse requires a Ready Exact version.");
                }
                var target = new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, protectedId.Value);
                var previous = await _runtime.Query.GetAnnotationUpdatesAsync(target, HistoryAnnotationKind.Pin, cancellationToken).ConfigureAwait(false);
                protectionAnnotations.Add(new HistoryAnnotationUpdate(AnnotationUpdateId.New(), target,
                    HistoryAnnotationKind.Pin, HistoryAnnotationProjection.FindTips(previous).Select(p => p.UpdateId), "true", now));
            }
            SourceCheckpoint? checkpoint = null;
            BranchUpdate? branchUpdate = null;
            if (successful && finalVersionId is { } resultVersion)
            {
                if (captured || request.SafetySnapshotIntent is not null
                    || currentCheckpoint?.VersionId != resultVersion)
                {
                    checkpoint = new SourceCheckpoint(CheckpointId.New(), request.ConfigSnapshot.ConfigId, now,
                        request.Invocation.RunId, request.Invocation.Provenance,
                        [new(source.SourceId, source.Descriptor, resultVersion, disposition, finalBoundary)],
                        currentCheckpoint is null ? [] : [currentCheckpoint.CheckpointId],
                        request.SafetySnapshotIntent is not null ? CheckpointCreationKind.SafetySnapshot : CheckpointCreationKind.Capture);
                    checkpoints.Add(checkpoint);
                }
                var candidate = checkpoint ?? currentCheckpoint;
                var admission = candidate is null ? null : await _admission.EvaluateAsync(candidate, versions, representations, cancellationToken).ConfigureAwait(false);
                if (request.Protect && admission?.IsReady != true)
                    throw new HistoryCommitConflictException("Protected backup requires an Exact admitted checkpoint.");
                if (request.SafetySnapshotIntent is not null && admission?.IsReady != true)
                    throw new HistoryCommitConflictException("SafetySnapshot requires Exact affected Source checkpoints.");
                if (admission is { IsReady: false })
                    admissions.Add(new("history.checkpoint.exact_admission_failed", HistoryDiagnosticSeverity.Warning, admission.Diagnostic));
                if (admission?.IsReady == true && request.Intent == HistoryCommitIntent.AdvanceBranch
                    && (checkpoint is not null || state.ActiveBranchId is null || request.BranchCreationIntent is not null))
                {
                    var branchId = request.BranchCreationIntent?.BranchId ?? state.ActiveBranchId ?? BranchId.New();
                    branchUpdate = new BranchUpdate(BranchUpdateId.New(), branchId,
                        state.ActiveBranchUpdateId is { } parentUpdate ? [parentUpdate] : [],
                        request.BranchCreationIntent?.Name ?? currentBranch?.Name ?? request.ConfigSnapshot.DefaultBranchName,
                        candidate!.CheckpointId, false, now,
                        request.BranchCreationIntent is not null || state.ActiveBranchId is null ? BranchUpdateReason.Created
                            : currentCheckpoint?.VersionId != reliableBaseline ? BranchUpdateReason.BackupFromHistoricalState : BranchUpdateReason.Backup,
                        sourceId: source.SourceId);
                    branchUpdates.Add(branchUpdate);
                    state = state with { ActiveBranchId = branchId, ActiveBranchUpdateId = branchUpdate.UpdateId,
                        CheckpointAncestryAnchorId = candidate.CheckpointId };
                }
                else if (request.Intent == HistoryCommitIntent.IndependentRecoveryPoint && candidate is not null)
                    state = state with { CheckpointAncestryAnchorId = candidate.CheckpointId };
                state = state with { BaseVersionId = resultVersion, Relation = nextRelation };
            }
            runSources.Add(new BackupRunSourceResult(source.SourceId, runOutcome,
                successful ? finalVersionId : null, capture?.Diagnostics ?? [],
                checkpoint?.CheckpointId ?? (successful && currentCheckpoint?.VersionId == finalVersionId ? currentCheckpoint?.CheckpointId : null),
                branchUpdate?.UpdateId ?? state.ActiveBranchUpdateId));
            nextBaselines.Add(state);
        }

        if (resultMap.Keys.Any(sourceId => request.ConfigSnapshot.Sources.All(source => source.SourceId != sourceId)))
            throw new HistoryCommitConflictException("A Source result is absent from the Config snapshot roster.");
        if (request.BranchCreationIntent is not null && request.AffectedSourceIds.Length != 1)
            throw new HistoryCommitConflictException("Branch creation capture must target exactly one Source.");
        SafetySnapshot? safetySnapshot = request.SafetySnapshotIntent is null ? null
            : new(SafetySnapshotId.New(), checkpoints.Select(c => c.CheckpointId), now, request.SafetySnapshotIntent.Reason);
        var allStates = baselineMap.Values.ToDictionary(s => s.SourceId);
        foreach (var state in nextBaselines) allStates[state.SourceId] = state;
        bool stateChanged = !nextBaselines.All(state => baselineMap.TryGetValue(state.SourceId, out var before) && before == state);
        HistoryWorkspace? updatedWorkspace = stateChanged ? new HistoryWorkspace(request.ConfigSnapshot.ConfigId,
            checked((workspace?.StateRevision ?? HistoryWorkspaceStore.MissingRevision) + 1),
            allStates.Values.OrderBy(state => state.SourceId.ToString(), StringComparer.Ordinal)) : null;
        var runOutcomeValue = runSources.All(r => r.Outcome == BackupRunSourceOutcome.Reused) ? BackupRunOutcome.NoChange
            : runSources.All(r => r.Outcome is BackupRunSourceOutcome.Captured or BackupRunSourceOutcome.Reused) ? BackupRunOutcome.Completed
            : runSources.Any(r => r.Outcome is BackupRunSourceOutcome.Captured or BackupRunSourceOutcome.Reused) ? BackupRunOutcome.Partial : BackupRunOutcome.Failed;
        var run = new BackupRun(request.Invocation.RunId, request.ConfigSnapshot.ConfigId, request.Invocation.StartedAtUtc,
            request.Invocation.CompletedAtUtc, request.Invocation.Kind, runOutcomeValue, runSources,
            request.SourceCaptureResults.SelectMany(result => result.Diagnostics).Concat(admissions));

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
        facts.AddRange(protectionAnnotations);
        facts.AddRange(representations);
        facts.AddRange(metadataSnapshots);
        facts.AddRange(checkpoints);
        if (safetySnapshot is not null) facts.Add(safetySnapshot);
        facts.AddRange(branchUpdates);
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
            checkpoints.ToImmutable(),
            safetySnapshot,
            branchUpdates.ToImmutable(),
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
                await RequireVersionForSourceAsync(baseVersionId, result.SourceId, cancellationToken).ConfigureAwait(false);
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
        WorkspaceSourceBaseline? workspace,
        CancellationToken cancellationToken)
    {
        if (workspace?.ActiveBranchId is not { } branchId
            || workspace.ActiveBranchUpdateId is not { } updateId)
        {
            return null;
        }

        var update = await _runtime.Query.GetBranchUpdateAsync(updateId, cancellationToken).ConfigureAwait(false)
            ?? throw new HistoryCommitConflictException("Workspace ActiveBranchUpdateId does not exist.");
        if (update.SourceId != workspace.SourceId || update.BranchId != branchId || update.IsDeleted)
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
        SourceId sourceId,
        CancellationToken cancellationToken)
    {
        var updates = await _runtime.Query.GetAllBranchUpdatesAsync(cancellationToken).ConfigureAwait(false);
        if (updates.Any(update => update.BranchId == intent.BranchId))
        {
            throw new HistoryCommitConflictException("New BranchId already exists.");
        }
        var tips = HistoryBranchProjection.Build(updates);
        if (tips.Any(branch => !branch.IsDeleted
                               && branch.Tips.Any(tip => tip.SourceId == sourceId && string.Equals(
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

    private static bool WorkspaceEquals(HistoryWorkspace left, HistoryWorkspace right) => HistoryWorkspace.StateEquals(left, right);





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

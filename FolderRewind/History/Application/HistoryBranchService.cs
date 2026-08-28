using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed record HistoryBranchCommandResult(
    PackId PackId,
    BranchUpdate BranchUpdate,
    ConfigurationCheckpoint? CreatedCheckpoint,
    bool Activated,
    bool IndexRefreshSucceeded);

public sealed class HistoryBranchCommandException(string message) : Exception(message);

public enum HistoryWorkingStateStatus
{
    ExactBaseline = 0,
    Dirty = 1,
    Unknown = 2
}

public sealed class HistoryBranchService
{
    private readonly HistoryRuntime _runtime;
    private readonly HistoryPackCodec _codec;

    public HistoryBranchService(HistoryRuntime runtime, HistoryPackCodec? codec = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _codec = codec ?? new HistoryPackCodec();
    }

    /// <summary>Create a dormant Branch at a historical Checkpoint. It does not restore or alter Workspace.</summary>
    public async Task<HistoryBranchCommandResult> CreateFromCheckpointAsync(
        CheckpointId checkpointId,
        string name,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _runtime.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await _runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        var checkpoint = await _runtime.Query.GetCheckpointAsync(checkpointId, cancellationToken).ConfigureAwait(false)
            ?? throw new HistoryBranchCommandException($"Checkpoint {checkpointId} does not exist.");
        if (checkpoint.ConfigId != _runtime.ConfigId)
        {
            throw new HistoryBranchCommandException("Checkpoint belongs to another Config.");
        }
        var branchName = NormalizeName(name);
        var branches = await LoadBranchesAsync(cancellationToken).ConfigureAwait(false);
        EnsureUniqueName(branches, branchName);
        var update = new BranchUpdate(
            BranchUpdateId.New(),
            BranchId.New(),
            [],
            branchName,
            checkpointId,
            isDeleted: false,
            DateTimeOffset.UtcNow,
            BranchUpdateReason.Created);
        var committed = await HistoryCommandCommitter.CommitInsideGateAsync(
            _runtime, _codec, [update], null, null, cancellationToken).ConfigureAwait(false);
        return new(committed.Pack.PackId, update, null, Activated: false, committed.IndexRefreshSucceeded);
    }

    /// <summary>
    /// Create and activate a Branch for the exact current Workspace vector. Reuses an existing exact
    /// Checkpoint when possible; otherwise appends an aggregate Checkpoint without manufacturing Versions.
    /// </summary>
    public async Task<HistoryBranchCommandResult> CreateFromCurrentStateAsync(
        HistoryConfigSnapshot configSnapshot,
        HistoryWorkspace expectedWorkspace,
        string name,
        HistoryProvenance provenance,
        HistoryWorkingStateStatus workingStateStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configSnapshot);
        ArgumentNullException.ThrowIfNull(expectedWorkspace);
        ArgumentNullException.ThrowIfNull(provenance);
        if (configSnapshot.ConfigId != _runtime.ConfigId || expectedWorkspace.ConfigId != _runtime.ConfigId)
        {
            throw new HistoryBranchCommandException("Config or Workspace identity does not match this Runtime.");
        }
        if (workingStateStatus != HistoryWorkingStateStatus.ExactBaseline)
        {
            throw new HistoryBranchCommandException(
                "Current files are dirty or unknown; use CaptureCurrentStateForBranch before creating the Branch.");
        }

        await using var lease = await _runtime.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await _runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        var currentWorkspace = await RequireExpectedWorkspaceAsync(expectedWorkspace, cancellationToken).ConfigureAwait(false);
        var sourceMap = configSnapshot.Sources.ToDictionary(source => source.SourceId);
        if (currentWorkspace.SourceBaselines.Length != sourceMap.Count
            || currentWorkspace.SourceBaselines.Any(baseline =>
                !sourceMap.ContainsKey(baseline.SourceId)
                || baseline.Relation != WorkspaceBaselineRelation.Exact
                || baseline.BaseVersionId is null))
        {
            throw new HistoryBranchCommandException(
                "Current Workspace does not have an Exact baseline for every Config Source; capture current state first.");
        }

        var branchName = NormalizeName(name);
        var branches = await LoadBranchesAsync(cancellationToken).ConfigureAwait(false);
        EnsureUniqueName(branches, branchName);
        var vector = currentWorkspace.SourceBaselines.ToDictionary(item => item.SourceId, item => item.BaseVersionId);
        var checkpoints = await _runtime.Query.GetAllCheckpointsAsync(cancellationToken).ConfigureAwait(false);
        var exactCheckpoint = checkpoints
            .Where(checkpoint => checkpoint.ConfigId == _runtime.ConfigId && CheckpointMatches(checkpoint, vector))
            .OrderByDescending(checkpoint => checkpoint.CreatedAtUtc)
            .ThenByDescending(checkpoint => checkpoint.CheckpointId.ToString(), StringComparer.Ordinal)
            .FirstOrDefault();
        ConfigurationCheckpoint? aggregate = null;
        if (exactCheckpoint is null)
        {
            aggregate = new ConfigurationCheckpoint(
                CheckpointId.New(),
                _runtime.ConfigId,
                DateTimeOffset.UtcNow,
                createdByRunId: null,
                provenance,
                configSnapshot.Sources.Select(source => new CheckpointSource(
                    source.SourceId,
                    source.Descriptor,
                    vector[source.SourceId],
                    CheckpointSourceDisposition.CarriedForward)));
            exactCheckpoint = aggregate;
        }

        var update = new BranchUpdate(
            BranchUpdateId.New(),
            BranchId.New(),
            [],
            branchName,
            exactCheckpoint.CheckpointId,
            isDeleted: false,
            DateTimeOffset.UtcNow,
            BranchUpdateReason.Created);
        var updatedWorkspace = new HistoryWorkspace(
            _runtime.ConfigId,
            checked(currentWorkspace.StateRevision + 1),
            update.BranchId,
            update.UpdateId,
            currentWorkspace.SourceBaselines);
        var facts = aggregate is null ? new object[] { update } : [aggregate, update];
        var committed = await HistoryCommandCommitter.CommitInsideGateAsync(
            _runtime,
            _codec,
            facts,
            currentWorkspace,
            updatedWorkspace,
            cancellationToken).ConfigureAwait(false);
        return new(committed.Pack.PackId, update, aggregate, Activated: true, committed.IndexRefreshSucceeded);
    }

    /// <summary>Capture dirty current state and atomically create/activate a new Branch without moving the old Branch.</summary>
    public Task<HistoryCommitBatch> CaptureCurrentStateForBranchAsync(
        HistoryCommitRequest captureRequest,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(captureRequest);
        if (captureRequest.BranchCreationIntent is not null)
        {
            throw new HistoryBranchCommandException("Capture request already contains a Branch creation intent.");
        }
        var request = new HistoryCommitRequest(
            captureRequest.ConfigSnapshot,
            captureRequest.Invocation,
            captureRequest.ExpectedWorkspace,
            captureRequest.SourceCaptureResults,
            new HistoryBranchCreationIntent(BranchId.New(), NormalizeName(name)));
        return _runtime.Commit.CommitAsync(request, cancellationToken);
    }

    public async Task<HistoryBranchCommandResult> RenameAsync(
        BranchId branchId,
        string newName,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _runtime.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await _runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        var branches = await LoadBranchesAsync(cancellationToken).ConfigureAwait(false);
        var branch = RequireSingleActiveTip(branches, branchId, "rename");
        var name = NormalizeName(newName);
        EnsureUniqueName(branches, name, branchId);
        var tip = branch.Tips[0];
        var update = new BranchUpdate(
            BranchUpdateId.New(), branchId, [tip.UpdateId], name, tip.TargetCheckpointId,
            isDeleted: false, DateTimeOffset.UtcNow, BranchUpdateReason.Renamed);
        var workspace = await LoadWorkspaceAsync(cancellationToken).ConfigureAwait(false);
        HistoryWorkspace? updatedWorkspace = null;
        if (workspace?.ActiveBranchId == branchId)
        {
            if (workspace.ActiveBranchUpdateId != tip.UpdateId)
            {
                throw new HistoryBranchCommandException("Active Workspace does not point at the current Branch tip.");
            }
            updatedWorkspace = new HistoryWorkspace(
                _runtime.ConfigId,
                checked(workspace.StateRevision + 1),
                branchId,
                update.UpdateId,
                workspace.SourceBaselines);
        }
        var committed = await HistoryCommandCommitter.CommitInsideGateAsync(
            _runtime, _codec, [update], workspace, updatedWorkspace, cancellationToken).ConfigureAwait(false);
        return new(committed.Pack.PackId, update, null, updatedWorkspace is not null, committed.IndexRefreshSucceeded);
    }

    public async Task<HistoryBranchCommandResult> DeleteAsync(
        BranchId branchId,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _runtime.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await _runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        var branches = await LoadBranchesAsync(cancellationToken).ConfigureAwait(false);
        var branch = RequireSingleActiveTip(branches, branchId, "delete");
        var workspace = await LoadWorkspaceAsync(cancellationToken).ConfigureAwait(false);
        if (workspace?.ActiveBranchId == branchId)
        {
            throw new HistoryBranchCommandException("The active Branch cannot be deleted.");
        }
        if (branches.Count(candidate => !candidate.IsDeleted) <= 1)
        {
            throw new HistoryBranchCommandException("The last effective Branch cannot be deleted.");
        }
        var tip = branch.Tips[0];
        var update = new BranchUpdate(
            BranchUpdateId.New(), branchId, [tip.UpdateId], tip.Name, tip.TargetCheckpointId,
            isDeleted: true, DateTimeOffset.UtcNow, BranchUpdateReason.Deleted);
        var committed = await HistoryCommandCommitter.CommitInsideGateAsync(
            _runtime, _codec, [update], null, null, cancellationToken).ConfigureAwait(false);
        return new(committed.Pack.PackId, update, null, Activated: false, committed.IndexRefreshSucceeded);
    }

    private async Task<ImmutableArray<HistoryBranchState>> LoadBranchesAsync(CancellationToken cancellationToken)
        => HistoryBranchProjection.Build(
            await _runtime.Query.GetAllBranchUpdatesAsync(cancellationToken).ConfigureAwait(false));

    private static HistoryBranchState RequireSingleActiveTip(
        IEnumerable<HistoryBranchState> branches,
        BranchId branchId,
        string operation)
    {
        var branch = branches.SingleOrDefault(candidate => candidate.BranchId == branchId)
            ?? throw new HistoryBranchCommandException($"Branch {branchId} does not exist.");
        if (branch.IsMultiTip)
        {
            throw new HistoryBranchCommandException($"Cannot {operation} a multi-tip Branch before reconciliation.");
        }
        if (branch.IsDeleted)
        {
            throw new HistoryBranchCommandException($"Cannot {operation} a deleted Branch.");
        }
        return branch;
    }

    private static void EnsureUniqueName(
        IEnumerable<HistoryBranchState> branches,
        string name,
        BranchId? exceptBranchId = null)
    {
        if (branches.Any(branch => branch.BranchId != exceptBranchId
                                   && !branch.IsDeleted
                                   && branch.Tips.Any(tip => !tip.IsDeleted && string.Equals(
                                       tip.Name,
                                       name,
                                       StringComparison.OrdinalIgnoreCase))))
        {
            throw new HistoryBranchCommandException($"Active Branch name '{name}' already exists on this device.");
        }
    }

    private static string NormalizeName(string name)
        => string.IsNullOrWhiteSpace(name)
            ? throw new HistoryBranchCommandException("Branch name cannot be empty.")
            : name.Trim();

    private async Task<HistoryWorkspace> RequireExpectedWorkspaceAsync(
        HistoryWorkspace expected,
        CancellationToken cancellationToken)
    {
        var actual = await LoadWorkspaceAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new HistoryBranchCommandException("Workspace is missing.");
        if (!WorkspaceEquals(expected, actual))
        {
            throw new HistoryBranchCommandException("Workspace changed before Branch creation.");
        }
        return actual;
    }

    private async Task<HistoryWorkspace?> LoadWorkspaceAsync(CancellationToken cancellationToken)
    {
        var load = await _runtime.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (load.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible)
        {
            throw new HistoryBranchCommandException($"Workspace recovery is required: {load.Diagnostic}");
        }
        return load.Value;
    }

    private static bool WorkspaceEquals(HistoryWorkspace left, HistoryWorkspace right)
        => left.ConfigId == right.ConfigId
            && left.StateRevision == right.StateRevision
            && left.ActiveBranchId == right.ActiveBranchId
            && left.ActiveBranchUpdateId == right.ActiveBranchUpdateId
            && left.SourceBaselines.OrderBy(item => item.SourceId.ToString(), StringComparer.Ordinal)
                .SequenceEqual(right.SourceBaselines.OrderBy(item => item.SourceId.ToString(), StringComparer.Ordinal));

    private static bool CheckpointMatches(
        ConfigurationCheckpoint checkpoint,
        IReadOnlyDictionary<SourceId, VersionId?> vector)
        => checkpoint.Sources.Length == vector.Count
            && checkpoint.Sources.All(source => vector.TryGetValue(source.SourceId, out var versionId)
                                                && source.VersionId == versionId);

}

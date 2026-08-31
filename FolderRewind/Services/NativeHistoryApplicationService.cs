using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static class NativeHistoryApplicationService
{
    public static async Task<IReadOnlyList<SafetySnapshotProjection>> GetSafetySnapshotsAsync(
        BackupConfig config,
        bool activeOnly = true,
        CancellationToken cancellationToken = default)
    {
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        await runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        return await new SafetySnapshotService(runtime).QueryAsync(activeOnly, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<HistoryRestoreResult> RestoreSafetySnapshotAsync(
        BackupConfig config,
        SafetySnapshotId snapshotId,
        BackupService.RestoreMode requestedMode,
        CancellationToken cancellationToken = default)
    {
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        await runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = (await runtime.Query.GetSafetySnapshotsAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.SnapshotId == snapshotId)
            ?? throw new InvalidOperationException("SafetySnapshot does not exist.");
        return await RestoreCheckpointAsync(
            config,
            snapshot.CheckpointId,
            completeCheckpoint: true,
            requestedMode,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> ReleaseSafetySnapshotAsync(
        BackupConfig config,
        SafetySnapshotId snapshotId,
        CancellationToken cancellationToken = default)
    {
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        return await new SafetySnapshotService(runtime).ReleaseAsync(snapshotId, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<HistoryRestoreResult> QuickRestoreAsync(
        BackupConfig config,
        ManagedFolder folder,
        CancellationToken cancellationToken = default)
    {
        NativeHostMutationContext.ThrowIfNestedMutation();
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var restore = CreateRestoreService(config, runtime);
        var resolution = await new HistoryQuickRestoreResolver(runtime, restore).ResolveAsync(
            Source(folder),
            AssessmentDepth.Deep,
            cancellationToken).ConfigureAwait(false);
        if (!resolution.IsReady || resolution.VersionId is null)
            return Blocked(resolution.Diagnostic);
        if (await BackupService.DeepProbeWorkspaceVersionAsync(
            config,
            folder,
            resolution.VersionId.Value,
            cancellationToken).ConfigureAwait(false))
        {
            return new HistoryRestoreResult(
                HistoryRestoreStatus.NoChanges,
                "Already at the active Branch's latest committed state.",
                false,
                []);
        }

        return await new NativeHistoryRestoreOrchestrator().ExecuteAsync(
            config,
            [folder],
            resolution.VersionId.Value.ToString(),
            token => RestoreVersionCoreAsync(
                config,
                folder,
                resolution.VersionId.Value,
                BackupService.RestoreMode.Clean,
                token,
                requireSafetySnapshot: true),
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<HistoryRestoreResult> RestoreVersionAsync(
        BackupConfig config,
        ManagedFolder folder,
        VersionId versionId,
        BackupService.RestoreMode requestedMode,
        CancellationToken cancellationToken = default)
    {
        NativeHostMutationContext.ThrowIfNestedMutation();
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var restore = CreateRestoreService(config, runtime);
        var requiredFidelity = requestedMode == BackupService.RestoreMode.Clean
            ? MaterializationFidelity.Exact
            : MaterializationFidelity.Partial;
        var assessment = await restore.AssessVersionAsync(
            versionId, requiredFidelity, AssessmentDepth.Deep, cancellationToken).ConfigureAwait(false);
        if (assessment.Readiness != HistoryReadiness.Ready || assessment.Selected is null)
            return Blocked($"Restore target is not Ready with {requiredFidelity} fidelity.");
        return await new NativeHistoryRestoreOrchestrator().ExecuteAsync(
            config,
            [folder],
            versionId.ToString(),
            token => RestoreVersionCoreAsync(config, folder, versionId, requestedMode, token),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HistoryRestoreResult> RestoreVersionCoreAsync(
        BackupConfig config,
        ManagedFolder folder,
        VersionId versionId,
        BackupService.RestoreMode requestedMode,
        CancellationToken cancellationToken = default,
        bool requireSafetySnapshot = false)
    {
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var workspace = await RequireWorkspaceAsync(runtime, cancellationToken).ConfigureAwait(false);
        if (requireSafetySnapshot || config.Archive.BackupBeforeRestore)
        {
            var protection = await ProtectBeforeRestoreAsync(config, runtime, cancellationToken).ConfigureAwait(false);
            if (protection.Result is not null) return protection.Result;
            workspace = protection.Workspace!;
        }
        var result = await CreateRestoreService(config, runtime).RestoreVersionAsync(
            versionId,
            Binding(config, folder),
            workspace,
            MapRestoreMode(requestedMode),
            cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
            await BackupService.SynchronizeCaptureBaselinesWithWorkspaceAsync(
                config, result.AppliedSources, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public static async Task<HistoryRestoreResult> RestoreCheckpointAsync(
        BackupConfig config,
        CheckpointId checkpointId,
        bool completeCheckpoint,
        BackupService.RestoreMode requestedMode,
        CancellationToken cancellationToken = default)
    {
        NativeHostMutationContext.ThrowIfNestedMutation();
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        await runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        var checkpoint = await runtime.Query.GetCheckpointAsync(checkpointId, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null) return Blocked("Checkpoint does not exist.");
        var currentIds = config.SourceFolders.Select(Source).ToHashSet();
        if (completeCheckpoint && checkpoint.Sources.Any(item => !currentIds.Contains(item.SourceId)))
            return Blocked("Complete restore requires explicit historical Source binding repair.");
        var affected = config.SourceFolders
            .Where(folder => checkpoint.Sources.Any(item => item.VersionId is not null && item.SourceId == Source(folder)))
            .ToArray();
        var restore = CreateRestoreService(config, runtime);
        var requiredFidelity = requestedMode == BackupService.RestoreMode.Clean
            ? MaterializationFidelity.Exact
            : MaterializationFidelity.Partial;
        foreach (var source in checkpoint.Sources.Where(item => item.VersionId is not null && currentIds.Contains(item.SourceId)))
        {
            var assessment = await restore.AssessVersionAsync(
                source.VersionId!.Value, requiredFidelity, AssessmentDepth.Deep, cancellationToken).ConfigureAwait(false);
            if (assessment.Readiness != HistoryReadiness.Ready || assessment.Selected is null)
                return Blocked($"Checkpoint Source {source.SourceId} is not Ready.");
        }
        return await new NativeHistoryRestoreOrchestrator().ExecuteAsync(
            config,
            affected,
            checkpointId.ToString(),
            token => RestoreCheckpointCoreAsync(
                config, checkpointId, completeCheckpoint, requestedMode, token),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HistoryRestoreResult> RestoreCheckpointCoreAsync(
        BackupConfig config,
        CheckpointId checkpointId,
        bool completeCheckpoint,
        BackupService.RestoreMode requestedMode,
        CancellationToken cancellationToken = default)
    {
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var workspace = await RequireWorkspaceAsync(runtime, cancellationToken).ConfigureAwait(false);
        var checkpoint = await runtime.Query.GetCheckpointAsync(checkpointId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Checkpoint does not exist.");
        var restorableSources = checkpoint.Sources
            .Where(item => item.VersionId is not null)
            .Select(item => item.SourceId)
            .ToHashSet();
        var bindings = config.SourceFolders
            .Where(folder => completeCheckpoint || restorableSources.Contains(Source(folder)))
            .Select(folder => Binding(config, folder))
            .ToArray();
        if (config.Archive.BackupBeforeRestore)
        {
            var protection = await ProtectBeforeRestoreAsync(config, runtime, cancellationToken).ConfigureAwait(false);
            if (protection.Result is not null) return protection.Result;
            workspace = protection.Workspace!;
        }
        var result = await CreateRestoreService(config, runtime).RestoreCheckpointAsync(
            checkpointId,
            bindings,
            workspace,
            completeCheckpoint
                ? HistoryCheckpointRestoreScope.CompleteCheckpoint
                : HistoryCheckpointRestoreScope.AvailableMappedSources,
            MapRestoreMode(requestedMode),
            cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
            await BackupService.SynchronizeCaptureBaselinesWithWorkspaceAsync(
                config, result.AppliedSources, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public static async Task<HistoryRestoreResult> CheckoutAsync(
        BackupConfig config,
        BranchUpdateId selectedTipId,
        CancellationToken cancellationToken = default)
    {
        NativeHostMutationContext.ThrowIfNestedMutation();
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var workspace = await RequireWorkspaceAsync(runtime, cancellationToken).ConfigureAwait(false);
        var restore = CreateRestoreService(config, runtime);
        var bindings = config.SourceFolders.Select(folder => Binding(config, folder)).ToArray();
        var plan = await new HistoryCheckoutPlanner(runtime, restore).BuildAsync(
            selectedTipId,
            bindings,
            workspace,
            AssessmentDepth.Deep,
            cancellationToken).ConfigureAwait(false);
        if (!plan.CanExecute)
            return new HistoryRestoreResult(
                HistoryRestoreStatus.BlockedBeforeMutation,
                plan.Diagnostic,
                false,
                [],
                plan);
        var restoreIds = plan.Sources
            .Where(item => item.Action == HistoryCheckoutSourceAction.Restore)
            .Select(item => item.SourceId)
            .ToHashSet();
        var affected = config.SourceFolders.Where(folder => restoreIds.Contains(Source(folder))).ToArray();
        return await new NativeHistoryRestoreOrchestrator().ExecuteAsync(
            config,
            affected,
            selectedTipId.ToString(),
            token => CheckoutCoreAsync(config, selectedTipId, token),
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<HistoryCheckoutPlan> PlanCheckoutAsync(
        BackupConfig config,
        BranchUpdateId selectedTipId,
        AssessmentDepth assessmentDepth = AssessmentDepth.Deep,
        CancellationToken cancellationToken = default)
    {
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var workspace = await RequireWorkspaceAsync(runtime, cancellationToken).ConfigureAwait(false);
        var plan = await new HistoryCheckoutPlanner(runtime, CreateRestoreService(config, runtime)).BuildAsync(
            selectedTipId,
            config.SourceFolders.Select(folder => Binding(config, folder)).ToArray(),
            workspace,
            assessmentDepth,
            cancellationToken).ConfigureAwait(false);
        if (plan.Readiness is not (HistoryCheckoutReadiness.Ready
            or HistoryCheckoutReadiness.ProtectionRequired
            or HistoryCheckoutReadiness.PreparationRequired)) return plan;
        // Planner 只判断 History 数据；Host 在展示执行入口前还必须验证必需的插件协调器。
        if (NativeHistoryRestoreOrchestrator.IsCoordinatorAvailable(config, out var diagnostic)) return plan;
        return plan with
        {
            Readiness = HistoryCheckoutReadiness.CoordinatorUnavailable,
            Diagnostic = diagnostic
        };
    }

    public static async Task<HistoryCheckoutPlan> PrepareCheckoutAsync(
        BackupConfig config,
        BranchUpdateId selectedTipId,
        CancellationToken cancellationToken = default)
    {
        var initial = await PlanCheckoutAsync(
            config,
            selectedTipId,
            AssessmentDepth.Fast,
            cancellationToken).ConfigureAwait(false);
        if (initial.Readiness != HistoryCheckoutReadiness.PreparationRequired
            || initial.Checkpoint is null) return initial;

        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var restore = CreateRestoreService(config, runtime);
        var allRepresentations = await runtime.Query.GetAllRepresentationsAsync(cancellationToken).ConfigureAwait(false);
        var representationsById = allRepresentations.ToDictionary(item => item.RepresentationId);
        var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Value;
        var preparedRepresentationIds = new HashSet<RepresentationId>();
        // 准备是用户显式动作：按依赖优先顺序下载，历史列表刷新本身绝不触发网络副作用。
        foreach (var source in initial.Checkpoint.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.VersionId is not { } versionId) continue;
            var folder = config.SourceFolders.Single(item => Source(item) == source.SourceId);
            var assessment = await restore.AssessVersionAsync(
                versionId,
                MaterializationFidelity.Exact,
                AssessmentDepth.Fast,
                cancellationToken).ConfigureAwait(false);
            if (assessment.Readiness != HistoryReadiness.PreparationRequired
                || assessment.Selected is null) continue;

            foreach (var representation in DependencyFirstClosure(
                         representationsById[assessment.Selected.RepresentationId],
                         representationsById))
            {
                if (!preparedRepresentationIds.Add(representation.RepresentationId)) continue;
                var alreadyLocal = (catalog?.Entries ?? []).Any(item =>
                    item.RepresentationId == representation.RepresentationId
                    && item.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath
                    && File.Exists(item.Locator.AbsolutePath));
                if (alreadyLocal) continue;
                var fileName = representation.RepresentationSpecificMetadata.GetValueOrDefault("fileName")
                    ?? representation.RepresentationSpecificMetadata.GetValueOrDefault("legacyFileName")
                    ?? $"{representation.RepresentationId}.7z";
                if (!await CloudSyncService.DownloadRepresentationAsync(
                        config,
                        folder,
                        representation.RepresentationId,
                        fileName,
                        cancellationToken).ConfigureAwait(false))
                {
                    return initial with
                    {
                        Readiness = HistoryCheckoutReadiness.ExactRepresentationUnavailable,
                        Diagnostic = $"Failed to prepare Exact representation {representation.RepresentationId}."
                    };
                }
            }
        }
        return await PlanCheckoutAsync(config, selectedTipId, AssessmentDepth.Deep, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<HistoryRestoreResult> CheckoutCoreAsync(
        BackupConfig config,
        BranchUpdateId selectedTipId,
        CancellationToken cancellationToken = default)
    {
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var workspace = await RequireWorkspaceAsync(runtime, cancellationToken).ConfigureAwait(false);
        var restore = CreateRestoreService(config, runtime);
        var bindings = config.SourceFolders
            .Select(folder => Binding(config, folder))
            .ToArray();
        var result = await new HistoryCheckoutService(
            runtime,
            restore,
            new SafetySnapshotWorkingStateProtector(config, runtime)).CheckoutAsync(
            selectedTipId,
            bindings,
            workspace,
            HistoryCheckoutProtectionMode.ProtectCurrentWork,
            cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
            await BackupService.SynchronizeCaptureBaselinesWithWorkspaceAsync(
                config, result.AppliedSources, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public static async Task ApplyAutomaticRetentionAsync(
        BackupConfig config,
        CancellationToken cancellationToken = default)
    {
        // KeepCount=0 是用户配置层的“无限保留”哨兵；任何入口都不得把它下传为“保留 0 个”。
        if (config.Archive.KeepCount <= 0)
            return;

        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var archive = new SevenZipHistoryArchiveBackend(config);
        var representations = new RepresentationRuntime(
        [
            new CoreArchiveRepresentationHandler(archive),
            new SmartDeltaRepresentationHandler(archive)
        ]);
        async Task<IRepresentationEnvironment> Environment(CancellationToken token)
            => await BuildEnvironmentAsync(runtime, token).ConfigureAwait(false);
        var payloads = new FileSystemHistoryLocalPayloadStore();
        var planner = new HistoryRetentionPlanner(runtime, representations, Environment, payloads);
        var plan = await planner.PlanAsync(
            new HistoryRetentionRequest(
                config.Archive.KeepCount,
                HistoryRetentionOperationRoots.Empty,
                allowPostMigrationCleanup: true),
            cancellationToken).ConfigureAwait(false);
        if (!plan.CanExecute)
        {
            LogService.LogWarning(
                "[Retention] " + string.Join(" ", plan.Blockers),
                nameof(NativeHistoryApplicationService));
            return;
        }
        var executor = new HistoryRetentionExecutor(
            runtime,
            planner,
            representations,
            Environment,
            archive,
            payloads,
            new ArtifactLedgerGarbageCollector(config));
        var result = await executor.ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            LogService.LogWarning(
                "[Retention] " + result.Diagnostic,
                nameof(NativeHistoryApplicationService));
        }
    }

    public static async Task ReleaseVersionAsync(
        BackupConfig config,
        VersionId versionId,
        CancellationToken cancellationToken = default)
    {
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        await runtime.MaterializationPolicies.SetAsync(
            versionId,
            MaterializationPolicyState.Released,
            "Explicit user release",
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<HistoryTargetedReplicaDeletionResult> DeleteVersionLocalPayloadAsync(
        BackupConfig config,
        VersionId versionId,
        RepresentationId representationId,
        string localPath,
        bool releaseVersion,
        CancellationToken cancellationToken = default)
    {
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        await runtime.MaterializationPolicies.EnsureCanReleaseAsync(versionId, cancellationToken)
            .ConfigureAwait(false);

        var releaseCommitted = false;
        try
        {
            if (releaseVersion)
            {
                await ReleaseVersionAsync(config, versionId, cancellationToken).ConfigureAwait(false);
                releaseCommitted = true;
            }

            return await new HistoryLocalReplicaMaintenanceService(runtime)
                .DeleteControlledReplicaAsync(
                    versionId,
                    representationId,
                    localPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception deleteError) when (releaseCommitted)
        {
            try
            {
                await runtime.MaterializationPolicies.SetAsync(
                    versionId,
                    MaterializationPolicyState.Retained,
                    "Compensate failed targeted local deletion",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception compensationError)
            {
                throw new AggregateException(
                    "Targeted local deletion failed and the Version release could not be compensated.",
                    deleteError,
                    compensationError);
            }
            throw;
        }
    }

    public static async Task<HistoryRestoreService> CreateRestoreServiceAsync(
        BackupConfig config,
        CancellationToken cancellationToken = default)
    {
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        await runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        return CreateRestoreService(config, runtime);
    }

    private static HistoryRestoreService CreateRestoreService(BackupConfig config, HistoryRuntime runtime)
    {
        var archive = new SevenZipHistoryArchiveBackend(config);
        var representations = new RepresentationRuntime(
        [
            new CoreArchiveRepresentationHandler(archive),
            new SmartDeltaRepresentationHandler(archive)
        ]);
        return new HistoryRestoreService(
            runtime,
            representations,
            token => BuildEnvironmentAsync(runtime, token),
            new FileSystemHistoryRestoreMutationBackend());
    }

    private static async Task<IRepresentationEnvironment> BuildEnvironmentAsync(
        HistoryRuntime runtime,
        CancellationToken cancellationToken)
    {
        var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Value;
        var representations = await runtime.Query.GetAllRepresentationsAsync(cancellationToken).ConfigureAwait(false);
        var replicas = new List<StorageReplica>();
        var active = new List<ReplicaId>();
        foreach (var representation in representations)
        {
            foreach (var replica in await runtime.Query.GetStorageReplicasAsync(
                         representation.RepresentationId,
                         cancellationToken).ConfigureAwait(false))
            {
                replicas.Add(replica);
                var updates = await runtime.Query.GetReplicaLifecycleUpdatesAsync(replica.ReplicaId, cancellationToken)
                    .ConfigureAwait(false);
                var parents = updates.SelectMany(item => item.ParentUpdateIds).ToHashSet();
                if (updates.Where(item => !parents.Contains(item.UpdateId))
                    .Any(item => item.State == ReplicaLifecycleState.Active))
                {
                    active.Add(replica.ReplicaId);
                }
            }
        }
        return new RepresentationEnvironment(catalog?.Entries, replicas, active);
    }

    private static async Task<HistoryWorkspace> RequireWorkspaceAsync(
        HistoryRuntime runtime,
        CancellationToken cancellationToken)
    {
        var load = await runtime.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (load.Status != DeviceLocalStateStatus.Valid || load.Value is null)
            throw new DeviceLocalStateConflictException("Workspace requires recovery before restore.");
        return load.Value;
    }

    private static async Task<(HistoryWorkspace? Workspace, HistoryRestoreResult? Result)> ProtectBeforeRestoreAsync(
        BackupConfig config,
        HistoryRuntime runtime,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await BackupService.CreateSafetySnapshotAsync(
                config,
                SafetySnapshotReason.BeforeRestore,
                cancellationToken).ConfigureAwait(false);
            return (await RequireWorkspaceAsync(runtime, cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (null, new HistoryRestoreResult(
                HistoryRestoreStatus.BlockedBeforeMutation,
                "Required BeforeRestore SafetySnapshot was canceled; target mutation did not start.",
                false,
                []));
        }
        catch (Exception ex)
        {
            return (null, new HistoryRestoreResult(
                HistoryRestoreStatus.BlockedBeforeMutation,
                $"Required BeforeRestore SafetySnapshot failed: {ex.Message}",
                false,
                []));
        }
    }

    private static HistoryRestoreResult Blocked(string diagnostic)
        => new(HistoryRestoreStatus.BlockedBeforeMutation, diagnostic, false, []);

    private static SourceId Source(ManagedFolder folder)
        => Guid.TryParse(folder.Id, out var id) && id != Guid.Empty
            ? new SourceId(id)
            : throw new InvalidDataException("ManagedFolder has no stable SourceId.");

    private static HistoryRestoreSourceBinding Binding(BackupConfig config, ManagedFolder folder)
        => new(
            Source(folder),
            folder.Path,
            EffectiveSourceBoundaryFactory.Create(folder.Path, folder.SourceScope, config.Filters));

    private static IReadOnlyList<VersionRepresentation> DependencyFirstClosure(
        VersionRepresentation root,
        IReadOnlyDictionary<RepresentationId, VersionRepresentation> representations)
    {
        var result = new List<VersionRepresentation>();
        var visited = new HashSet<RepresentationId>();
        void Visit(VersionRepresentation representation)
        {
            if (!visited.Add(representation.RepresentationId)) return;
            foreach (var dependencyId in representation.DependencyRepresentationIds)
            {
                if (representations.TryGetValue(dependencyId, out var dependency)) Visit(dependency);
            }
            result.Add(representation);
        }
        Visit(root);
        return result;
    }

    private sealed class SafetySnapshotWorkingStateProtector(
        BackupConfig config,
        HistoryRuntime runtime) : IHistoryWorkingStateProtector
    {
        public async Task<HistoryWorkspace> ProtectAsync(
            HistoryWorkspace expectedWorkspace,
            CancellationToken cancellationToken)
        {
            var current = await RequireWorkspaceAsync(runtime, cancellationToken).ConfigureAwait(false);
            if (!HistoryRestoreTransactionJournalStore.WorkspaceEquals(current, expectedWorkspace))
                throw new DeviceLocalStateConflictException("Workspace changed before SafetySnapshot capture.");
            _ = await BackupService.CreateSafetySnapshotAsync(
                config,
                SafetySnapshotReason.BeforeCheckout,
                cancellationToken).ConfigureAwait(false);
            return await RequireWorkspaceAsync(runtime, cancellationToken).ConfigureAwait(false);
        }
    }

    private static HistoryRestoreApplyMode MapRestoreMode(BackupService.RestoreMode mode)
        => mode == BackupService.RestoreMode.Clean
            ? HistoryRestoreApplyMode.Clean
            : HistoryRestoreApplyMode.Overwrite;

    private sealed class ArtifactLedgerGarbageCollector(BackupConfig config) : IHistoryArtifactGarbageCollector
    {
        public async Task GarbageCollectAsync(
            IReadOnlySet<Guid> protectedArtifactRootIds,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(config.DestinationPath)) return;
            var store = new FileArtifactLedgerStore(config.DestinationPath);
            await store.GarbageCollectUnreachableAsync(
                protectedArtifactRootIds.Select(id => new ArtifactId(id)),
                dryRun: false,
                cancellationToken).ConfigureAwait(false);
        }
    }
}

internal sealed class SevenZipHistoryArchiveBackend : IArchiveRepresentationBackend, IHistoryCompactionBackend
{
    private readonly BackupConfig _config;

    public SevenZipHistoryArchiveBackend(BackupConfig config)
        => _config = config ?? throw new ArgumentNullException(nameof(config));

    public async ValueTask<PayloadVerificationResult> VerifyAsync(
        VersionRepresentation representation,
        string localPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(localPath))
            return new(false, string.Empty, "Archive payload is missing.");
        var result = await RunAsync("t", localPath, outputDirectory: null, workingDirectory: null, cancellationToken)
            .ConfigureAwait(false);
        return result.Success
            ? new(true, $"7z-test:{new FileInfo(localPath).Length}", string.Empty)
            : new(false, string.Empty, result.Diagnostic);
    }

    public async ValueTask MaterializeAsync(
        IReadOnlyList<ArchiveMaterializationInput> dependencyFirstInputs,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingDirectory);
        foreach (var input in dependencyFirstInputs)
        {
            var result = await RunAsync(
                "x",
                input.LocalPath,
                stagingDirectory,
                workingDirectory: null,
                cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                throw new InvalidDataException(result.Diagnostic);
            ApplyDeletedFiles(input.Representation, stagingDirectory);
        }
        var marker = Path.Combine(stagingDirectory, "__FolderRewind_Internal");
        if (Directory.Exists(marker)) Directory.Delete(marker, recursive: true);
    }

    public async Task<HistoryCompactionPayload> CreateFullAsync(
        SourceVersion version,
        string materializedDirectory,
        RepresentationId replacementRepresentationId,
        string durableOutputDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(durableOutputDirectory);
        var path = Path.Combine(durableOutputDirectory, "payload.7z");
        var result = await RunAsync(
            "a",
            path,
            outputDirectory: null,
            workingDirectory: materializedDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!result.Success) throw new InvalidDataException(result.Diagnostic);
        return new(
            "7z",
            path,
            new FileInfo(path).Length,
            null,
            version.StateFingerprint,
            ImmutableDictionary<string, string>.Empty);
    }

    public ValueTask<PayloadVerificationResult> DeepVerifyAsync(
        VersionRepresentation representation,
        string payloadPath,
        CancellationToken cancellationToken)
        => VerifyAsync(representation, payloadPath, cancellationToken);

    private async Task<(bool Success, string Diagnostic)> RunAsync(
        string operation,
        string archivePath,
        string? outputDirectory,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        var executable = SevenZipExecutableLocator.Resolve(
            ConfigService.CurrentConfig?.GlobalSettings?.SevenZipPath);
        if (string.IsNullOrWhiteSpace(executable))
            return (false, I18n.GetString("BackupService_Log_SevenZipNotFound"));
        var password = _config.IsEncrypted ? EncryptionService.RetrievePassword(_config.Id) : null;
        if (_config.IsEncrypted && string.IsNullOrEmpty(password))
            return (false, "Encrypted archive credential is unavailable.");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Path.GetDirectoryName(archivePath) ?? Environment.CurrentDirectory
                : workingDirectory
        };
        start.ArgumentList.Add(operation);
        start.ArgumentList.Add(archivePath);
        if (operation == "x") start.ArgumentList.Add("-o" + outputDirectory);
        if (operation == "a") start.ArgumentList.Add("*");
        start.ArgumentList.Add("-y");
        if (!string.IsNullOrEmpty(password)) start.ArgumentList.Add("-p" + password);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) return (false, "7z process did not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        return process.ExitCode == 0
            ? (true, string.Empty)
            : (false, string.IsNullOrWhiteSpace(error) ? output : error);
    }

    private static void ApplyDeletedFiles(VersionRepresentation representation, string stagingDirectory)
    {
        if (!representation.RepresentationSpecificMetadata.TryGetValue("deletedFiles", out var encoded)) return;
        var root = Path.GetFullPath(stagingDirectory);
        foreach (var relative in encoded.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = relative.Replace('\\', '/');
            if (!FolderRewind.History.Storage.HistoryRepositoryPaths.IsSafeRepositoryRelativePath(normalized))
                throw new InvalidDataException("Smart deletion path is unsafe.");
            var target = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            var relation = Path.GetRelativePath(root, target);
            if (relation == ".." || relation.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("Smart deletion escapes the materialization root.");
            if (File.Exists(target)) File.Delete(target);
            else if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
}

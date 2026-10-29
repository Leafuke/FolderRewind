using FolderRewind.History.Merge;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using FolderRewind.History.Storage;
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

internal static partial class NativeHistoryApplicationService
{
    public static async Task<IReadOnlyList<SafetySnapshotProjection>> GetSafetySnapshotsAsync(
        BackupConfig config,
        bool activeOnly = true,
        CancellationToken cancellationToken = default)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadableAsync(config, cancellationToken).ConfigureAwait(false);
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
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
        await runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = (await runtime.Query.GetSafetySnapshotsAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.SnapshotId == snapshotId)
            ?? throw new InvalidOperationException("SafetySnapshot does not exist.");
        var selections = new Dictionary<SourceId, VersionId>();
        foreach (var id in snapshot.CheckpointIds)
        {
            var checkpoint = await runtime.Query.GetCheckpointAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("SafetySnapshot Source checkpoint is missing.");
            selections.Add(checkpoint.SourceId, checkpoint.VersionId);
        }
        return await RestoreSelectionsAsync(config, selections, requestedMode, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> ReleaseSafetySnapshotAsync(
        BackupConfig config,
        SafetySnapshotId snapshotId,
        CancellationToken cancellationToken = default)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
        return await new SafetySnapshotService(runtime).ReleaseAsync(snapshotId, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<HistoryRestoreResult> QuickRestoreAsync(
        BackupConfig config,
        ManagedFolder folder,
        CancellationToken cancellationToken = default,
        RestoreRequestOptions? options = null)
    {
        var preparation = await PrepareVersionRestoreAsync(config, folder, null,
            options?.Mode == "overwrite" ? BackupService.RestoreMode.Overwrite : BackupService.RestoreMode.Clean,
            cancellationToken).ConfigureAwait(false);
        return preparation.Target is { } target
            ? await ExecutePreparedRestoreAsync(config, folder, target, cancellationToken, options).ConfigureAwait(false)
            : Blocked(preparation.Diagnostic);
    }

    public static async Task<HistoryRestoreResult> RestoreVersionAsync(
        BackupConfig config,
        ManagedFolder folder,
        VersionId versionId,
        BackupService.RestoreMode requestedMode,
        CancellationToken cancellationToken = default,
        RestoreRequestOptions? options = null,
        string? expectedConfigSignature = null)
    {
        var preparation = await PrepareVersionRestoreAsync(config, folder, versionId, requestedMode,
            cancellationToken, expectedConfigSignature).ConfigureAwait(false);
        return preparation.Target is { } target
            ? await ExecutePreparedRestoreAsync(config, folder, target, cancellationToken, options).ConfigureAwait(false)
            : Blocked(preparation.Diagnostic);
    }

    private static async Task<HistoryRestoreResult> RestoreVersionCoreAsync(
        BackupConfig config,
        ManagedFolder folder,
        VersionId versionId,
        BackupService.RestoreMode requestedMode,
        CancellationToken cancellationToken = default,
        string? expectedSignature = null,
        HistoryQuickRestoreResolution? quickSelection = null,
        ArchiveRestoreVerificationContext? verificationContext = null)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
        expectedSignature ??= NativeHistoryConfigLease.Signature(config);
        var restore = CreateRestoreService(config, runtime, ordinaryRestore: true, expectedSignature: expectedSignature,
            verificationContext: verificationContext,
            validateVersionTarget: async (version, token) =>
            {
                if (version.VersionId != versionId || version.SourceId != Source(folder))
                    throw new InvalidOperationException("Selected restore Version no longer belongs to the requested Source.");
                var policy = MaterializationPolicyProjection.Project(versionId,
                    await runtime.Query.GetMaterializationPolicyTipsAsync(versionId, token).ConfigureAwait(false));
                if (policy.HasExplicitPolicy && policy.EffectiveState == MaterializationPolicyState.Released)
                    throw new InvalidOperationException("Selected restore Version's payload was released after selection.");
                if (quickSelection is not null)
                    await new HistoryQuickRestoreResolver(runtime, CreateRestoreService(config, runtime, verificationContext: verificationContext))
                        .ValidateAsync(Source(folder), quickSelection, token).ConfigureAwait(false);
            });
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterAsync(config.Id, cancellationToken).ConfigureAwait(false);
        if (NativeHistoryConfigLease.Signature(config) != expectedSignature) return Blocked(I18n.GetString("Restore_ConfigurationChanged"));
        restore.ExistingOperation = operation;
        await restore.RecoverInsideConfigurationAsync(operation, cancellationToken).ConfigureAwait(false);
        var workspace = await RequireWorkspaceAsync(runtime, cancellationToken).ConfigureAwait(false);
        var binding = await BindingAsync(config, folder, cancellationToken).ConfigureAwait(false);
        HistoryRestoreResult result;
        if (quickSelection is not null)
        {
            var requiresCurrentInput = RequiresRestoreCurrentInput(config);
            result = await HistoryQuickRestoreExecution.ExecuteAsync(runtime, restore, binding, versionId, workspace,
                MapRestoreMode(requestedMode), config.Archive.ShouldCreateSafetySnapshot(quickRestore: true), requiresCurrentInput,
                (sharedOperation, preparedBinding, token) => ProtectBeforeRestoreAsync(config, runtime, [Source(folder)], token, sharedOperation, [preparedBinding]),
                token => new HistoryQuickRestoreResolver(runtime, restore).ValidateAsync(Source(folder), quickSelection, token),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ConfigureRestoreProtection(restore, config, runtime, [Source(folder)], config.Archive.ShouldCreateSafetySnapshot(quickRestore: false));
            result = await restore.RestoreVersionAsync(versionId, binding, workspace,
                MapRestoreMode(requestedMode), cancellationToken).ConfigureAwait(false);
        }
        return await FinishRestoreMaintenanceAsync(config, runtime, result, operation, expectedSignature, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<HistoryRestoreResult> RestoreCheckpointAsync(
        BackupConfig config,
        CheckpointId checkpointId,
        bool completeCheckpoint,
        BackupService.RestoreMode requestedMode,
        CancellationToken cancellationToken = default)
    {
        NativeHostMutationContext.ThrowIfNestedMutation();
        var signature = NativeHistoryConfigLease.Signature(config);
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
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
                source.VersionId!.Value, requiredFidelity, AssessmentDepth.Fast, cancellationToken).ConfigureAwait(false);
            if (assessment.Readiness != HistoryReadiness.Ready || assessment.Selected is null)
                return Blocked($"Checkpoint Source {source.SourceId} is not Ready.");
        }
        PreflightRestoreLocations(config, await BindingsAsync(config, affected, cancellationToken).ConfigureAwait(false));
        return await new NativeHistoryRestoreOrchestrator().ExecuteAsync(
            config,
            affected,
            checkpointId.ToString(),
            token => RestoreCheckpointCoreAsync(
                config, checkpointId, completeCheckpoint, requestedMode, token, signature),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HistoryRestoreResult> RestoreCheckpointCoreAsync(
        BackupConfig config,
        CheckpointId checkpointId,
        bool completeCheckpoint,
        BackupService.RestoreMode requestedMode,
        CancellationToken cancellationToken, string expectedSignature)
    {
        using var verification = new ArchiveRestoreVerificationContext(config.Archive.RestorePreflightMode);
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterAsync(config.Id, cancellationToken).ConfigureAwait(false);
        if (NativeHistoryConfigLease.Signature(config) != expectedSignature) return Blocked(I18n.GetString("Restore_ConfigurationChanged"));
        var restore = CreateRestoreService(config, runtime, ordinaryRestore: true, expectedSignature: expectedSignature, verificationContext: verification);
        restore.ExistingOperation = operation;
        await restore.RecoverInsideConfigurationAsync(operation, cancellationToken).ConfigureAwait(false);
        var workspace = await RequireWorkspaceAsync(runtime, cancellationToken).ConfigureAwait(false);
        var checkpoint = await runtime.Query.GetCheckpointAsync(checkpointId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Checkpoint does not exist.");
        var restorableSources = checkpoint.Sources
            .Where(item => item.VersionId is not null)
            .Select(item => item.SourceId)
            .ToHashSet();
        var bindings = await BindingsAsync(
            config,
            config.SourceFolders.Where(folder => restorableSources.Contains(Source(folder))),
            cancellationToken).ConfigureAwait(false);
        ConfigureRestoreProtection(restore, config, runtime, bindings.Select(b => b.SourceId).ToArray(), config.Archive.BackupBeforeRestore);
        var result = await restore.RestoreCheckpointAsync(
            checkpointId,
            bindings,
            workspace,
            completeCheckpoint
                ? HistoryCheckpointRestoreScope.CompleteCheckpoint
                : HistoryCheckpointRestoreScope.AvailableMappedSources,
            MapRestoreMode(requestedMode),
            cancellationToken).ConfigureAwait(false);
        return await FinishRestoreMaintenanceAsync(config, runtime, result, operation, expectedSignature, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<HistoryRestoreResult> CheckoutAsync(
        BackupConfig config,
        BranchUpdateId selectedTipId,
        CancellationToken cancellationToken = default)
    {
        NativeHostMutationContext.ThrowIfNestedMutation();
        var signature = NativeHistoryConfigLease.Signature(config);
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
        var workspace = await RequireWorkspaceAsync(runtime, cancellationToken).ConfigureAwait(false);
        var restore = CreateRestoreService(config, runtime);
        var update = await runtime.Query.GetBranchUpdateAsync(selectedTipId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Branch update is missing.");
        var bindings = await BindingsAsync(config, config.SourceFolders.Where(f => Source(f) == update.SourceId), cancellationToken).ConfigureAwait(false);
        var plan = await new HistoryCheckoutPlanner(
            runtime,
            restore,
            new NativeWorkingStateProbe(config)).BuildAsync(
            selectedTipId,
            bindings,
            workspace,
            AssessmentDepth.Fast,
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
        PreflightRestoreLocations(config, bindings);
        return await new NativeHistoryRestoreOrchestrator().ExecuteAsync(
            config,
            affected,
            selectedTipId.ToString(),
            token => CheckoutCoreAsync(config, selectedTipId, token, signature),
            cancellationToken,
            WorkspaceOperationKind.Checkout).ConfigureAwait(false);
    }

    internal static async Task<HistoryWorkingStateComparison> CompareWorkingStateAsync(BackupConfig config, SourceId source, CancellationToken token)
    {
        var signature = NativeHistoryConfigLease.Signature(config);
        var runtime = await NativeHistoryCoreGateway.EnsureReadableAsync(config, token).ConfigureAwait(false);
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterAsync(config.Id, token).ConfigureAwait(false);
        await using var guard = await NativeHistoryConfigLease.EnterInsideOperationAsync(config, signature, operation, token).ConfigureAwait(false);
        var baseline = (await runtime.WorkspaceStore.LoadAsync(token).ConfigureAwait(false)).Value?.SourceBaselines.SingleOrDefault(s => s.SourceId == source);
        if (baseline is null) return new(HistoryWorkingStateComparisonStatus.Unknown, "BaselineUnavailable");
        var binding = (await BindingsAsync(config, config.SourceFolders.Where(f => Source(f) == source), token).ConfigureAwait(false)).Single();
        return await new HistoryExactWorkingStateProbe(runtime, CreateRestoreService(config, runtime)).CompareAsync(binding, baseline, token).ConfigureAwait(false);
    }

    public static async Task<HistoryCheckoutPlan> PlanCheckoutAsync(
        BackupConfig config,
        BranchUpdateId selectedTipId,
        AssessmentDepth assessmentDepth = AssessmentDepth.Fast,
        CancellationToken cancellationToken = default)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadableAsync(config, cancellationToken).ConfigureAwait(false);
        var workspace = await RequireWorkspaceAsync(runtime, cancellationToken).ConfigureAwait(false);
        var update = await runtime.Query.GetBranchUpdateAsync(selectedTipId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Branch update is missing.");
        var plan = await new HistoryCheckoutPlanner(
            runtime,
            CreateRestoreService(config, runtime),
            new NativeWorkingStateProbe(config)).BuildAsync(
            selectedTipId,
            await BindingsAsync(config, config.SourceFolders.Where(f => Source(f) == update.SourceId), cancellationToken).ConfigureAwait(false),
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

        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
        var restore = CreateRestoreService(config, runtime);
        try
        {
            await PrepareExactVersionsAsync(config, runtime, restore, initial.Checkpoint.Sources
                .Where(s => s.VersionId is not null).Select(s => (s.SourceId, s.VersionId!.Value)), cancellationToken).ConfigureAwait(false);
        }
        catch (HistoryMergeBlockedException ex)
        {
            return initial with { Readiness = HistoryCheckoutReadiness.ExactRepresentationUnavailable, Diagnostic = ex.Message };
        }
        return await PlanCheckoutAsync(config, selectedTipId, AssessmentDepth.Fast, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<HistoryRestoreResult> CheckoutCoreAsync(
        BackupConfig config,
        BranchUpdateId selectedTipId,
        CancellationToken cancellationToken, string expectedSignature)
    {
        using var verification = new ArchiveRestoreVerificationContext(config.Archive.RestorePreflightMode);
        var expectedConfigRevision = config.ConfigRevision;
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
        var restore = CreateRestoreService(config, runtime, expectedSignature: expectedSignature, verificationContext: verification, targetVolumeRestore: true);
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterAsync(config.Id, cancellationToken).ConfigureAwait(false);
        if (NativeHistoryConfigLease.Signature(config) != expectedSignature) return Blocked(I18n.GetString("Restore_ConfigurationChanged"));
        restore.ExistingOperation = operation;
        await restore.RecoverInsideConfigurationAsync(operation, cancellationToken).ConfigureAwait(false);
        var workspace = await RequireWorkspaceAsync(runtime, cancellationToken).ConfigureAwait(false);
        var update = await runtime.Query.GetBranchUpdateAsync(selectedTipId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Branch update is missing.");
        var bindings = await BindingsAsync(config, config.SourceFolders.Where(f => Source(f) == update.SourceId), cancellationToken).ConfigureAwait(false);
        var result = await new HistoryCheckoutService(
            runtime,
            restore,
            new SafetySnapshotWorkingStateProtector(config, runtime, affectedSources: [update.SourceId]),
            null,
            async token =>
            {
                var authoritative = ConfigService.CurrentConfig.BackupConfigs.SingleOrDefault(item => item.Id == config.Id)
                    ?? throw new InvalidOperationException("Configuration was removed during Checkout.");
                if (authoritative.ConfigRevision != expectedConfigRevision)
                    throw new InvalidOperationException("Configuration revision changed during Checkout.");
                return await BindingsAsync(authoritative, authoritative.SourceFolders.Where(f => Source(f) == update.SourceId), token).ConfigureAwait(false);
            }).CheckoutAsync(
            selectedTipId,
            bindings,
            workspace,
            HistoryCheckoutProtectionMode.ProtectCurrentWork,
            cancellationToken).ConfigureAwait(false);
        return await FinishRestoreMaintenanceAsync(config, runtime, result, operation, expectedSignature, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<HistoryCleanupReport?> ApplyAutomaticRetentionAsync(BackupConfig config,
        NativeHistoryConfigurationOperationGate.Lease operation, CancellationToken cancellationToken = default)
    {
        operation.Require(new(config.Id));
        var report = await RunCleanupInsideOperationAsync(config, HistoryRetentionBenefitPolicy.SpaceFirst,
            true, null, cancellationToken).ConfigureAwait(false);
        if (report?.HasWarnings == true)
            LogService.LogWarning("[Retention] " + report.Status + "; see cleanup report.", nameof(NativeHistoryApplicationService));
        return report;
    }

    public static async Task ReleaseVersionAsync(
        BackupConfig config,
        VersionId versionId,
        CancellationToken cancellationToken = default)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
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
        _ = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterAsync(config.Id, cancellationToken).ConfigureAwait(false);
        var existed = File.Exists(localPath);
        await using var prepared = await PrepareVersionDeletionAsync(config, versionId, representationId,
            localPath, hideRecord: false, releaseVersion, operation, token: cancellationToken).ConfigureAwait(false);
        var result = await CommitVersionDeletionAsync(config, prepared, cancellationToken).ConfigureAwait(false);
        if (!result.Committed) throw new InvalidOperationException(result.Diagnostic);
        return new(existed, existed && !File.Exists(localPath), prepared.TargetReplicaCount);
    }

    public static async Task<HistoryRestoreService> CreateRestoreServiceAsync(
        BackupConfig config,
        CancellationToken cancellationToken = default,
        ArchiveRestoreVerificationContext? verification = null, bool targetVolume = false)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
        await runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        return CreateRestoreService(config, runtime, verificationContext: verification, targetVolumeRestore: targetVolume);
    }

    private static HistoryRestoreService CreateRestoreService(BackupConfig config, HistoryRuntime runtime, bool ordinaryRestore = false,
        string? expectedSignature = null, Func<SourceVersion, CancellationToken, Task>? validateVersionTarget = null,
        ArchiveRestoreVerificationContext? verificationContext = null, bool targetVolumeRestore = false,
        HistoryRestorePreparationSession? preparationSession = null)
    {
        var configSignature = expectedSignature ?? NativeHistoryConfigLease.Signature(config);
        var archive = new SevenZipHistoryArchiveBackend(config, verificationContext is not null);
        var representations = HistoryRepresentationRuntimeFactory.Create(config, runtime, archive);
        HistoryRestoreService? restore = null;
        restore = new HistoryRestoreService(
            runtime,
            representations,
            token => BuildEnvironmentAsync(runtime, token),
            new FileSystemHistoryRestoreMutationBackend(),
            ordinaryRestore && (RequiresRestoreCurrentInput(config) || Plugins.V3.PluginV3ModelMapper.ToKind(config).OwnerId.Value != "folderrewind.core")
                ? (binding, staging, token) => Plugins.V3.PluginV3RestoreStagingPreparation.PrepareAsync(
                config, NativeHistoryRestoreOrchestrator.Current?.Id ?? Guid.NewGuid(),
                NativeHistoryRestoreOrchestrator.Current?.PreservePlayerData == true, binding, staging, token,
                NativeHistoryRestoreOrchestrator.Current?.PreservePlayerDataOverride,
                NativeHistoryRestoreOrchestrator.Current?.RestoreWhitelist,
                NativeHistoryRestoreOrchestrator.Current?.RestorePreservePaths, currentInput: restore!.CurrentInputs.GetValueOrDefault(binding.SourceId)) : null,
            token => NativeHistoryConfigLease.EnterAsync(config, configSignature, token))
        {
            RecoverNativeInsideOperation = (operation, token) => Plugins.V3.NativeArtifactHostService.RecoverInsideOperationAsync(config, runtime, operation, token),
            FinalGuardInsideOperation = (operation, token) => NativeHistoryConfigLease.EnterInsideOperationAsync(config, configSignature, operation, token),
            ValidateVersionTarget = validateVersionTarget,
            VerificationContext = verificationContext,
            PreparationEnvironmentFactory = (graph, token) => BuildPreparationEnvironmentAsync(runtime, graph, token),
            RestoreConfigurationSignature = string.Join("/", config.Id, config.ConfigRevision,
                config.Archive.RestorePreflightMode, config.Archive.QuickRestoreSafetySnapshotEnabled, config.Archive.BackupBeforeRestore),
            UseTargetVolumeWorkspaces = ordinaryRestore || targetVolumeRestore,
            RequiresCurrentInput = ordinaryRestore && RequiresRestoreCurrentInput(config),
            PreparationSession = preparationSession,
            ProtectedRestoreRoots = string.IsNullOrWhiteSpace(config.DestinationPath)
                ? [AppRuntimeInfo.ApplicationBaseDirectory, ConfigService.ConfigDirectory]
                : [config.DestinationPath, AppRuntimeInfo.ApplicationBaseDirectory, ConfigService.ConfigDirectory],
            AllowDirectorySwap = !RequiresRestoreCurrentInput(config)
        };
        return restore;
    }

    internal static async Task<IRepresentationEnvironment> BuildEnvironmentAsync(
        HistoryRuntime runtime,
        CancellationToken cancellationToken)
    {
        var representations = await runtime.Query.GetAllRepresentationsAsync(cancellationToken).ConfigureAwait(false);
        return await BuildPreparationEnvironmentAsync(runtime, representations, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IRepresentationEnvironment> BuildPreparationEnvironmentAsync(HistoryRuntime runtime,
        IReadOnlyList<VersionRepresentation> representations, CancellationToken cancellationToken)
    {
        using var timing = HistoryOperationDiagnostics.Current?.Measure("restore-environment-load");
        var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Value;
        var byRepresentation = (await runtime.Query.GetAllStorageReplicasAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(replica => replica.RepresentationId);
        var byReplica = (await runtime.Query.GetAllReplicaLifecycleUpdatesAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(update => update.ReplicaId);
        var replicas = new List<StorageReplica>();
        var active = new List<ReplicaId>();
        foreach (var representation in representations)
        {
            foreach (var replica in byRepresentation[representation.RepresentationId])
            {
                replicas.Add(replica);
                if (History.Cloud.HistoryCloudReplicaValidity.IsActive(byReplica[replica.ReplicaId]))
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
        IReadOnlyCollection<SourceId> affectedSources,
        CancellationToken cancellationToken, NativeHistoryConfigurationOperationGate.Lease operation,
        IReadOnlyList<HistoryRestoreSourceBinding>? physicalBindings = null)
    {
        using var timing = HistoryOperationDiagnostics.Current?.Measure("safety-snapshot-protect");
        try
        {
            _ = await BackupService.CreateSafetySnapshotAsync(
                config,
                SafetySnapshotReason.BeforeRestore,
                cancellationToken, operation, affectedSources, physicalBindings).ConfigureAwait(false);
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

    private static bool RequiresRestoreCurrentInput(BackupConfig config) => config.Filters.RestoreWhitelist.Count > 0
        || NativeHistoryRestoreOrchestrator.Current?.PreservePlayerData == true
        || NativeHistoryRestoreOrchestrator.Current?.PreservePlayerDataOverride.HasValue == true
        || NativeHistoryRestoreOrchestrator.Current?.RestoreWhitelist.Count is > 0
        || NativeHistoryRestoreOrchestrator.Current?.RestorePreservePaths.Count is > 0;

    private static async Task<HistoryRestoreResult> FinishRestoreMaintenanceAsync(BackupConfig config, HistoryRuntime runtime,
        HistoryRestoreResult result, NativeHistoryConfigurationOperationGate.Lease operation, string signature, CancellationToken token)
    {
        if (!result.Succeeded || result.AppliedSources.Count == 0) return result;
        using var timing = HistoryOperationDiagnostics.Current?.Measure("restore-baseline-sync");
        try
        {
            await using var guard = await NativeHistoryConfigLease.EnterInsideOperationAsync(config, signature, operation, token).ConfigureAwait(false);
            await BackupService.SynchronizeCaptureBaselinesWithWorkspaceAsync(config, result.AppliedSources, token).ConfigureAwait(false);
            return result;
        }
        catch (Exception error)
        {
            runtime.ReportMaintenance(error.Message);
            return result.WithPostActionWarning(I18n.Format("Restore_MaintenanceIncomplete", error.Message));
        }
    }

    private static void PreflightRestoreLocations(BackupConfig config, IReadOnlyList<HistoryRestoreSourceBinding> bindings)
    {
        var roots = new[] { config.DestinationPath, AppRuntimeInfo.ApplicationBaseDirectory, ConfigService.ConfigDirectory }
            .Where(path => !string.IsNullOrWhiteSpace(path));
        _ = HistoryRestoreWorkspacePlanner.Plan(HistoryTransactionId.New(), bindings, roots);
    }

    private static void ConfigureRestoreProtection(HistoryRestoreService restore, BackupConfig config, HistoryRuntime runtime,
        IReadOnlyCollection<SourceId> affected, bool enabled)
    {
        if (!enabled) return;
        restore.ProtectCurrentWorkInsideOperation = async (operation, bindings, token) =>
        {
            var result = await ProtectBeforeRestoreAsync(config, runtime, affected, token, operation, bindings).ConfigureAwait(false);
            return result.Result is null ? result.Workspace! : throw new InvalidOperationException(result.Result.Diagnostic);
        };
    }

    private static SourceId Source(ManagedFolder folder)
        => Guid.TryParse(folder.Id, out var id) && id != Guid.Empty
            ? new SourceId(id)
            : throw new InvalidDataException("ManagedFolder has no stable SourceId.");

    private static async Task<HistoryRestoreSourceBinding> BindingAsync(
        BackupConfig config,
        ManagedFolder folder,
        CancellationToken cancellationToken)
    {
        var resolution = await HistorySourceBoundaryResolver.ResolveAsync(config, folder, cancellationToken)
            .ConfigureAwait(false);
        if (resolution.IsBlocked)
        {
            var code = resolution.Diagnostics.LastOrDefault()?.Code ?? "history.boundary_resolution_failed";
            throw new InvalidOperationException($"Effective Source Boundary resolution was blocked: {code}");
        }
        return new HistoryRestoreSourceBinding(
            Source(resolution.EffectiveFolder),
            resolution.EffectiveFolder.Path,
            resolution.Boundary);
    }

    private static async Task<IReadOnlyList<HistoryRestoreSourceBinding>> BindingsAsync(
        BackupConfig config,
        IEnumerable<ManagedFolder> folders,
        CancellationToken cancellationToken)
    {
        var result = new List<HistoryRestoreSourceBinding>();
        foreach (var folder in folders)
            result.Add(await BindingAsync(config, folder, cancellationToken).ConfigureAwait(false));
        return result;
    }

    internal static Task PrepareExactVersionsAsync(BackupConfig config, HistoryRuntime runtime, HistoryRestoreService restore,
        IEnumerable<(SourceId Source, VersionId Version)> inputs, CancellationToken token)
        => PrepareVersionsAsync(config, runtime, restore, inputs, MaterializationFidelity.Exact, token);

    internal static async Task PrepareVersionsAsync(BackupConfig config, HistoryRuntime runtime, HistoryRestoreService restore,
        IEnumerable<(SourceId Source, VersionId Version)> inputs, MaterializationFidelity fidelity, CancellationToken token)
    {
        var representations = (await runtime.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false)).ToDictionary(r => r.RepresentationId);
        var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
        await ExactReplicaPreparation.PrepareAsync(inputs, representations,
            (version, depth, ct) => restore.AssessVersionAsync(version, fidelity, depth, ct),
            id => (catalog?.Entries ?? []).Any(e => e.RepresentationId == id && e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath && File.Exists(e.Locator.AbsolutePath)),
            (source, representation, ct) => CloudSyncService.DownloadRepresentationAsync(config,
                config.SourceFolders.Single(f => Source(f) == source), representation.RepresentationId,
                representation.RepresentationSpecificMetadata.GetValueOrDefault("fileName")
                    ?? representation.RepresentationSpecificMetadata.GetValueOrDefault("legacyFileName") ?? $"{representation.RepresentationId}.7z", ct), token).ConfigureAwait(false);
    }

    private sealed class SafetySnapshotWorkingStateProtector(
        BackupConfig config,
        HistoryRuntime runtime, SafetySnapshotReason reason = SafetySnapshotReason.BeforeCheckout,
        IReadOnlyCollection<SourceId>? affectedSources = null) : IHistoryWorkingStateProtector, IHistoryWorkingStateProtectorInsideOperation
    {
        public async Task<HistoryWorkspace> ProtectInsideOperationAsync(HistoryWorkspace expected,
            NativeHistoryConfigurationOperationGate.Lease operation, CancellationToken token,
            IReadOnlyList<HistoryRestoreSourceBinding>? physicalBindings = null)
        {
            operation.Require(runtime.ConfigId);
            if (!HistoryRestoreTransactionJournalStore.WorkspaceEquals(await RequireWorkspaceAsync(runtime, token).ConfigureAwait(false), expected))
                throw new DeviceLocalStateConflictException("Workspace changed before SafetySnapshot.");
            await BackupService.CreateSafetySnapshotAsync(config, reason, token, operation, affectedSources, physicalBindings).ConfigureAwait(false);
            return await RequireWorkspaceAsync(runtime, token).ConfigureAwait(false);
        }
        public async Task<HistoryWorkspace> ProtectAsync(
            HistoryWorkspace expectedWorkspace,
            CancellationToken cancellationToken)
        {
            var current = await RequireWorkspaceAsync(runtime, cancellationToken).ConfigureAwait(false);
            if (!HistoryRestoreTransactionJournalStore.WorkspaceEquals(current, expectedWorkspace))
                throw new DeviceLocalStateConflictException("Workspace changed before SafetySnapshot capture.");
            _ = await BackupService.CreateSafetySnapshotAsync(
                config,
                reason,
                cancellationToken, affectedSources: affectedSources).ConfigureAwait(false);
            return await RequireWorkspaceAsync(runtime, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class NativeWorkingStateProbe(BackupConfig config, ArchiveRestoreVerificationContext? verification = null) : IHistoryWorkingStateProbe
    {
        public async Task<bool> IsExactAsync(
            HistoryRestoreSourceBinding binding,
            WorkspaceSourceBaseline baseline,
            CancellationToken cancellationToken)
        {
            if (baseline.BaseVersionId is not { } versionId
                || baseline.Relation != WorkspaceBaselineRelation.Exact)
                return false;
            var folder = config.SourceFolders.SingleOrDefault(item => Source(item) == binding.SourceId);
            return folder is not null
                && await BackupService.DeepProbeWorkspaceVersionAsync(
                    config,
                    folder,
                    versionId,
                    cancellationToken, verification).ConfigureAwait(false);
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
            var native = Plugins.V3.NativeArtifactHostService.Store(config);
            var nativeLedger = await native.Ledger.LoadAsync(cancellationToken).ConfigureAwait(false);
            var nativeIds = nativeLedger.Artifacts.Select(a => a.ArtifactId.Value).ToHashSet();
            await native.CollectAsync(protectedArtifactRootIds.Where(nativeIds.Contains).Select(id => new ArtifactId(id)), cancellationToken);
            var store = new FileArtifactLedgerStore(config.DestinationPath);
            var legacy = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            // A destination may be shared by configurations; never collect another config's nodes.
            var foreign = legacy.Artifacts.Where(a => a.ConfigId != config.Id).Select(a => a.ArtifactId);
            await store.GarbageCollectUnreachableAsync(
                protectedArtifactRootIds.Where(id => !nativeIds.Contains(id)).Select(id => new ArtifactId(id)).Concat(foreign),
                dryRun: false,
                cancellationToken).ConfigureAwait(false);
        }
    }
}

internal sealed class SevenZipHistoryArchiveBackend : SevenZipArchiveProcessBackend
{
    internal SevenZipHistoryArchiveBackend(BackupConfig config, bool freezeInput = false) : base(
        Executable(freezeInput), Credential(config, freezeInput), config.IsEncrypted, BackupService.InternalRestoreMarkerDirectoryName) { }
    private static Func<string?> Executable(bool freeze)
    {
        string? Resolve() => SevenZipExecutableLocator.Resolve(ConfigService.CurrentConfig?.GlobalSettings?.SevenZipPath);
        if (!freeze) return Resolve;
        var snapshot = Resolve(); return () => snapshot;
    }
    private static Func<string?> Credential(BackupConfig config, bool freeze)
    {
        if (!freeze) return () => EncryptionService.RetrievePassword(config.Id);
        var snapshot = EncryptionService.RetrievePassword(config.Id); return () => snapshot;
    }
}

using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    /// <summary>
    /// 面向界面的历史图谱动作层：把「切换分支」这类用户动作折成图谱侧的服务调用。
    /// <para>
    /// 应用与 1.9.x 一致，只搬了切换分支需要的那一段。相对 1.9.x 有三处刻意省略，都有各自的原因：
    /// </para>
    /// <list type="bullet">
    /// <item>1.9.x 在这层外面套了 <c>NativeHistoryRestoreOrchestrator</c> 跟插件的还原协调器打招呼 ——
    /// 那是插件 V3 的设施，本分支没有，且它的非插件配置路径也只是一次直通调用，所以这里直接调核心。</item>
    /// <item>1.9.x 的 <c>PrepareCheckoutAsync</c>（为「准备中」的检查点从云端补下缺失的表示形态）没有搬 ——
    /// 它整段都是 <c>CloudSyncService</c> 的下载逻辑，而本分支没有云端副本来源，
    /// 表示形态的「需要准备」只可能由共享副本触发，因此计划不会落到那个状态。</item>
    /// <item>重载绑定时 1.9.x 会比对 <c>ConfigRevision</c>，本分支没有这个字段。已核实这不需要补：
    /// 那份修订号只在插件侧的配置变更路径里被重新生成（<c>PluginService.ConfigKinds</c> /
    /// <c>Plugins/V3/**</c> / <c>HistorySourceBindingRepairService</c>），本分支一个都没有，
    /// 照搬字段只会得到一个赋值一次、此后再不变的值，比对恒真。
    /// 而切换真正危险的那一维（覆盖哪些目录、各自边界）本层重载后由
    /// <see cref="HistoryCheckoutService"/> 结构化比对绑定来守，与修订号无关。</item>
    /// </list>
    /// 合并相关的方法在 <c>NativeHistoryApplicationService.Merge.cs</c>。
    /// </summary>
    internal static partial class NativeHistoryApplicationService
    {
        /// <summary>
        /// 切换分支前先算一遍计划，让调用方能在真正动手之前把「会覆盖哪些目录」「是否需要先做保护点」讲给用户。
        /// </summary>
        public static async Task<HistoryCheckoutPlan> PlanCheckoutAsync(
            BackupConfig config,
            BranchUpdateId selectedTipId,
            AssessmentDepth assessmentDepth = AssessmentDepth.Deep,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(config);
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
            var workspace = await RequireWorkspaceAsync(runtime, cancellationToken).ConfigureAwait(false);
            return await new HistoryCheckoutPlanner(
                    runtime,
                    CreateRestoreService(config, runtime),
                    new NativeWorkingStateProbe(config))
                .BuildAsync(
                    selectedTipId,
                    Bindings(config, config.SourceFolders),
                    workspace,
                    assessmentDepth,
                    cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 把配置的源目录整体切到某条分支的端点。
        /// 计划不可执行时不做任何改动，只把原因带回去；可执行时当前磁盘内容会先被保护点接住再覆盖。
        /// </summary>
        public static async Task<HistoryRestoreResult> CheckoutAsync(
            BackupConfig config,
            BranchUpdateId selectedTipId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(config);
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
            var workspace = await RequireWorkspaceAsync(runtime, cancellationToken).ConfigureAwait(false);
            var restore = CreateRestoreService(config, runtime);
            var bindings = Bindings(config, config.SourceFolders);

            // 计划只算一次给人看，真正执行时服务内部还会自己再算一次并在锁内复核，
            // 因此这里是「先看能不能动手」，不是执行依据。
            var plan = await new HistoryCheckoutPlanner(runtime, restore, new NativeWorkingStateProbe(config))
                .BuildAsync(selectedTipId, bindings, workspace, AssessmentDepth.Deep, cancellationToken)
                .ConfigureAwait(false);
            if (!plan.CanExecute)
            {
                return new HistoryRestoreResult(
                    HistoryRestoreStatus.BlockedBeforeMutation,
                    plan.Diagnostic,
                    false,
                    [],
                    plan);
            }

            return await new HistoryCheckoutService(
                runtime,
                restore,
                new SafetySnapshotWorkingStateProtector(config, runtime, SafetySnapshotReason.BeforeCheckout),
                new NativeWorkingStateProbe(config),
                token => ReloadBindingsAsync(config, token))
                .CheckoutAsync(
                    selectedTipId,
                    bindings,
                    workspace,
                    HistoryCheckoutProtectionMode.ProtectCurrentWork,
                    cancellationToken).ConfigureAwait(false);
        }

        public static async Task<HistoryRestoreService> CreateRestoreServiceAsync(
            BackupConfig config,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(config);
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
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
                    var updates = await runtime.Query
                        .GetReplicaLifecycleUpdatesAsync(replica.ReplicaId, cancellationToken).ConfigureAwait(false);
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

        /// <summary>
        /// 落地期间配置可能被改。这里重新取一次权威配置再解析边界，
        /// 由切换服务拿新旧绑定做结构比对；比对不过就中止，避免把旧边界写回历史。
        /// </summary>
        private static async Task<IReadOnlyList<HistoryRestoreSourceBinding>> ReloadBindingsAsync(
            BackupConfig config,
            CancellationToken cancellationToken)
        {
            var authoritative = await UiDispatcherService.RunOnUiAsync(() => Task.FromResult(
                    ConfigService.CurrentConfig?.BackupConfigs?
                        .FirstOrDefault(item => item is not null && item.Id == config.Id)))
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("Configuration was removed during Checkout.");
            return Bindings(authoritative, authoritative.SourceFolders);
        }

        private static SourceId Source(ManagedFolder folder)
            => Guid.TryParse(folder.Id, out var id) && id != Guid.Empty
                ? new SourceId(id)
                : throw new InvalidDataException("ManagedFolder has no stable SourceId.");

        private static HistoryRestoreSourceBinding Binding(BackupConfig config, ManagedFolder folder)
        {
            var resolution = HistorySourceBoundaryResolver.Resolve(config, folder);
            if (resolution.IsBlocked)
            {
                throw new InvalidOperationException(
                    $"Effective Source Boundary resolution was blocked: {resolution.BlockedErrorCode}");
            }

            return new HistoryRestoreSourceBinding(
                Source(resolution.EffectiveFolder),
                resolution.EffectiveFolder.Path,
                resolution.Boundary);
        }

        private static IReadOnlyList<HistoryRestoreSourceBinding> Bindings(
            BackupConfig config,
            IEnumerable<ManagedFolder> folders)
            => folders.Where(folder => folder is not null)
                .Select(folder => Binding(config, folder))
                .ToArray();

        /// <summary>
        /// 切换分支/合并都会覆盖工作区，所以覆盖之前先把当前磁盘状态整体收成一次独立提交。
        /// 它不进任何分支，只是「后悔药」。用哪种原因由调用方给，落进快照里便于事后分辨。
        /// </summary>
        private sealed class SafetySnapshotWorkingStateProtector(
            BackupConfig config,
            HistoryRuntime runtime,
            SafetySnapshotReason reason)
            : IHistoryWorkingStateProtector
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
                    reason,
                    cancellationToken).ConfigureAwait(false);
                return await RequireWorkspaceAsync(runtime, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 判断磁盘上的内容是否仍然精确等于工作区基线指向的那个版本。
        /// 不等就说明有未收进历史的改动，切换前必须先做保护点。
        /// </summary>
        private sealed class NativeWorkingStateProbe(BackupConfig config) : IHistoryWorkingStateProbe
        {
            public async Task<bool> IsExactAsync(
                HistoryRestoreSourceBinding binding,
                WorkspaceSourceBaseline baseline,
                CancellationToken cancellationToken)
            {
                if (baseline.BaseVersionId is not { } versionId
                    || baseline.Relation != WorkspaceBaselineRelation.Exact)
                {
                    return false;
                }

                var folder = config.SourceFolders.FirstOrDefault(
                    item => item is not null && Guid.TryParse(item.Id, out var id) && new SourceId(id) == binding.SourceId);
                return folder is not null
                    && await BackupService.DeepProbeWorkspaceVersionAsync(
                        config,
                        folder,
                        versionId,
                        cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    /// <summary>
    /// 面向界面的合并动作层：建会话、重算、应用。
    /// <para>
    /// 相对 1.9.x 有四处刻意省略，原因各不相同：
    /// </para>
    /// <list type="bullet">
    /// <item>1.9.x 在应用那一步外面套了 <c>NativeHistoryRestoreOrchestrator</c>（协调器可用性检查 +
    /// <c>ExecuteAsync(..., WorkspaceOperationKind.Merge)</c> 包装）—— 那是插件 V3 的设施，
    /// 本分支既没有这个类型也没有 <c>WorkspaceOperationKind</c>，且它的非插件配置路径原本也只是直通调用，
    /// 所以这里直接调核心，与切换分支那一层保持同一处理。</item>
    /// <item>1.9.x 会为合并出的版本采集插件元数据（<c>PluginV3BackupSession.CaptureVersionMetadataAsync</c>）——
    /// 本分支没有插件 V3，<c>HistoryMergeCommitBuilder</c> 的 <c>metadata</c> 又是可选参数，故整段不搬。</item>
    /// <item>1.9.x 用 <c>NativeHistoryConfigLease.Signature(config)</c> 当配置修订号，并在应用前比对它。
    /// 本分支没有配置修订号（切换分支那一层已就同一问题拍板「不加，保留现有绑定比对」），
    /// 因此这里传常量。后果是 <see cref="HistoryMergeApplyService"/> 里
    /// <c>current.Item1 != session.Plan.ConfigRevision</c> <b>恒为假</b>，
    /// 真正拦住「落地期间配置被改」的是同处的 <c>BindingsEqual</c> 绑定结构比对（它比对源 id、目录与边界指纹）。
    /// 读到这里的人不要以为修订号比对在起作用。</item>
    /// <item>1.9.x 在应用成功后还会调 <c>BackupService.SynchronizeCaptureBaselinesWithWorkspaceAsync</c>，
    /// 按新的工作区基线<b>急切重建</b>受影响来源那份「可丢弃的捕获基线缓存」（它只用来给捕获做
    /// SkipIfUnchanged / Smart 短路）。本分支不搬这个调用，因为它对<b>正确性</b>并非必需：
    /// <c>NativeHistoryCoreGateway.LoadCaptureBaselineAsync</c> 每次读缓存都拿
    /// <c>SourceCaptureBaselinePolicy.IsApplicableToWorkspace</c> 与当前工作区基线比对，
    /// 对不上就当场丢弃并返回 null。于是 <c>DoHistoryFullBackupAsync</c> 里那道
    /// <c>SkipIfUnchanged &amp;&amp; baseline is not null &amp;&amp; …</c> 短路拿不到基线，
    /// 只能照原样采一次全量 —— <b>不会误判成「没有变化」</b>。
    /// 代价就是少一次短路：合并落地后每个受影响来源的下一次备份会重采一遍（此后那次提交会用新的基线候选
    /// 把缓存写回来，短路随之恢复）。只是浪费一次采集，不会算错。</item>
    /// </list>
    /// </summary>
    internal static partial class NativeHistoryApplicationService
    {
        /// <summary>
        /// 本分支没有配置修订号，用一个常量占住合并计划里的那一格。
        /// 陈旧检测靠绑定比对，见上面的类型注释。
        /// </summary>
        private const string MergeConfigRevision = "branch-backup/no-config-revision";

        /// <summary>
        /// 发起一次合并：把来源分支合进工作区当前的活动分支。
        /// 返回的会话可能直接可用（无冲突），也可能停在 <c>Resolving</c> 等人解决冲突；
        /// 没有可合并内容时返回 <c>null</c>。
        /// </summary>
        public static async Task<MergeSession?> StartMergeAsync(
            BackupConfig config,
            BranchId sourceBranchId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(config);
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
            var restore = CreateRestoreService(config, runtime);
            await restore.RecoverIncompleteAsync(cancellationToken).ConfigureAwait(false);
            return await new HistoryMergeService(runtime, restore)
                .StartAsync(
                    sourceBranchId,
                    MergeConfigRevision,
                    Bindings(config, config.SourceFolders),
                    cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 按当前分支端点与配置重算一份计划。工作区或端点变了之后用这个，而不是在旧会话上继续。
        /// </summary>
        public static async Task<MergeSession> RecomputeMergeAsync(
            BackupConfig config,
            MergeSession session,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(session);
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
            return await new HistoryMergeService(runtime, CreateRestoreService(config, runtime))
                .RecomputeAsync(
                    session,
                    MergeConfigRevision,
                    Bindings(config, config.SourceFolders),
                    cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 把一个已解决完（<c>Ready</c>）的会话原子应用到工作区。
        /// 服务内部会先做保护点、复核绑定是否被改、最后一次性提交；这里只负责把依赖接齐。
        /// </summary>
        public static async Task<HistoryRestoreResult> ApplyMergeAsync(
            BackupConfig config,
            MergeSession session,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(session);
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
            var restore = CreateRestoreService(config, runtime);
            var archives = new SevenZipHistoryArchiveBackend(config);
            var builder = new HistoryMergeCommitBuilder(runtime, restore, archives, archives);
            return await new HistoryMergeApplyService(
                    runtime,
                    restore,
                    builder,
                    new SafetySnapshotWorkingStateProtector(config, runtime, SafetySnapshotReason.BeforeMerge),
                    token => ReloadMergeBindingsAsync(config, token))
                .ApplyAsync(session, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 列出会话的冲突与解决状态。窗口每次解决一条后重新调它，拿到的是最新进度。
        /// </summary>
        public static async Task<IReadOnlyList<MergeConflictView>> ListMergeConflictsAsync(
            BackupConfig config,
            MergeSession session,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(session);
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
            return await new HistoryMergeQueryService(runtime)
                .ListConflictsAsync(session, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 读出某条冲突三方的内容与行级合并结果，供窗口逐块展示。
        /// 只对该冲突<b>可以逐块编辑</b>时调用 —— 别的冲突没有「一个文件的三份内容」这回事。
        /// </summary>
        public static async Task<MergeTextConflictView> LoadMergeTextAsync(
            BackupConfig config,
            MergeSession session,
            string conflictId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(session);
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
            return new HistoryMergeQueryService(runtime).LoadText(session, conflictId);
        }

        /// <summary>
        /// 读出本次会话自动合并掉的内容，供窗口在「一条冲突都没有」时铺开给人过目。
        /// 与 <see cref="ListMergeConflictsAsync"/> 一样只是读投影，不改会话状态。
        /// </summary>
        public static async Task<MergeAutoMergeView> ListAutoMergedAsync(
            BackupConfig config,
            MergeSession session,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(session);
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
            return await new HistoryMergeQueryService(runtime)
                .LoadAutoMergedAsync(session, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 把一条冲突整份取一边（本地或对方）。
        /// <para>
        /// <paramref name="conflict"/> 是窗口<b>看到的那一份</b>：它的签名会一并交给存储层比对，
        /// 期间输入变了就拒绝，避免拿旧判断去解决已经变了的冲突。
        /// </para>
        /// </summary>
        public static async Task<MergeSession> ResolveMergeConflictAsync(
            BackupConfig config,
            MergeSession session,
            MergeConflictView conflict,
            MergeResolutionChoice choice,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(conflict);
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
            return CreateMergeService(config, runtime)
                .ResolveAsSide(session, conflict.ConflictId, conflict.InputSignature, choice);
        }

        /// <summary>
        /// 把界面自己合成的整份文件登记为一条冲突的解决结果（逐块选择与手改都走这里）。
        /// 「单一文件、非结构冲突」这些限制由存储层校验，它在库里才看得到那条冲突的真身。
        /// </summary>
        public static async Task<MergeSession> ImportMergeResolutionAsync(
            BackupConfig config,
            MergeSession session,
            MergeConflictView conflict,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(conflict);
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
            return await CreateMergeService(config, runtime)
                .ImportManualAsync(session, conflict.ConflictId, conflict.InputSignature, content, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// 重新读取会话的最新状态。存在的理由是应用那一步：<see cref="ApplyMergeAsync"/> 返回的是还原结果而不是会话，
        /// 而它失败时可能已经在库里把会话标成了陈旧 —— 窗口手里那一份就过期了，之后任何写操作都会被修订号比对挡下。
        /// 会话已经被回收时返回 <c>null</c>，由调用方决定怎么处理（窗口那边是保留旧引用并如实报错）。
        /// </summary>
        public static async Task<MergeSession?> ReloadMergeSessionAsync(
            BackupConfig config,
            MergeSession session,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(session);
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
            return runtime.MergeSessions.List().FirstOrDefault(stored => stored.Id == session.Id);
        }

        /// <summary>
        /// 放弃本次合并：会话到此为止，再也不可能被应用。
        /// 已发布的版本与产物不在这里删，由会话目录的清理按 roots 判定回收。
        /// </summary>
        public static async Task<MergeSession> AbandonMergeAsync(
            BackupConfig config,
            MergeSession session,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(session);
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
            return CreateMergeService(config, runtime).Abandon(session);
        }

        /// <summary>
        /// 合并动作层的构造。provider 一律<b>不传</b>、走默认（文本感知实现）：
        /// 本文件里所有 <see cref="HistoryMergeService"/> 都必须是同一个 provider ——
        /// 建会话那一处写下的版本串，要在准备、重算、解决、应用各处都对得上，否则会话直接失效。
        /// </summary>
        private static HistoryMergeService CreateMergeService(BackupConfig config, HistoryRuntime runtime)
            => new(runtime, CreateRestoreService(config, runtime));

        /// <summary>
        /// 应用期间配置可能被改。这里重新取一次权威配置再解析边界，交由应用服务拿新旧绑定做结构比对。
        /// 与切换分支那条链路用的是同一个重载器，语义保持一致。
        /// </summary>
        private static async Task<(string Revision, IReadOnlyList<HistoryRestoreSourceBinding> Bindings)>
            ReloadMergeBindingsAsync(BackupConfig config, CancellationToken cancellationToken)
            => (MergeConfigRevision,
                await ReloadBindingsAsync(config, cancellationToken).ConfigureAwait(false));
    }
}

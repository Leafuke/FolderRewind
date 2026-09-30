using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Graph;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels
{
    /// <summary>
    /// 「备份分支」页的视图模型。
    /// <para>
    /// 分支是<b>配置级</b>的：一条分支的端点指向一个配置检查点，该检查点一次性覆盖配置下的全部来源。
    /// 因此本页只按配置筛选，不提供文件夹维度。
    /// </para>
    /// <para>
    /// 分支命令（创建 / 重命名 / 删除）直接落在 <see cref="HistoryBranchService"/> 上：
    /// 校验语义（完整 Exact 检查点、名称非空且设备内唯一、多端点须先收敛、活动分支与最后一条分支不可删）
    /// 都在那里，页面只负责取输入并把失败原因原样反馈给用户。
    /// </para>
    /// </summary>
    public sealed class BackupBranchPageViewModel : ViewModelBase
    {
        private BackupConfig? _selectedConfig;
        private BackupBranchItem? _selectedBranch;
        private BranchFilterOption? _selectedBranchFilter;
        private HistoryPresentationSnapshot? _snapshot;
        private double _railWidth;
        private string _currentBranchName = string.Empty;
        private string _statusMessage = string.Empty;
        private bool _hasStatus;
        private bool _isBusy;

        public ObservableCollection<BackupConfig> Configs =>
            ConfigService.CurrentConfig?.BackupConfigs ?? new ObservableCollection<BackupConfig>();

        public ObservableCollection<BackupBranchItem> Branches { get; } = new();

        /// <summary>可以拿来建分支的备份。仅在「从某次备份创建分支」对话框里使用。</summary>
        public ObservableCollection<BackupRunItem> BranchableRuns { get; } = new();

        /// <summary>「所属分支」下拉的选项。首项是「全部分支」哨兵。</summary>
        public ObservableCollection<BranchFilterOption> BranchFilterOptions { get; } = new();

        /// <summary>备份记录，一行一个检查点。顺序就是图谱的拓扑序：新到旧、子先于父。</summary>
        public ObservableCollection<BackupRecordItem> Records { get; } = new();

        public BackupConfig? SelectedConfig
        {
            get => _selectedConfig;
            set => SetProperty(ref _selectedConfig, value);
        }

        public BackupBranchItem? SelectedBranch
        {
            get => _selectedBranch;
            set
            {
                if (SetProperty(ref _selectedBranch, value))
                {
                    RaiseCommandState();
                }
            }
        }

        /// <summary>
        /// 「所属分支」的当前选择。选中具体分支时同时交给 <see cref="SelectedBranch"/>，
        /// 5 个分支命令的可用性跟着它亮灭；选「全部分支」等于没有选中分支，按钮整体置灰。
        /// </summary>
        public BranchFilterOption? SelectedBranchFilter
        {
            get => _selectedBranchFilter;
            set
            {
                if (!SetProperty(ref _selectedBranchFilter, value))
                {
                    return;
                }

                SelectedBranch = value?.Branch;
                RebuildRecords();
            }
        }

        /// <summary>泳道图宽度。全图一个值 —— 每行同宽，竖线才能在行与行之间对齐。</summary>
        public double RailWidth
        {
            get => _railWidth;
            private set => SetProperty(ref _railWidth, value);
        }

        public bool CanRenameSelected => !_isBusy && _selectedBranch?.CanRename == true;

        public bool CanDeleteSelected => !_isBusy && _selectedBranch?.CanDelete == true;

        public bool CanCheckoutSelected => !_isBusy && _selectedBranch?.CanCheckout == true;

        /// <summary>
        /// 能不能拿选中分支发起合并：合并的方向是「选中分支 → 当前分支」，
        /// 所以站在自己头上（或根本没有活动分支）时没有可合的东西。
        /// </summary>
        public bool CanMergeSelected => !_isBusy && _selectedBranch?.CanCheckout == true;

        /// <summary>与选中项无关的命令（刷新、从备份创建分支）只受进行中状态约束。</summary>
        public bool CanStartCommand => !_isBusy;

        /// <summary>当前分支名。没有活动分支时为空，由页面回退到「尚未确定」文案。</summary>
        public string CurrentBranchName
        {
            get => _currentBranchName;
            private set
            {
                if (SetProperty(ref _currentBranchName, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(HasCurrentBranchName));
                }
            }
        }

        public bool HasCurrentBranchName => _currentBranchName.Length > 0;

        /// <summary>空态/错误态的提示文案；为空表示正常展示分支列表。</summary>
        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value ?? string.Empty);
        }

        public bool HasStatus
        {
            get => _hasStatus;
            private set => SetProperty(ref _hasStatus, value);
        }

        /// <summary>刷新与分支命令共用一个进行中状态：同一时间只允许一个操作，避免读到一半的图谱。</summary>
        public bool IsBusy
        {
            get => _isBusy;
            private set => SetProperty(ref _isBusy, value);
        }

        public bool HasRecords => Records.Count > 0;

        public bool HasBranchableRuns => BranchableRuns.Count > 0;

        public void Initialize()
        {
            SelectedConfig = Configs.FirstOrDefault();
        }

        /// <summary>
        /// 读取选中配置的分支列表、活动分支与可建分支的备份。
        /// 只读操作，不获取配置操作门 —— 该门只用于串行化捕获与提交这类会改动事实的操作。
        /// </summary>
        public Task RefreshAsync() => RunBusyAsync(RefreshCoreAsync);

        private async Task RefreshCoreAsync()
        {
            // 刷新前记下选中的分支：重建选项必然会先把选中项清空，结尾再把它接回来。
            var previousBranchId = SelectedBranchFilter?.BranchId;

            Branches.Clear();
            BranchableRuns.Clear();
            BranchFilterOptions.Clear();
            Records.Clear();
            _snapshot = null;
            SelectedBranchFilter = null;
            OnPropertyChanged(nameof(HasRecords));
            OnPropertyChanged(nameof(HasBranchableRuns));
            RailWidth = 0;
            CurrentBranchName = string.Empty;

            if (ConfigService.CurrentConfig is null || Configs.Count == 0 || SelectedConfig is null)
            {
                SetStatus(I18n.GetString("BackupBranchPage_NoConfig"));
                return;
            }

            try
            {
                var runtime = await NativeHistoryCoreGateway
                    .EnsureReadyAsync(SelectedConfig, CancellationToken.None).ConfigureAwait(true);

                // 展示层只认平台的投影：分支可操作性、可建分支的备份都由它判定，页面不另算一套。
                var snapshot = await new HistoryPresentationQueryService(runtime)
                    .QueryAsync(cancellationToken: CancellationToken.None).ConfigureAwait(true);

                foreach (var branch in snapshot.Branches)
                {
                    Branches.Add(new BackupBranchItem(branch));
                }

                foreach (var run in snapshot.Runs.Where(run => run.IsBranchableCheckpoint))
                {
                    BranchableRuns.Add(new BackupRunItem(run));
                }

                _snapshot = snapshot;
                BranchFilterOptions.Add(BranchFilterOption.AllBranches);
                foreach (var branch in Branches)
                {
                    BranchFilterOptions.Add(new BranchFilterOption(branch));
                }

                CurrentBranchName = snapshot.Branches.FirstOrDefault(branch => branch.IsActive)?.Name
                    ?? string.Empty;
                OnPropertyChanged(nameof(HasBranchableRuns));

                // 恢复选择必须放在最后：重置选项集合会让下拉把选中项报成 null 再写回来，
                // 先恢复就会被那一下冲掉。哨兵总在，所以这里不会落空。
                SelectedBranchFilter = BranchFilterOptions
                    .FirstOrDefault(option => option.BranchId == previousBranchId) ?? BranchFilterOptions[0];

                SetStatus(Branches.Count == 0
                    ? I18n.GetString("BackupBranchPage_Empty")
                    : Records.Count == 0
                        ? I18n.GetString("BackupBranchPage_RecordsEmpty")
                        : null);
            }
            catch (Exception ex)
            {
                SetStatus(I18n.Format("BackupBranchPage_LoadFailed", ex.Message));
            }
        }

        /// <summary>
        /// 按当前「所属分支」的选择重算记录列表。数据全部来自刷新时留下的快照，不读库、不 await。
        /// <para>
        /// 刻意<b>不</b>走 <see cref="RunBusyAsync"/>：本页的忙碌是<b>拒绝门控</b>
        /// （<see cref="TryEnterBusy"/> 忙时直接返回 false，整段逻辑被跳过），
        /// 而切下拉是瞬时的本地重算 —— 套上去只会在忙碌中被静默吞掉，表现为「点了没反应」。
        /// </para>
        /// </summary>
        private void RebuildRecords()
        {
            Records.Clear();

            if (_snapshot is not { } snapshot)
            {
                RailWidth = 0;
                OnPropertyChanged(nameof(HasRecords));
                return;
            }

            var nodes = snapshot.Checkpoints
                .Select(checkpoint => new CheckpointGraphNode(
                    checkpoint.CheckpointId, checkpoint.CreatedAtUtc, checkpoint.ParentCheckpointIds))
                .ToImmutableArray();
            var layout = CheckpointGraphLayoutBuilder.Build(nodes, CheckpointGraphLayout.PaletteSize);
            var summaries = snapshot.Checkpoints.ToDictionary(checkpoint => checkpoint.CheckpointId);
            var selectedBranchId = SelectedBranchFilter?.BranchId;

            // 宽度取整张图的泳道数：只按筛选结果排会算出逐行不同的宽度，竖线当场错位。
            RailWidth = layout.LaneCount * CheckpointGraphLayout.LaneWidth;

            foreach (var row in layout.Rows)
            {
                var summary = summaries[row.Id];
                var isDimmed = selectedBranchId is { } branchId && !summary.BranchIds.Contains(branchId);
                Records.Add(new BackupRecordItem(summary, row, RailWidth, isDimmed));
            }

            OnPropertyChanged(nameof(HasRecords));
        }

        /// <summary>在指定备份的检查点上新建一条分支。新分支是休眠的，不会自动切换过去。</summary>
        public Task<bool> CreateBranchAsync(CheckpointId checkpointId, string name)
            => RunBranchCommandAsync(
                (runtime, token) => runtime.Branches.CreateFromCheckpointAsync(checkpointId, name, token),
                "BackupBranchPage_BranchCreated",
                name);

        public Task<bool> RenameBranchAsync(BackupBranchItem branch, string newName)
            => RunBranchCommandAsync(
                (runtime, token) => runtime.Branches.RenameAsync(branch.BranchId, newName, token),
                "BackupBranchPage_BranchRenamed",
                newName);

        public Task<bool> DeleteBranchAsync(BackupBranchItem branch)
            => RunBranchCommandAsync(
                (runtime, token) => runtime.Branches.DeleteAsync(branch.BranchId, token),
                "BackupBranchPage_BranchDeleted",
                branch.Name);

        /// <summary>
        /// 切换分支前先算一遍计划，让页面能把「能不能切、切之前要不要先保护当前内容」讲给用户。
        /// 这里用浅评估：它只是给确认框看的，真正的校验在执行时还会用深评估重来一遍。
        /// </summary>
        public async Task<HistoryCheckoutPlan?> PlanCheckoutAsync(BackupBranchItem branch)
        {
            var config = SelectedConfig;
            if (config is null || branch.TipUpdateId is not { } tipId)
            {
                return null;
            }

            try
            {
                return await NativeHistoryApplicationService
                    .PlanCheckoutAsync(config, tipId, AssessmentDepth.Fast)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                NotificationService.ShowError(
                    ex.Message, I18n.GetString("BackupBranchPage_CommandFailedTitle"));
                return null;
            }
        }

        /// <summary>
        /// 发起一次合并：把选中分支合进当前活动分支，并建出一个待解决的会话。
        /// <para>
        /// 这一步<b>不动</b>任何源目录 —— 它只把三份输入物化到会话目录并算出冲突。
        /// 真正落地在合并窗口里按下「应用到工作区」时，那一步平台会先做保护点。
        /// </para>
        /// <para>
        /// 返回 <c>null</c> 有两种情形，都已经给过用户提示，调用方不必再问一次：
        /// <b>没有可合并的内容</b>（提示「没有变化」），以及<b>前置条件不满足而失败</b>（提示失败原因）。
        /// </para>
        /// </summary>
        public async Task<MergeSession?> StartMergeAsync(BackupBranchItem branch)
        {
            var config = SelectedConfig;
            if (config is null)
            {
                return null;
            }

            // 失败与否要跟着结果一起带出来：两者都是 null 会话，但只该给「没有变化」道贺。
            var (entered, (failed, session)) = await RunBusyAsync<(bool Failed, MergeSession? Session)>(async () =>
            {
                try
                {
                    return (false, await NativeHistoryApplicationService
                        .StartMergeAsync(config, branch.BranchId, CancellationToken.None)
                        .ConfigureAwait(true));
                }
                catch (Exception ex)
                {
                    // 合并的前置条件（两边都得有唯一且可用的端点、工作区得指向当前分支）由平台判，
                    // 原因原样说出来，别让用户对着灰按钮猜。
                    NotificationService.ShowWarning(
                        ex.Message, I18n.GetString("BackupBranchPage_CommandFailedTitle"));
                    return (true, null);
                }
            }).ConfigureAwait(true);

            if (!entered || session is null)
            {
                if (entered && !failed)
                {
                    NotificationService.ShowSuccess(
                        I18n.GetString("BackupBranchPage_MergeNoChanges"),
                        I18n.GetString("BackupBranchPage_Title.Text"));
                }

                return null;
            }

            return session;
        }

        /// <summary>
        /// 切换分支：把配置下的源目录替换成目标分支端点那次备份时的状态。
        /// 当前内容若有未收进历史的改动，服务会先做保护点，因此这里不需要页面另行备份。
        /// </summary>
        public async Task<bool> CheckoutAsync(BackupBranchItem branch)
        {
            var config = SelectedConfig;
            if (config is null || branch.TipUpdateId is not { } tipId)
            {
                return false;
            }

            var (entered, result) = await RunBusyAsync<HistoryRestoreResult?>(async () =>
            {
                try
                {
                    return await NativeHistoryApplicationService
                        .CheckoutAsync(config, tipId, CancellationToken.None)
                        .ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    NotificationService.ShowError(
                        ex.Message, I18n.GetString("BackupBranchPage_CommandFailedTitle"));
                    return null;
                }
            }).ConfigureAwait(true);

            if (!entered || result is null)
            {
                return false;
            }

            if (result.Status == HistoryRestoreStatus.CommittedRecoveryRequired)
            {
                // 工作区已经指向新分支了，但本机派生态没跟上：说清楚，别让用户以为一切正常。
                NotificationService.ShowWarning(
                    I18n.Format("BackupBranchPage_CheckoutRecoveryRequired", result.Diagnostic),
                    I18n.GetString("BackupBranchPage_CommandFailedTitle"));
            }
            else if (result.Succeeded)
            {
                NotificationService.ShowSuccess(
                    I18n.Format("BackupBranchPage_CheckoutSucceeded", branch.Name),
                    I18n.GetString("BackupBranchPage_Title.Text"));
            }
            else
            {
                NotificationService.ShowWarning(
                    result.Diagnostic, I18n.GetString("BackupBranchPage_CheckoutBlockedTitle"));
            }

            await RefreshAsync().ConfigureAwait(true);
            return result.Succeeded;
        }

        /// <summary>
        /// 执行一条分支命令：命令本身是事实变更，由平台在锁内完成全部校验。
        /// 这里只做三件事 —— 取运行时、把失败原因反馈给用户、成功后重新读取列表。
        /// </summary>
        private async Task<bool> RunBranchCommandAsync(
            Func<HistoryRuntime, CancellationToken, Task<HistoryBranchCommandResult>> command,
            string successMessageKey,
            string successSubject)
        {
            var config = SelectedConfig;
            if (config is null)
            {
                return false;
            }

            var (entered, succeeded) = await RunBusyAsync(async () =>
            {
                try
                {
                    var runtime = await NativeHistoryCoreGateway
                        .EnsureReadyAsync(config, CancellationToken.None).ConfigureAwait(true);
                    await command(runtime, CancellationToken.None).ConfigureAwait(true);
                    return true;
                }
                catch (HistoryBranchCommandException ex)
                {
                    // 校验失败：原因来自平台规则，原样说出来，别让用户对着禁用按钮猜。
                    NotificationService.ShowWarning(
                        ex.Message, I18n.GetString("BackupBranchPage_CommandFailedTitle"));
                    return false;
                }
                catch (Exception ex)
                {
                    NotificationService.ShowError(
                        ex.Message, I18n.GetString("BackupBranchPage_CommandFailedTitle"));
                    return false;
                }
            }).ConfigureAwait(true);

            if (entered && succeeded)
            {
                NotificationService.ShowSuccess(
                    I18n.Format(successMessageKey, successSubject),
                    I18n.GetString("BackupBranchPage_Title.Text"));
                await RefreshAsync().ConfigureAwait(true);
            }

            return succeeded;
        }

        protected override bool TryEnterBusy()
        {
            if (IsBusy)
            {
                return false;
            }

            IsBusy = true;
            RaiseCommandState();
            return true;
        }

        protected override void ExitBusy()
        {
            IsBusy = false;
            RaiseCommandState();
        }

        private void RaiseCommandState()
        {
            OnPropertyChanged(nameof(CanRenameSelected));
            OnPropertyChanged(nameof(CanDeleteSelected));
            OnPropertyChanged(nameof(CanCheckoutSelected));
            OnPropertyChanged(nameof(CanMergeSelected));
            OnPropertyChanged(nameof(CanStartCommand));
        }

        /// <summary>
        /// 设置空态/错误态文案。加载中由 <see cref="IsBusy"/> 单独驱动，
        /// 不占用这一块 —— 这里只表达「确实没有可展示内容」。
        /// </summary>
        private void SetStatus(string? message)
        {
            StatusMessage = message ?? string.Empty;
            HasStatus = !string.IsNullOrWhiteSpace(message);
        }
    }
}

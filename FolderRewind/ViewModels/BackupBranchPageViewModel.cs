using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using FolderRewind.Services;
using System;
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
        private string _currentBranchName = string.Empty;
        private string _statusMessage = string.Empty;
        private bool _hasStatus;
        private bool _isBusy;

        public ObservableCollection<BackupConfig> Configs =>
            ConfigService.CurrentConfig?.BackupConfigs ?? new ObservableCollection<BackupConfig>();

        public ObservableCollection<BackupBranchItem> Branches { get; } = new();

        /// <summary>可以拿来建分支的备份。仅在「从某次备份创建分支」对话框里使用。</summary>
        public ObservableCollection<BackupRunItem> BranchableRuns { get; } = new();

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

        public bool CanRenameSelected => !_isBusy && _selectedBranch?.CanRename == true;

        public bool CanDeleteSelected => !_isBusy && _selectedBranch?.CanDelete == true;

        public bool CanCheckoutSelected => !_isBusy && _selectedBranch?.CanCheckout == true;

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

        public bool HasBranches => Branches.Count > 0;

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
            Branches.Clear();
            BranchableRuns.Clear();
            SelectedBranch = null;
            OnPropertyChanged(nameof(HasBranches));
            OnPropertyChanged(nameof(HasBranchableRuns));
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

                CurrentBranchName = snapshot.Branches.FirstOrDefault(branch => branch.IsActive)?.Name
                    ?? string.Empty;
                OnPropertyChanged(nameof(HasBranches));
                OnPropertyChanged(nameof(HasBranchableRuns));
                SetStatus(Branches.Count == 0 ? I18n.GetString("BackupBranchPage_Empty") : null);
            }
            catch (Exception ex)
            {
                SetStatus(I18n.Format("BackupBranchPage_LoadFailed", ex.Message));
            }
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
                    I18n.GetString("BackupBranchPage_Title"));
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
                    I18n.GetString("BackupBranchPage_Title"));
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

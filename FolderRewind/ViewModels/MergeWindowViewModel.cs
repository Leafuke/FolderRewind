using CommunityToolkit.Mvvm.Input;
using FolderRewind.History.Application;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels
{
    /// <summary>
    /// 合并窗口的视图模型：列出冲突、逐块给出选择、把结果交给平台。
    /// <para>
    /// 分工是明确的：本类只做「拼出用户要的整份文件」与「把动作转给平台」，
    /// 校验（单文件、非结构冲突、输入未变）在存储层，冲突判定在合并引擎 —— 窗口不重算任何一条规则。
    /// </para>
    /// <para>
    /// 窗口有两种形态，由「有没有冲突」决定：有冲突时是逐条冲突的解决界面；
    /// 没有冲突时没人可问，中间栏改铺<b>已自动合并的结果</b>（只读）—— 用户点开窗口是为了看合并干了什么，
    /// 让他对着一个空窗口猜「是不是坏了」，比多铺一层预览糟糕得多。两种形态都留着「应用到工作区」。
    /// </para>
    /// </summary>
    public sealed class MergeWindowViewModel : ViewModelBase
    {
        /// <summary>未变化区段超过这么多行就默认折叠。见 <see cref="MergeHunkItem"/> 的说明。</summary>
        private const int CollapseThreshold = 20;

        private readonly BackupConfig _config;

        // 窗口内的临时状态，按冲突 id 存。会话一旦重算（修订号变了），三者都作废。
        private readonly Dictionary<string, LineMergeChoice> _hunkChoices = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _resultEdits = new(StringComparer.Ordinal);

        // 已经交给平台的那一份手改内容。它<b>不算未提交的改动</b>（见 HasUnsubmittedEdit），
        // 但结果栏仍要显示它，所以不能像「重置」那样把 _resultEdits 一并丢掉。
        private readonly Dictionary<string, string> _submittedEdits = new(StringComparer.Ordinal);

        private MergeSession _session;
        private MergeConflictItem? _selectedConflict;
        private MergeTextConflictView? _text;

        // 一条冲突都没有时才有值：本次自动合掉的东西。有冲突时它是 null，中间栏归逐冲突视图。
        private MergeAutoMergeView? _autoMerge;

        private string _resultText = string.Empty;
        private bool _isHandEdited;
        private bool _showBase;
        private bool _isBusy;
        private int _busyDepth;
        private string _statusMessage = string.Empty;

        // 「载入详情」可能被两条路径同时触发（选中变化、解决完刷新），用令牌让后一次作数、
        // 前一次在 await 之后自行退出 —— 否则晚到的旧结果会盖掉新选中项的内容。
        private int _loadToken;

        public MergeWindowViewModel(BackupConfig config, MergeSession session)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public ObservableCollection<MergeConflictItem> Conflicts { get; } = new();

        /// <summary>当前冲突的区段。整份取一边的冲突这里是空的。</summary>
        public ObservableCollection<MergeHunkItem> Hunks { get; } = new();

        /// <summary>
        /// 列表选中项。只是<b>数据</b>，不是动作 —— 载入详情由页面在选中变化后调
        /// <see cref="LoadSelectedAsync"/> 完成，属性设置器里发起异步操作会变成难以追踪的即发即弃。
        /// </summary>
        public MergeConflictItem? SelectedConflict
        {
            get => _selectedConflict;
            set
            {
                if (SetProperty(ref _selectedConflict, value))
                {
                    OnPropertyChanged(nameof(HasSelectedConflict));
                    OnPropertyChanged(nameof(CanNavigatePrevious));
                    OnPropertyChanged(nameof(CanNavigateNext));
                    OnPropertyChanged(nameof(CanResolveSelection));
                }
            }
        }

        public bool HasSelectedConflict => _selectedConflict is not null;

        /// <summary>本次合并的方向：把来源分支合进当前分支。</summary>
        public string SourceBranchName => _session.Plan.Theirs.Name;

        public string TargetBranchName => _session.Plan.Ours.Name;

        public string DirectionText => I18n.Format("Merge_Direction", TargetBranchName, SourceBranchName);

        public bool HasConflicts => Conflicts.Count > 0;

        public int ConflictCount => Conflicts.Count;

        public int ResolvedCount => Conflicts.Count(item => item.IsResolved);

        /// <summary>
        /// 标题栏上的进度。没有冲突时改说「自动合了多少来源」，别显示成「0 / 0」——
        /// 那种情形下窗口展示的本来就不是冲突，而是一份已经合好的结果。
        /// </summary>
        public string ProgressText => Conflicts.Count == 0
            ? I18n.Format("Merge_ProgressAutoMerged", _autoMerge?.SourceCount ?? 0)
            : I18n.Format("Merge_Progress", ResolvedCount, Conflicts.Count);

        /// <summary>
        /// 窗口现在展示的是「已自动合并的结果」而不是逐条冲突。
        /// <para>
        /// 一条冲突都没有时会话也已经可以应用，除了直接应用没别的可做；中间栏与其空着，
        /// 不如把已经合好的东西铺开让人过目 —— 用户点开窗口是为了看合并到底干了什么，
        /// 而不是为了看一句「没有冲突」。
        /// </para>
        /// </summary>
        public bool IsAutoMergePreview => Conflicts.Count == 0;

        /// <summary>预览区上方那句话。还没读到结果时为空。</summary>
        public string AutoMergeSummary => _autoMerge is null
            ? string.Empty
            : I18n.Format("Merge_AutoMerge_Summary", _autoMerge.Files.Length);

        private int UnviewableAutoMergedCount => _autoMerge?.Files.Count(file => file.Documents is null) ?? 0;

        /// <summary>
        /// 有没有合完了却摊不开的文件（二进制、缺一侧内容、编码认不出）。
        /// 有的话要明说一句：否则用户会以为这批文件没被合过。
        /// </summary>
        public bool HasUnviewableAutoMergedFiles => UnviewableAutoMergedCount > 0;

        public string AutoMergeUnviewableText => I18n.Format("Merge_AutoMerge_Unviewable", UnviewableAutoMergedCount);

        /// <summary>「从左侧选一条冲突」那句提示只在真有冲突、又没选中时才该出现。</summary>
        public bool ShowNoSelectionHint => HasConflicts && !HasSelectedConflict;

        /// <summary>冲突导航到头就停：到头再点没反应，比绕回第一条更容易看懂。</summary>
        public bool CanNavigatePrevious => IndexOfSelection() > 0;

        public bool CanNavigateNext => IndexOfSelection() is var index && index >= 0 && index < Conflicts.Count - 1;

        /// <summary>
        /// 能不能对当前冲突做解决动作。除了会话必须在可解决的状态，还要求<b>这一条冲突确实存在</b> ——
        /// 无冲突的会话也会开这个窗口，那时按钮该是灰的而不是点了报错。
        /// </summary>
        public bool CanResolveSelection => !_isBusy && HasSelectedConflict
            && _session.State is MergeSessionState.Resolving or MergeSessionState.Ready;

        /// <summary>重算与放弃不受选中项影响，只受进行中状态约束。</summary>
        public bool CanStartCommand => !_isBusy;

        /// <summary>当前冲突的路径。整份取一边的冲突也显示它，用户至少要知道在解决哪个文件。</summary>
        public string ConflictPath => _selectedConflict?.Title ?? string.Empty;

        public string ConflictKindText => _selectedConflict?.KindText ?? string.Empty;

        /// <summary>这条冲突能逐块处理：有共同祖先、单一文件、扩展名在白名单里。</summary>
        public bool IsEditable => _text?.CanEdit == true;

        /// <summary>
        /// 不能逐块处理时给用户的原因。分两种：引擎明确拒绝了（附原因），
        /// 或者这条冲突压根不是「一个文件的三份内容」那回事（多文件、结构冲突）。后者也要说清楚，
        /// 否则用户对着两个灰按钮猜。
        /// </summary>
        public string SideOnlyText => _text?.Refusal is { } refusal
            ? I18n.GetString(RefusalKey(refusal.Kind))
            : I18n.GetString("Merge_SideOnlyGeneric");

        /// <summary>逐块选择与手改都不可用的冲突：只能整份取一边，页面上要说明清楚。</summary>
        public bool ShowSideOnlyNotice => !IsEditable && HasSelectedConflict;

        /// <summary>结果文本框的内容。它是「将要提交的那一整份文件」，也是唯一被提交的东西。</summary>
        public string ResultText
        {
            get => _resultText;
            private set => SetProperty(ref _resultText, value ?? string.Empty);
        }

        /// <summary>
        /// 结果被手改过。手改之后逐块选择不再改写结果（那会把手改的内容悄悄抹掉），
        /// 要回到逐块组合得先按「按逐块结果重置」。
        /// </summary>
        public bool IsHandEdited
        {
            get => _isHandEdited;
            private set
            {
                if (SetProperty(ref _isHandEdited, value))
                {
                    OnPropertyChanged(nameof(CanSubmitManual));
                    OnPropertyChanged(nameof(CanApply));
                    UpdateHunkAvailability();
                }
            }
        }

        /// <summary>逐块组合的结果可以提交：所有冲突块都选过了，或者用户自己写好了整份结果。</summary>
        public bool CanSubmitManual => CanResolveSelection && IsEditable
            && (IsHandEdited || Hunks.Where(hunk => hunk.IsConflict).All(hunk => hunk.Choice is not null));

        /// <summary>
        /// 可以应用。<b>还要求窗口里没有未提交的改动</b> ——
        /// 库里的会话已经「全部解决」而用户手上还捏着一份改过没保存的结果时，直接应用会用库里那份，
        /// 他会以为自己改的东西生效了。
        /// </summary>
        public bool CanApply => !_isBusy && _session.State == MergeSessionState.Ready && !HasUnsubmittedChanges;

        /// <summary>
        /// 窗口里是否有尚未交给平台的改动。
        /// <para>
        /// <b>只看手改。</b>这里曾经还看过「有冲突块没选」（<c>Hunks.Any(hunk => hunk.IsPending)</c>），那是错的：
        /// 直接手改整份结果、以及「整份取一边」这两种解决方式，用户根本不需要碰逐块按钮，
        /// 那几块就永远停在「待选择」，把这一格钉死成 <c>true</c> —— 会话早已 <c>Ready</c>，
        /// 「应用到工作区」却再也不亮（阶段四实测到的就是这个）。
        /// </para>
        /// <para>
        /// 「逐块只选了一半」不需要在这里管：那时冲突还没解决，<see cref="CanApply"/> 的
        /// <c>State == Ready</c> 一项已经拦住了。
        /// </para>
        /// </summary>
        public bool HasUnsubmittedChanges => HasUnsubmittedEdit;

        /// <summary>
        /// 手上是否捏着一份还没交出去的手改。
        /// <para>
        /// <b>交出去过的那一份不算。</b>按过「保存手改结果」之后，内容仍然留在结果栏里
        /// （用户该继续看到自己写的东西），但它已经在库里了；继续算成「未提交」会让
        /// 「应用到工作区」一直灰着，而会话其实早就可以应用 —— 用户会以为自己白保存了。
        /// 之后再改一次，内容与交出去的那一份不再相同，它又变回未提交。
        /// </para>
        /// </summary>
        private bool HasUnsubmittedEdit => IsHandEdited && SelectedConflict is { } conflict
            && !(_submittedEdits.TryGetValue(conflict.ConflictId, out var submitted)
                && string.Equals(submitted, ResultText, StringComparison.Ordinal));

        /// <summary>把共同祖先一并显示出来（每块之内，不占第四栏）。</summary>
        public bool ShowBase
        {
            get => _showBase;
            set
            {
                if (!SetProperty(ref _showBase, value))
                {
                    return;
                }

                foreach (var hunk in Hunks)
                {
                    hunk.ShowBase = value;
                }
            }
        }

        /// <summary>
        /// 有没有动作正在跑。驱动界面的禁用与忙指示，是<b>界面锁，不是互斥锁</b> ——
        /// 真正拦住并发写的还是平台那条会话修订号 CAS。
        /// </summary>
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (!SetProperty(ref _isBusy, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(CanApply));
                OnPropertyChanged(nameof(CanResolveSelection));
                OnPropertyChanged(nameof(CanStartCommand));
                UpdateHunkAvailability();
            }
        }

        /// <summary>
        /// 进忙。<b>只累加计数，不在这里拒绝</b>：本视图模型的动作本来是嵌套的
        /// （<see cref="RunResolveAsync"/> 里一次解决之后还要 <see cref="RefreshConflictsAsync"/>
        /// 与 <see cref="LoadSelectedAsync"/>，三层都走 <c>RunBusyAsync</c>），
        /// 若在外层已经忙时拒绝内层，内层的刷新与载入会被<b>静默跳过</b>
        /// （<c>RunBusyAsync</c> 返回 <c>false</c> 加一个默认值，列表看着纹丝不动），比不锁更难查。
        /// 界面状态只在最外层进出时翻转 —— 基类把进出配成对（<c>try/finally</c>），计数不会失衡。
        /// </summary>
        protected override bool TryEnterBusy()
        {
            if (++_busyDepth == 1)
            {
                IsBusy = true;
            }

            return true;
        }

        protected override void ExitBusy()
        {
            if (--_busyDepth == 0)
            {
                IsBusy = false;
            }
        }

        /// <summary>错误与空态文案。为空表示一切正常。</summary>
        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value ?? string.Empty);
        }

        /// <summary>
        /// 关窗口前是否需要先问一句：会话还在半途就得说清楚「关掉即放弃」。
        /// 已提交或已放弃的会话没有可放弃的东西，不必打扰用户。
        /// </summary>
        public bool NeedsDecision => _session.State is not (MergeSessionState.Committed or MergeSessionState.Abandoned);

        public async Task InitializeAsync()
        {
            await RefreshConflictsAsync().ConfigureAwait(true);
            SelectedConflict = Conflicts.FirstOrDefault();
            await LoadSelectedAsync().ConfigureAwait(true);
        }

        /// <summary>
        /// 重新读取冲突列表与解决进度，并<b>就地</b>更新已有条目。
        /// 就地更新是为了留住选中项与滚动位置：每次解决一条就重建列表，用户刚点过的那一条会跳走。
        /// </summary>
        public async Task RefreshConflictsAsync()
        {
            var (entered, views) = await RunBusyAsync<IReadOnlyList<MergeConflictView>?>(async () =>
            {
                try
                {
                    return await NativeHistoryApplicationService
                        .ListMergeConflictsAsync(_config, _session, CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    SetStatus(ex.Message);
                    return null;
                }
            }).ConfigureAwait(true);

            if (!entered || views is null)
            {
                return;
            }

            var selectedId = SelectedConflict?.ConflictId;
            var existing = Conflicts.ToDictionary(item => item.ConflictId, StringComparer.Ordinal);
            Conflicts.Clear();
            foreach (var view in views)
            {
                if (existing.TryGetValue(view.ConflictId, out var item))
                {
                    item.Update(view);
                    Conflicts.Add(item);
                }
                else
                {
                    Conflicts.Add(new MergeConflictItem(view));
                }
            }

            SelectedConflict = Conflicts.FirstOrDefault(item => item.ConflictId == selectedId)
                ?? Conflicts.FirstOrDefault();
            OnPropertyChanged(nameof(HasConflicts));
            OnPropertyChanged(nameof(ConflictCount));
            OnPropertyChanged(nameof(ResolvedCount));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(NeedsDecision));
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(CanNavigatePrevious));
            OnPropertyChanged(nameof(CanNavigateNext));
            OnPropertyChanged(nameof(CanResolveSelection));
            OnPropertyChanged(nameof(IsAutoMergePreview));
            OnPropertyChanged(nameof(ShowNoSelectionHint));

            // 「没有冲突」这件事本身由中间的预览卡片说明，不再占用状态栏 ——
            // 状态栏要留给错误，把一句常态文案摆在那里，真出错时容易被当成同一类信息忽略掉。
            SetStatus(null);

            if (Conflicts.Count == 0)
            {
                await LoadAutoMergedAsync().ConfigureAwait(true);
                return;
            }

            // 从「没有冲突」变成「有冲突」（重算、或应用失败后重读）时把上一次的预览丢掉，
            // 否则摘要还会报着已经作废的数字。
            _autoMerge = null;
            OnPropertyChanged(nameof(AutoMergeSummary));
            OnPropertyChanged(nameof(HasUnviewableAutoMergedFiles));
            OnPropertyChanged(nameof(AutoMergeUnviewableText));
        }

        /// <summary>
        /// 读出自动合并掉的内容并铺成区段流。只在一条冲突都没有时有事可做 ——
        /// 有冲突时中间栏归逐冲突视图，这里直接返回，不覆盖它。
        /// </summary>
        private async Task LoadAutoMergedAsync()
        {
            if (Conflicts.Count > 0)
            {
                return;
            }

            var (entered, view) = await RunBusyAsync<MergeAutoMergeView?>(async () =>
            {
                try
                {
                    return await NativeHistoryApplicationService
                        .ListAutoMergedAsync(_config, _session, CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    SetStatus(ex.Message);
                    return null;
                }
            }).ConfigureAwait(true);

            if (!entered || view is null)
            {
                return;
            }

            _autoMerge = view;
            BuildAutoMergeHunks(view);
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(AutoMergeSummary));
            OnPropertyChanged(nameof(HasUnviewableAutoMergedFiles));
            OnPropertyChanged(nameof(AutoMergeUnviewableText));
        }

        /// <summary>
        /// 把自动合并的结果铺成区段流。
        /// <para>
        /// 这里全是只读的：这些文件已经没有冲突块，也就没有可选的；更不该让人在这儿手改 ——
        /// 手改是「解决某一条冲突」的动作，而没有冲突时没有可挂靠的那一条。
        /// </para>
        /// </summary>
        private void BuildAutoMergeHunks(MergeAutoMergeView view)
        {
            Hunks.Clear();
            foreach (var file in view.Files)
            {
                if (file.Documents is not { } documents)
                {
                    continue;
                }

                // 只有一个来源时文件名自己就说明白了，不必再冠一遍来源名。
                var header = view.SourceCount > 1
                    ? I18n.Format("Merge_AutoMerge_FileHeader", file.SourceName, file.Path)
                    : file.Path;
                var index = 0;
                foreach (var hunk in documents.Outcome.Hunks)
                {
                    Hunks.Add(new MergeHunkItem(index, hunk, CollapseThreshold)
                    {
                        ShowBase = _showBase,
                        FileHeader = index == 0 ? header : string.Empty
                    });
                    index++;
                }
            }

            UpdateHunkAvailability();
        }

        /// <summary>载入选中冲突的详情：能逐块编辑的读三方内容，其余的只读出「为什么不能」。</summary>
        public async Task LoadSelectedAsync()
        {
            var token = ++_loadToken;

            if (SelectedConflict is not { } conflict || !conflict.CanEditPerHunk)
            {
                // 没有冲突可看时中间栏留的是「已自动合并」预览，别把它清掉；
                // 真选了一条（只是这条不能逐块处理）才该清空重来。
                if (HasSelectedConflict)
                {
                    Hunks.Clear();
                }

                _text = null;
                SetEditState(false, string.Empty);
                RaiseDetailState();
                return;
            }

            Hunks.Clear();
            RaiseDetailState();

            var (entered, text) = await RunBusyAsync<MergeTextConflictView?>(async () =>
            {
                try
                {
                    return await NativeHistoryApplicationService
                        .LoadMergeTextAsync(_config, _session, conflict.ConflictId, CancellationToken.None)
                        .ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    SetStatus(ex.Message);
                    return null;
                }
            }).ConfigureAwait(true);

            if (!entered || text is null || token != _loadToken)
            {
                return;
            }

            _text = text;
            BuildHunks(conflict.ConflictId, text);

            // 手改过的结果留在窗口自己的记忆里：切走再切回来，用户写的东西还在。
            if (_resultEdits.TryGetValue(conflict.ConflictId, out var edited))
            {
                SetEditState(true, edited);
            }
            else
            {
                SetEditState(false, ComposeResult());
            }

            RaiseDetailState();
        }

        /// <summary>详情区随选中项与载入结果变化的那些属性，一处触发，免得漏掉某一个。</summary>
        private void RaiseDetailState()
        {
            OnPropertyChanged(nameof(CanResolveSelection));
            OnPropertyChanged(nameof(IsEditable));
            OnPropertyChanged(nameof(SideOnlyText));
            OnPropertyChanged(nameof(ShowSideOnlyNotice));
            OnPropertyChanged(nameof(ConflictPath));
            OnPropertyChanged(nameof(ConflictKindText));
            OnPropertyChanged(nameof(CanSubmitManual));
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(HasUnsubmittedChanges));
        }

        private int IndexOfSelection() => _selectedConflict is null ? -1 : Conflicts.IndexOf(_selectedConflict);

        /// <summary>整份取一边。适用于任何冲突，逐块编辑不了的那些只能走这条路。</summary>
        public Task ResolveWholeSideAsync(MergeResolutionChoice choice)
        {
            if (choice == MergeResolutionChoice.Manual)
            {
                throw new InvalidOperationException("Whole-side resolution cannot be Manual.");
            }

            return RunResolveAsync(() => NativeHistoryApplicationService
                .ResolveMergeConflictAsync(_config, _session, SelectedConflict!.View, choice, CancellationToken.None));
        }

        /// <summary>
        /// 把界面里这份结果登记为手动解决：逐块选择拼出来的，或用户手改的，都走这一条。
        /// 空白结果也允许提交 —— 「把文件清空」是一个明确的决定，不该被界面当成误操作拦下来。
        /// </summary>
        public async Task SubmitManualAsync()
        {
            if (SelectedConflict is not { } conflict || _text is not { Documents: { } documents })
            {
                return;
            }

            var text = ResultText;
            var bytes = TextMergePolicy.SerializeEdited(text, documents.Ours);
            if (!await RunResolveAsync(() => NativeHistoryApplicationService
                    .ImportMergeResolutionAsync(_config, _session, conflict.View, bytes, CancellationToken.None))
                .ConfigureAwait(true))
            {
                return;
            }

            // 交出去的这一份从此不算未提交，内容照旧留在结果栏里。
            _submittedEdits[conflict.ConflictId] = text;
            OnPropertyChanged(nameof(HasUnsubmittedChanges));
            OnPropertyChanged(nameof(CanApply));
        }

        /// <summary>
        /// 结果被用户改动。此后逐块选择不再改写它，见 <see cref="IsHandEdited"/>。
        /// <para>
        /// 只该由「用户真的敲了键盘」触发。视图把结果推进文本框时也会引发一次变更通知，
        /// 那一次由视图自己认出来：推进的文本记在 <c>MergeWindow._pushedText</c> 上，
        /// TextChanged 到达时内容一致就当回声跳过。
        /// </para>
        /// <para>
        /// 这里不拿 <see cref="ResultText"/> 去比对来猜 —— 判据是「调用方说是人改的」。
        /// 靠比对猜的话，「改回原样」这种真实编辑会被当成回声丢掉。
        /// </para>
        /// </summary>
        public void MarkResultEdited(string text)
        {
            if (SelectedConflict is not { } conflict || !IsEditable)
            {
                return;
            }

            _resultEdits[conflict.ConflictId] = text ?? string.Empty;
            SetEditState(true, text ?? string.Empty);
        }

        /// <summary>丢掉手改，回到逐块组合的结果。</summary>
        public void ResetResultEdit()
        {
            if (SelectedConflict is not { } conflict)
            {
                return;
            }

            _resultEdits.Remove(conflict.ConflictId);
            _submittedEdits.Remove(conflict.ConflictId);
            SetEditState(false, ComposeResult());
        }

        /// <summary>
        /// 在冲突之间前后移动。到头就停住，不绕回 —— 绕回会让人以为还有没看的冲突。
        /// <para>
        /// 这里改了选中项，列表的选中事件还会再触发一次载入；重复的那次由 <see cref="_loadToken"/> 收掉，
        /// 留着这一句是为了不依赖「列表一定会发事件」这件事。
        /// </para>
        /// </summary>
        public async Task<bool> MoveSelectionAsync(int delta)
        {
            if (_selectedConflict is null)
            {
                return false;
            }

            var index = Conflicts.IndexOf(_selectedConflict);
            var target = index + delta;
            if (index < 0 || target < 0 || target >= Conflicts.Count)
            {
                return false;
            }

            SelectedConflict = Conflicts[target];
            await LoadSelectedAsync().ConfigureAwait(true);
            return true;
        }

        /// <summary>按当前分支端点与配置重算计划。重算之后冲突是全新的一批，窗口里的临时状态一并作废。</summary>
        public async Task RecomputeAsync()
        {
            var (entered, session) = await RunBusyAsync<MergeSession?>(async () =>
            {
                try
                {
                    return await NativeHistoryApplicationService
                        .RecomputeMergeAsync(_config, _session, CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    SetStatus(ex.Message);
                    return null;
                }
            }).ConfigureAwait(true);

            if (!entered || session is null)
            {
                return;
            }

            _session = session;
            _hunkChoices.Clear();
            _resultEdits.Clear();
            _submittedEdits.Clear();
            SelectedConflict = null;
            await InitializeAsync().ConfigureAwait(true);
        }

        /// <summary>放弃本次合并。会话到此为止，再也不可能被应用。</summary>
        public async Task AbandonAsync()
        {
            var (entered, session) = await RunBusyAsync<MergeSession?>(async () =>
            {
                try
                {
                    return await NativeHistoryApplicationService
                        .AbandonMergeAsync(_config, _session, CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    SetStatus(ex.Message);
                    return null;
                }
            }).ConfigureAwait(true);

            if (entered && session is not null)
            {
                _session = session;
            }
        }

        /// <summary>
        /// 合并这件事已经办完，把还留在可应用状态的会话收掉。
        /// <para>
        /// 正常情况下平台在提交成功时已经把会话标成已提交，这里什么也不做。
        /// 只有一种情况需要它：结果是「工作区本来就和合并结果一样」（<see cref="HistoryRestoreStatus.NoChanges"/>），
        /// 那时应用算成功但会话仍停在「全部解决、待应用」。留着它没有意义，
        /// 只会让下次关窗口时问一句莫名其妙的「要放弃吗」。
        /// </para>
        /// </summary>
        public async Task FinishAsync()
        {
            if (NeedsDecision)
            {
                await AbandonAsync().ConfigureAwait(true);
            }
        }

        /// <summary>
        /// 把结果原子应用到工作区：平台会先做保护点、复核工作区没被人动过，再一次性提交。
        /// 返回是否成功 —— 失败原因已经进了状态文案，窗口只管要不要关。
        /// </summary>
        public async Task<bool> ApplyAsync()
        {
            var (entered, result) = await RunBusyAsync<HistoryRestoreResult?>(async () =>
            {
                try
                {
                    return await NativeHistoryApplicationService
                        .ApplyMergeAsync(_config, _session, CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    SetStatus(ex.Message);
                    return null;
                }
            }).ConfigureAwait(true);

            if (!entered || result is null)
            {
                return false;
            }

            // 应用那一步返回的是还原结果不是会话：它可能已经在库里把会话标成了陈旧，
            // 手里这一份就过期了。无论成败都重新读一次，之后的操作才落在正确的修订号上。
            await ReloadSessionAsync().ConfigureAwait(true);

            if (result.Succeeded)
            {
                NotificationService.ShowSuccess(
                    I18n.Format("Merge_ApplySucceeded", TargetBranchName, SourceBranchName),
                    I18n.GetString("Merge_Title.Text"));
                await RefreshConflictsAsync().ConfigureAwait(true);
                return true;
            }

            NotificationService.ShowWarning(result.Diagnostic, I18n.GetString("Merge_ApplyBlockedTitle"));
            await RefreshConflictsAsync().ConfigureAwait(true);
            return false;
        }

        private async Task ReloadSessionAsync()
        {
            try
            {
                if (await NativeHistoryApplicationService
                    .ReloadMergeSessionAsync(_config, _session, CancellationToken.None).ConfigureAwait(true) is { } stored)
                {
                    _session = stored;
                }
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message);
            }
        }

        /// <summary>
        /// 一次解决动作的共同收尾：更新会话、重读列表与进度、重新载入当前冲突的详情。
        /// 返回平台是否收下了这一次解决 —— 调用方要靠它决定「这一份算不算交出去了」。
        /// </summary>
        private async Task<bool> RunResolveAsync(Func<Task<MergeSession>> resolve)
        {
            var (entered, session) = await RunBusyAsync<MergeSession?>(async () =>
            {
                try
                {
                    return await resolve().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    SetStatus(ex.Message);
                    return null;
                }
            }).ConfigureAwait(true);

            if (!entered || session is null)
            {
                return false;
            }

            _session = session;
            await RefreshConflictsAsync().ConfigureAwait(true);
            await LoadSelectedAsync().ConfigureAwait(true);
            return true;
        }

        /// <summary>给选中的冲突里某一块定下取哪边。逐块按钮最终都落到这里。</summary>
        public void Choose(MergeHunkItem? item, LineMergeChoice choice)
        {
            if (item is null || !item.CanChoose || SelectedConflict is not { } conflict)
            {
                return;
            }

            item.Choice = choice;
            _hunkChoices[$"{conflict.ConflictId}#{item.Index}"] = choice;
            if (!IsHandEdited)
            {
                ResultText = ComposeResult();
            }

            OnPropertyChanged(nameof(CanSubmitManual));
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(HasUnsubmittedChanges));
        }

        private void BuildHunks(string conflictId, MergeTextConflictView text)
        {
            Hunks.Clear();
            if (text.Documents is not { } documents)
            {
                return;
            }

            var index = 0;
            foreach (var hunk in documents.Outcome.Hunks)
            {
                var item = new MergeHunkItem(index, hunk, CollapseThreshold)
                {
                    ShowBase = _showBase
                };
                if (_hunkChoices.TryGetValue($"{conflictId}#{index}", out var choice))
                {
                    item.Choice = choice;
                }

                Hunks.Add(item);
                index++;
            }

            UpdateHunkAvailability();
        }

        private void UpdateHunkAvailability()
        {
            var canChoose = IsEditable && !_isHandEdited && !_isBusy;
            foreach (var hunk in Hunks)
            {
                hunk.CanChoose = canChoose;
            }

            OnPropertyChanged(nameof(HasUnsubmittedChanges));
            OnPropertyChanged(nameof(CanApply));
        }

        private void SetEditState(bool handEdited, string text)
        {
            ResultText = text;
            IsHandEdited = handEdited;

            // 这两处派生属性还随「内容与交出去的那一份是否相同」变化，而那件事不翻转 IsHandEdited，
            // 所以每次改编辑状态都显式报一次，不指望 IsHandEdited 的 setter 替它发通知。
            OnPropertyChanged(nameof(HasUnsubmittedChanges));
            OnPropertyChanged(nameof(CanApply));
        }

        /// <summary>按逐块选择拼出整份结果。未选的冲突块按「取本地」预览（提交前另有门禁，见 <see cref="CanSubmitManual"/>）。</summary>
        private string ComposeResult()
        {
            if (_text?.Documents is not { } documents)
            {
                return string.Empty;
            }

            var lines = Hunks.SelectMany(hunk => hunk.ResolvedLines());
            return string.Join(documents.Ours.Newline, lines);
        }

        private static string RefusalKey(TextMergeRefusalKind kind) => kind switch
        {
            TextMergeRefusalKind.NotWhitelisted => "Merge_Refusal_NotWhitelisted",
            TextMergeRefusalKind.Missing => "Merge_Refusal_Missing",
            TextMergeRefusalKind.TooLarge => "Merge_Refusal_TooLarge",
            TextMergeRefusalKind.Undecodable => "Merge_Refusal_Undecodable",
            TextMergeRefusalKind.Binary => "Merge_Refusal_Binary",
            _ => "Merge_Refusal_UnresolvedConflict"
        };

        private void SetStatus(string? message)
        {
            StatusMessage = message ?? string.Empty;
        }
    }
}

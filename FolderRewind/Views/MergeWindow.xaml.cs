using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace FolderRewind.Views
{
    /// <summary>
    /// 合并窗口：逐块解决一次分支合并留下的冲突。
    /// <para>
    /// 窗口只管「展示 + 把用户的意思转给视图模型」；冲突判定、解决与落地都在平台层。
    /// 唯一需要在这里做的事是那个<b>文本回声</b>：视图模型把结果推给文本框会引发一次 TextChanged，
    /// 那不是用户改的，得认出来，否则程序化刷新会被记成「已手改」。
    /// </para>
    /// </summary>
    public sealed partial class MergeWindow : Window
    {
        /// <summary>
        /// 最近一次由程序写进文本框的内容。判断「这次变更是不是回声」靠比对内容，不靠时间窗 ——
        /// 文本框内部走文本服务，变更通知<b>不保证在赋值返回之前到达</b>，
        /// 用「赋值期间立起、赋值返回就放下」的开关挡不住迟到的通知，那一次刷新会被当成手改。
        /// </summary>
        private string? _pushedText;

        private bool _closeConfirmed;
        private bool _isClosing;

        public MergeWindow(BackupConfig config, MergeSession session)
        {
            ViewModel = new MergeWindowViewModel(config, session);

            InitializeComponent();

            // 带转换器的几处绑定用的是经典 {Binding}，因此需要 DataContext。
            // 原因是窗口为根的 XAML 里 x:Bind 用不了 Converter：生成代码是 SetConverterLookupRoot(this)，
            // 形参要求 FrameworkElement，而 Window 不是，编译期就直接失败。
            // 经典 {Binding} 由运行期解析、走元素自己的资源域，不受这条限制。
            // 模板内的 DataContext 会被列表项覆盖，所以这一行只管窗口层的那几处。
            RootGrid.DataContext = ViewModel;

            WindowChromeHelper.ApplySystemTitleBar(this, TitleBarDragRegion);
            ThemeService.ApplyThemeToWindow(this);
            ThemeService.ApplyPersonalizationToWindow(this);
            _ = WindowIconHelper.TryApplyAsync(this);

            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            Closed += OnClosed;
            if (AppWindow is { } appWindow)
            {
                appWindow.Closing += OnClosing;
            }

            _ = InitializeAsync();
        }

        public MergeWindowViewModel ViewModel { get; }

        private async Task InitializeAsync()
        {
            await ViewModel.InitializeAsync();
            PushResultText();
        }

        /// <summary>
        /// 视图模型换了结果文本就把它推进文本框。推进的那一次变更来源是程序而不是用户，
        /// 因此把它记在 <see cref="_pushedText"/> 上，等 TextChanged 真到的时候认出来是回声。
        /// </summary>
        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MergeWindowViewModel.ResultText))
            {
                PushResultText();
            }
        }

        private void PushResultText()
        {
            var text = ViewModel.ResultText;
            if (string.Equals(ResultTextBox.Text, text, StringComparison.Ordinal))
            {
                return;
            }

            // 先记后写：通知若是同步到达，也要认得出这是回声。
            _pushedText = text;
            ResultTextBox.Text = text;
        }

        private void OnResultTextChanged(object sender, TextChangedEventArgs e)
        {
            var text = ResultTextBox.Text;
            if (string.Equals(text, _pushedText, StringComparison.Ordinal))
            {
                return;
            }

            // 也记一份：下一次内容相同的通知仍是「已经同步过的那一份」，不是新的改动。
            // 用户改到的内容恰好等于这一份的情形不存在可登记的差异，忽略是对的。
            _pushedText = text;
            ViewModel.MarkResultEdited(text);
        }

        private async void OnConflictSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0)
            {
                return;
            }

            // 选中项由绑定写回视图模型，这里只负责载入详情；重复的载入由视图模型的令牌收掉。
            await ViewModel.LoadSelectedAsync();
            PushResultText();
        }

        private async void OnPreviousClick(object sender, RoutedEventArgs e)
        {
            if (await ViewModel.MoveSelectionAsync(-1))
            {
                PushResultText();
            }
        }

        private async void OnNextClick(object sender, RoutedEventArgs e)
        {
            if (await ViewModel.MoveSelectionAsync(1))
            {
                PushResultText();
            }
        }

        private void OnToggleHunkClick(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is MergeHunkItem hunk)
            {
                hunk.ToggleExpanded();
            }
        }

        private void OnAcceptOursClick(object sender, RoutedEventArgs e)
            => Choose(sender, LineMergeChoice.Ours);

        private void OnAcceptTheirsClick(object sender, RoutedEventArgs e)
            => Choose(sender, LineMergeChoice.Theirs);

        private void OnAcceptBothClick(object sender, RoutedEventArgs e)
            => Choose(sender, LineMergeChoice.Both);

        /// <summary>逐块按钮在数据模板里，够不到视图模型，所以把它自己（Tag）带过来。</summary>
        private void Choose(object sender, LineMergeChoice choice)
        {
            if ((sender as FrameworkElement)?.Tag is MergeHunkItem hunk)
            {
                ViewModel.Choose(hunk, choice);
            }
        }

        private async void OnTakeOursClick(object sender, RoutedEventArgs e)
        {
            await ViewModel.ResolveWholeSideAsync(MergeResolutionChoice.Ours);
            PushResultText();
        }

        private async void OnTakeTheirsClick(object sender, RoutedEventArgs e)
        {
            await ViewModel.ResolveWholeSideAsync(MergeResolutionChoice.Theirs);
            PushResultText();
        }

        private async void OnSaveManualClick(object sender, RoutedEventArgs e)
        {
            await ViewModel.SubmitManualAsync();
            PushResultText();
        }

        private void OnResetEditClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ResetResultEdit();
            PushResultText();
        }

        private async void OnRecomputeClick(object sender, RoutedEventArgs e)
        {
            await ViewModel.RecomputeAsync();
            PushResultText();
        }

        private async void OnAbandonClick(object sender, RoutedEventArgs e)
        {
            if (!await ConfirmAsync(
                    I18n.GetString("Merge_AbandonConfirmTitle"),
                    I18n.GetString("Merge_AbandonConfirmContent"),
                    I18n.GetString("Merge_AbandonConfirmPrimary")))
            {
                return;
            }

            await ViewModel.AbandonAsync();
            Close();
        }

        private async void OnApplyClick(object sender, RoutedEventArgs e)
        {
            if (await ViewModel.ApplyAsync())
            {
                // 成功了就把会话收掉再关：应用成功但会话还留着唯一的情形是「本来就一样」，
                // 不收掉的话关窗口时会问一句「要放弃吗」，而用户刚刚才看到「合并成功」。
                await ViewModel.FinishAsync();
                _closeConfirmed = true;
                Close();
            }
        }

        /// <summary>
        /// 关窗口前把话说清楚：会话还在半途时关掉等于放弃，不会有第二次机会。
        /// <para>
        /// 这里先把关闭取消掉，问完再自己关 —— <see cref="AppWindowClosingEventArgs"/> 是同步的，
        /// 而对话框是异步的。已确认过一次就不再问第二次（<see cref="_closeConfirmed"/>）。
        /// </para>
        /// </summary>
        private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_closeConfirmed || !ViewModel.NeedsDecision || _isClosing)
            {
                return;
            }

            args.Cancel = true;
            _isClosing = true;
            try
            {
                if (!await ConfirmAsync(
                        I18n.GetString("Merge_CloseConfirmTitle"),
                        I18n.GetString("Merge_CloseConfirmContent"),
                        I18n.GetString("Merge_CloseConfirmPrimary")))
                {
                    return;
                }

                await ViewModel.AbandonAsync();
            }
            catch (Exception ex)
            {
                // 问不出来也不能把窗口卡死：如实记一笔，会话留在库里由用户从「放弃会话」再处理。
                LogService.LogWarning($"[MergeWindow] Close confirmation failed: {ex.Message}", nameof(MergeWindow));
            }
            finally
            {
                _isClosing = false;
            }

            _closeConfirmed = true;
            Close();
        }

        private void OnClosed(object sender, WindowEventArgs args)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            if (AppWindow is { } appWindow)
            {
                appWindow.Closing -= OnClosing;
            }
        }

        private async Task<bool> ConfirmAsync(string title, string content, string primaryText)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = primaryText,
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
    }
}

using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using WinRT.Interop;

namespace FolderRewind.Views
{
    public sealed partial class MiniWindow : Window
    {
        public double MiniCardSizeDip => MiniWindowMetrics.CardSizeDip;
        public double CommentCardWidthDip => MiniWindowMetrics.CommentCardWidthDip;
        public CornerRadius MiniCornerRadius => new(MiniWindowMetrics.CornerRadiusDip);

        private readonly MiniWindowContext _context;
        private MiniWindowVisualState _visualState = MiniWindowVisualState.Normal;
        private MiniWindowExpansionState _expansionState = MiniWindowExpansionState.Collapsed;
        private MiniExpandDirection _activeExpandDirection = MiniExpandDirection.Right;
        private DispatcherTimer? _watchTimer;
        private CancellationTokenSource? _transitionCts;
        private Action<ElementTheme>? _themeChangedHandler;
        private bool _isDragging = false;
        private bool _isPointerCaptured = false;
        private bool _suppressNextTap = false;
        private POINT _dragStartCursorPos;
        private PointInt32 _windowStartPos;

        // Win32 Interop

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT pt);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass);

        private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        private SUBCLASSPROC? _subclassDelegate;
        private const uint WM_GETMINMAXINFO = 0x0024;
        private const uint WM_DPICHANGED = 0x02E0;
        private const uint WM_DESTROY = 0x0002;

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_NCRENDERING_POLICY = 2;
        private const int DWMNCRP_DISABLED = 2;
        private const int DWMWCP_ROUND = 2;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOOWNERZORDER = 0x0200;
        private const uint SWP_NOSIZE = 0x0001;

        // 尺寸常量

        private const int PanelColumnWidth = 228;

        private enum MiniWindowExpansionState
        {
            Collapsed,
            Expanding,
            Expanded,
            Collapsing,
        }

        public MiniWindow(MiniWindowContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            this.InitializeComponent();

            RootGrid.Loaded += RootGrid_Loaded;

            ConfigureWindow();
            SetupUI();
            SetupTransitions();
            StartWatchTimer();
            ApplyLocalizedStrings();

            this.Closed += MiniWindow_Closed;
        }

        // 窗口配置

        /// <summary>
        /// 配置窗口：无标题栏、置顶、小尺寸、圆角、隐藏任务栏图标
        /// </summary>
        private void ConfigureWindow()
        {
            ExtendsContentIntoTitleBar = false;

            // Mini 窗口不使用系统 Backdrop（如 Mica/Acrylic），避免顶部出现半透明材质层
            try
            {
                SystemBackdrop = null;
                SetTitleBar(null);
            }
            catch { }

            var appWindow = this.AppWindow;
            if (appWindow == null) return;

            // 使用 OverlappedPresenter 以允许更小的尺寸并自定义外观
            if (appWindow.Presenter is OverlappedPresenter overlapped)
            {
                overlapped.IsAlwaysOnTop = true;
                overlapped.IsResizable = false;
                overlapped.IsMaximizable = false;
                overlapped.IsMinimizable = false;
                overlapped.SetBorderAndTitleBar(false, false);
            }

            // 挂载子类化以处理 WM_GETMINMAXINFO，允许窗口尺寸小于系统默认最小值
            var hwnd = WindowNative.GetWindowHandle(this);
            _subclassDelegate = new SUBCLASSPROC(WindowSubclassProc);
            SetWindowSubclass(hwnd, _subclassDelegate, 1, IntPtr.Zero);

            // 设置初始尺寸
            CollapseToSquare(false);

            appWindow.Title = $"Mini - {_context.Folder?.DisplayName ?? "Folder"}";

            // Win11 圆角
            try
            {
                int preference = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch { }

            // 移除 DWM 1px 边框（Win11 22H2+，低版本自动忽略）
            try
            {
                int colorNone = unchecked((int)0xFFFFFFFE);
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorNone, sizeof(int));
            }
            catch { }

            // 取消窗口阴影
            try
            {
                int policy = DWMNCRP_DISABLED;
                DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));
            }
            catch { }


            // 从任务栏隐藏（WS_EX_TOOLWINDOW）
            try
            {
                var exStyle = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
                exStyle = (exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));
            }
            catch { }

            // 应用主题
            try
            {
                ThemeService.ApplyThemeToWindow(this);
                _themeChangedHandler = OnThemeChanged;
                ThemeService.ThemeChanged += _themeChangedHandler;
            }
            catch { }

            // 折叠标题栏
            try
            {
                appWindow.TitleBar.ExtendsContentIntoTitleBar = false;
                appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;

                // 保持标题栏相关背景全透明，避免系统残留绘制
                appWindow.TitleBar.BackgroundColor = Microsoft.UI.Colors.Transparent;
                appWindow.TitleBar.InactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

                appWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                appWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                appWindow.TitleBar.ButtonHoverBackgroundColor = Microsoft.UI.Colors.Transparent;
                appWindow.TitleBar.ButtonPressedBackgroundColor = Microsoft.UI.Colors.Transparent;
            }
            catch { }

        }

        private IntPtr WindowSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
        {
            if (uMsg == WM_GETMINMAXINFO)
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                mmi.ptMinTrackSize.X = 10; // 允许极小宽度
                mmi.ptMinTrackSize.Y = 10; // 允许极小高度
                Marshal.StructureToPtr(mmi, lParam, false);
                return IntPtr.Zero;
            }
            else if (uMsg == WM_DPICHANGED)
            {
                ApplyDpiChangedBounds(hWnd, wParam, lParam);
                return IntPtr.Zero;
            }
            else if (uMsg == WM_DESTROY && _subclassDelegate != null)
            {
                RemoveWindowSubclass(hWnd, _subclassDelegate, (uint)uIdSubclass);
            }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private void SetupUI()
        {
            UpdateTooltip();
        }

        private void RootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            RootGrid.Loaded -= RootGrid_Loaded;
            if (IsWindowExpanded)
                ResizeToExpanded();
            else
                CollapseToSquare(false);
        }

        /// <summary>
        /// 设置隐式动画过渡，为展开收起与悬停提供流畅效果
        /// </summary>
        private void SetupTransitions()
        {
            // 输入面板：淡入淡出 + 平移
            CommentPanel.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(160) };
            CommentPanel.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) };

            MiniSquare.CenterPoint = new Vector3(
                (float)(MiniWindowMetrics.CardSizeDip / 2d),
                (float)(MiniWindowMetrics.CardSizeDip / 2d),
                0);
            MiniSquare.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(90) };
            HoverOverlay.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) };
        }

        private void ApplyLocalizedStrings()
        {
            try
            {
                var placeholder = I18n.GetString("MiniWindow_CommentPlaceholder");
                if (!string.IsNullOrWhiteSpace(placeholder) && placeholder != "MiniWindow_CommentPlaceholder")
                {
                    CommentBox.PlaceholderText = placeholder;
                }

                MenuItemOpenFolder.Text = I18n.GetString("MiniWindow_Menu_OpenFolder");
                MenuItemBackup.Text = I18n.GetString("MiniWindow_Menu_Backup");
                MenuItemClose.Text = I18n.GetString("MiniWindow_Menu_Close");

                var expandDir = _context.ExpandDirection;
                MenuItemExpandLeft.Text = expandDir == MiniExpandDirection.Right
                    ? I18n.GetString("MiniWindow_Menu_ExpandLeft")
                    : I18n.GetString("MiniWindow_Menu_ExpandRight");
            }
            catch { }
        }

        private void UpdateTooltip()
        {
            var folder = _context.Folder;
            if (folder == null) return;

            var status = _visualState switch
            {
                MiniWindowVisualState.Changed => I18n.GetString("MiniWindow_Tip_Changed"),
                MiniWindowVisualState.BackingUp => I18n.GetString("MiniWindow_Tip_BackingUp"),
                MiniWindowVisualState.BackupDone => I18n.GetString("MiniWindow_Tip_Done"),
                MiniWindowVisualState.BackupFailed => I18n.GetString("MiniWindow_Tip_Failed"),
                _ => I18n.GetString("MiniWindow_Tip_Normal"),
            };

            // Tooltip 整体格式必须可本地化（不同语言的顺序/标点可能不同）
            MiniTooltip.Content = I18n.Format(
                "MiniWindow_Tip_Format",
                folder.DisplayName,
                folder.Path,
                folder.LastBackupTime,
                status);
        }

        // 变更检测定时器

        private void StartWatchTimer()
        {
            _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _watchTimer.Tick += WatchTimer_Tick;
            _watchTimer.Start();
        }

        private void WatchTimer_Tick(object? sender, object e)
        {
            if (_visualState == MiniWindowVisualState.BackingUp) return;

            var folderPath = _context.Folder?.Path;
            if (string.IsNullOrWhiteSpace(folderPath)) return;

            bool hasChanges = FolderWatcherService.HasChanges(folderPath);

            if (hasChanges && _visualState != MiniWindowVisualState.Changed)
            {
                SetVisualState(MiniWindowVisualState.Changed);
            }
            else if (!hasChanges && _visualState == MiniWindowVisualState.Changed)
            {
                SetVisualState(MiniWindowVisualState.Normal);
            }
        }

        // 视觉状态管理

        private void SetVisualState(MiniWindowVisualState state)
        {
            _visualState = state;

            var ribbonBrush = state switch
            {
                MiniWindowVisualState.Normal => GetThemeBrush("AccentFillColorDefaultBrush", new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue)),
                MiniWindowVisualState.Changed => GetThemeBrush("SystemFillColorCautionBrush", GetThemeBrush("AccentFillColorSecondaryBrush", GetThemeBrush("AccentFillColorDefaultBrush", new SolidColorBrush(Microsoft.UI.Colors.Orange)))),
                MiniWindowVisualState.BackingUp => GetThemeBrush("AccentFillColorSecondaryBrush", GetThemeBrush("AccentFillColorDefaultBrush", new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue))),
                MiniWindowVisualState.BackupDone => GetThemeBrush("SystemFillColorSuccessBrush", new SolidColorBrush(Microsoft.UI.Colors.LimeGreen)),
                MiniWindowVisualState.BackupFailed => GetThemeBrush("SystemFillColorCriticalBrush", new SolidColorBrush(Microsoft.UI.Colors.Crimson)),
                _ => GetThemeBrush("AccentFillColorDefaultBrush", new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue)),
            };

            RibbonBorder.BorderBrush = ribbonBrush;

            // 仅在备份/完成/失败状态显示中心图标
            BackupProgressRing.IsActive = state == MiniWindowVisualState.BackingUp;
            BackupProgressRing.Visibility = state == MiniWindowVisualState.BackingUp
                ? Visibility.Visible : Visibility.Collapsed;

            DoneIcon.Visibility = state == MiniWindowVisualState.BackupDone
                ? Visibility.Visible : Visibility.Collapsed;

            FailIcon.Visibility = state == MiniWindowVisualState.BackupFailed
                ? Visibility.Visible : Visibility.Collapsed;

            UpdateTooltip();
        }

        private Brush GetThemeBrush(string key, Brush fallback)
        {
            try
            {
                if (Microsoft.UI.Xaml.Application.Current?.Resources?.TryGetValue(key, out var value) == true && value is Brush brush)
                    return brush;
            }
            catch { }
            return fallback;
        }

        // 点击展开/收起输入框

        private void MiniSquare_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_isDragging || _suppressNextTap)
            {
                _suppressNextTap = false;
                return;
            }
            ToggleInputPanel();
        }

        private void ToggleInputPanel()
        {
            var shouldExpand = _expansionState is MiniWindowExpansionState.Collapsed
                or MiniWindowExpansionState.Collapsing;
            _ = SetInputPanelExpandedAsync(shouldExpand);
        }

        private void CollapseInputPanel()
        {
            _ = SetInputPanelExpandedAsync(false);
        }

        private async Task SetInputPanelExpandedAsync(bool expand)
        {
            if (expand && _expansionState is MiniWindowExpansionState.Expanding or MiniWindowExpansionState.Expanded)
                return;
            if (!expand && _expansionState == MiniWindowExpansionState.Collapsed)
                return;

            _transitionCts?.Cancel();
            _transitionCts?.Dispose();
            var transitionCts = new CancellationTokenSource();
            _transitionCts = transitionCts;
            var token = transitionCts.Token;

            try
            {
                if (expand)
                {
                    var anchor = GetCurrentAnchorPoint();
                    _expansionState = MiniWindowExpansionState.Expanding;
                    _activeExpandDirection = _context.ExpandDirection;
                    ResizeToExpanded(anchor);

                    var isLeft = _activeExpandDirection == MiniExpandDirection.Left;
                    CommentPanel.Opacity = 0;
                    CommentPanel.Translation = isLeft ? new Vector3(12, 0, 0) : new Vector3(-12, 0, 0);
                    CommentPanel.Visibility = Visibility.Visible;

                    await Task.Yield();
                    token.ThrowIfCancellationRequested();
                    CommentPanel.Opacity = 1;
                    CommentPanel.Translation = Vector3.Zero;
                    _expansionState = MiniWindowExpansionState.Expanded;
                    CommentBox.Focus(FocusState.Programmatic);
                }
                else
                {
                    _expansionState = MiniWindowExpansionState.Collapsing;
                    var wasLeftExpanded = _activeExpandDirection == MiniExpandDirection.Left;
                    CommentPanel.Opacity = 0;
                    CommentPanel.Translation = wasLeftExpanded
                        ? new Vector3(12, 0, 0)
                        : new Vector3(-12, 0, 0);
                    CommentBox.Text = "";

                    await Task.Delay(180, token);
                    token.ThrowIfCancellationRequested();
                    CommentPanel.Visibility = Visibility.Collapsed;
                    LeftExpandColumn.Width = new GridLength(0);
                    RightExpandColumn.Width = new GridLength(0);
                    CollapseToSquare(wasLeftExpanded);
                    _expansionState = MiniWindowExpansionState.Collapsed;
                }
            }
            catch (OperationCanceledException)
            {
                // A newer transition owns the final visual and window state.
            }
        }

        private void ConfigureCommentPanelLayout(MiniExpandDirection direction)
        {
            var isLeft = direction == MiniExpandDirection.Left;
            Grid.SetColumn(CommentPanel, isLeft ? 0 : 2);
            CommentPanel.Margin = isLeft
                ? new Thickness(0, 0, MiniWindowMetrics.CardGapDip, 0)
                : new Thickness(MiniWindowMetrics.CardGapDip, 0, 0, 0);
            LeftExpandColumn.Width = new GridLength(isLeft ? PanelColumnWidth : 0);
            RightExpandColumn.Width = new GridLength(isLeft ? 0 : PanelColumnWidth);
        }

        // 窗口尺寸管理

        private void ResizeToExpanded(MiniWindowPixelPoint? requestedAnchor = null)
        {
            try
            {
                var scale = GetScaleFactor();
                var hwnd = WindowNative.GetWindowHandle(this);
                var anchor = requestedAnchor ?? GetCurrentAnchorPoint();
                var preferredDirection = _context.ExpandDirection == MiniExpandDirection.Left
                    ? MiniWindowLayoutDirection.Left
                    : MiniWindowLayoutDirection.Right;
                var layout = MiniWindowLayoutPolicy.GetExpandedBounds(
                    anchor,
                    GetCurrentWorkArea(),
                    scale,
                    preferredDirection);

                _activeExpandDirection = layout.Direction == MiniWindowLayoutDirection.Left
                    ? MiniExpandDirection.Left
                    : MiniExpandDirection.Right;
                ConfigureCommentPanelLayout(_activeExpandDirection);

                SetWindowPos(hwnd, IntPtr.Zero,
                    layout.WindowBounds.X,
                    layout.WindowBounds.Y,
                    layout.WindowBounds.Width,
                    layout.WindowBounds.Height,
                    SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER);
            }
            catch { }
        }

        /// <summary>
        /// 收起时原子化恢复窗口为方块尺寸，确保方块视觉位置不跳动
        /// </summary>
        private void CollapseToSquare(bool wasLeftExpanded)
        {
            try
            {
                var scale = GetScaleFactor();
                var hwnd = WindowNative.GetWindowHandle(this);
                var anchor = GetCurrentAnchorPoint(wasLeftExpanded);
                var bounds = MiniWindowLayoutPolicy.ClampCollapsedBounds(
                    anchor,
                    GetCurrentWorkArea(),
                    scale);

                SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER);
            }
            catch { }
        }

        private MiniWindowPixelPoint GetCurrentAnchorPoint(bool? leftExpandedOverride = null)
        {
            var position = AppWindow.Position;
            var isLeftExpanded = leftExpandedOverride
                ?? (IsWindowExpanded && _activeExpandDirection == MiniExpandDirection.Left);
            if (!isLeftExpanded)
                return new MiniWindowPixelPoint(position.X, position.Y);

            var scale = GetScaleFactor();
            var cardSize = MiniWindowLayoutPolicy.DipToPixels(MiniWindowMetrics.CardSizeDip, scale);
            var expandedWidth = MiniWindowLayoutPolicy.DipToPixels(MiniWindowMetrics.ExpandedWidthDip, scale);
            return new MiniWindowPixelPoint(position.X + expandedWidth - cardSize, position.Y);
        }

        private MiniWindowPixelRect GetCurrentWorkArea()
        {
            var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest)
                ?? DisplayArea.Primary;
            var workArea = displayArea.WorkArea;
            return new MiniWindowPixelRect(workArea.X, workArea.Y, workArea.Width, workArea.Height);
        }

        private void ReflowIntoCurrentWorkArea()
        {
            if (IsWindowExpanded)
            {
                ResizeToExpanded();
                return;
            }

            CollapseToSquare(false);
        }

        private double GetScaleFactor()
        {
            try
            {
                var hwnd = WindowNative.GetWindowHandle(this);
                var dpi = GetDpiForWindow(hwnd);
                return dpi > 0 ? dpi / 96d : 1d;
            }
            catch { return 1.0; }
        }

        private void ApplyDpiChangedBounds(IntPtr hwnd, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                var dpi = unchecked((uint)wParam.ToInt64()) & 0xFFFF;
                var scale = dpi > 0 ? dpi / 96d : GetScaleFactor();
                var suggested = Marshal.PtrToStructure<RECT>(lParam);
                var size = MiniWindowLayoutPolicy.DipToPixels(MiniWindowMetrics.CardSizeDip, scale);
                var width = IsWindowExpanded
                    ? MiniWindowLayoutPolicy.DipToPixels(MiniWindowMetrics.ExpandedWidthDip, scale)
                    : size;

                SetWindowPos(hwnd, IntPtr.Zero, suggested.Left, suggested.Top, width, size,
                    SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER);
                if (_isPointerCaptured)
                {
                    GetCursorPos(out _dragStartCursorPos);
                    _windowStartPos = new PointInt32(suggested.Left, suggested.Top);
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_isDragging)
                        ReflowIntoCurrentWorkArea();
                });
            }
            catch { }
        }

        // 输入框事件

        private void CommentBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                var comment = (sender as TextBox)?.Text?.Trim() ?? "";
                _ = ExecuteBackupAsync(comment);
                CollapseInputPanel();
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                CollapseInputPanel();
            }
        }

        private void CommentBox_LostFocus(object sender, RoutedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_expansionState is MiniWindowExpansionState.Collapsed or MiniWindowExpansionState.Collapsing) return;
                var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot);
                if (!ReferenceEquals(focused, CommentBox))
                {
                    CollapseInputPanel();
                }
            });
        }

        // 备份执行

        private async Task ExecuteBackupAsync(string comment)
        {
            if (_context.Config == null || _context.Folder == null) return;
            if (_visualState == MiniWindowVisualState.BackingUp) return;

            SetVisualState(MiniWindowVisualState.BackingUp);

            try
            {
                string backupComment = string.IsNullOrWhiteSpace(comment)
                    ? "[Mini]"
                    : $"{comment} [Mini]";

                await BackupService.BackupFolderAsync(
                    _context.Config,
                    _context.Folder,
                    backupComment,
                    invocationOptions: BackupInvocationOptions.ForManual());
                FolderWatcherService.ResetChanges(_context.Folder.Path);

                SetVisualState(MiniWindowVisualState.BackupDone);
                await Task.Delay(2000);
                if (_visualState == MiniWindowVisualState.BackupDone)
                    SetVisualState(MiniWindowVisualState.Normal);
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("MiniWindow_Log_BackupFailed", ex.Message), nameof(MiniWindow), ex);
                SetVisualState(MiniWindowVisualState.BackupFailed);

                await Task.Delay(3000);
                if (_visualState == MiniWindowVisualState.BackupFailed)
                    SetVisualState(MiniWindowVisualState.Normal);
            }
        }

        /// <summary>
        /// 由 MiniWindowService 从热键触发调用
        /// </summary>
        public void TriggerBackupFromHotkey()
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                var hotkeyComment = I18n.GetString("MiniWindow_BackupComment_Hotkey");
                if (string.IsNullOrWhiteSpace(hotkeyComment) || hotkeyComment == "MiniWindow_BackupComment_Hotkey")
                    hotkeyComment = "[Hotkey]";

                await ExecuteBackupAsync(hotkeyComment);
            });
        }

        // 窗口拖拽（使用屏幕坐标消除抖动）

        private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(RootGrid);
            if (!point.Properties.IsLeftButtonPressed) return;

            // 仅在 MiniSquare 区域内允许拖拽
            var squarePoint = e.GetCurrentPoint(MiniSquare);
            if (squarePoint.Position.X < 0 || squarePoint.Position.Y < 0 ||
                squarePoint.Position.X > MiniSquare.ActualWidth || squarePoint.Position.Y > MiniSquare.ActualHeight)
                return;

            _isDragging = false;
            _suppressNextTap = false;
            _isPointerCaptured = RootGrid.CapturePointer(e.Pointer);

            if (_isPointerCaptured)
            {
                // 使用屏幕坐标而非相对坐标，彻底消除拖拽反馈回弹
                GetCursorPos(out _dragStartCursorPos);
                _windowStartPos = AppWindow.Position;
                MiniSquare.Scale = new Vector3(0.97f, 0.97f, 1f);
            }
        }

        private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isPointerCaptured) return;

            // 使用屏幕坐标计算增量，避免窗口移动导致的坐标漂移
            GetCursorPos(out POINT currentCursorPos);
            int deltaX = currentCursorPos.X - _dragStartCursorPos.X;
            int deltaY = currentCursorPos.Y - _dragStartCursorPos.Y;

            // 移动超过阈值才认为是拖拽
            var dragThreshold = MiniWindowLayoutPolicy.DipToPixels(MiniWindowMetrics.DragThresholdDip, GetScaleFactor());
            if (!_isDragging && (Math.Abs(deltaX) > dragThreshold || Math.Abs(deltaY) > dragThreshold))
            {
                _isDragging = true;
            }

            if (_isDragging)
            {
                int newX = _windowStartPos.X + deltaX;
                int newY = _windowStartPos.Y + deltaY;
                
                var hwnd = WindowNative.GetWindowHandle(this);
                SetWindowPos(hwnd, IntPtr.Zero, newX, newY, 0, 0,
                    SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOSIZE);
            }
        }

        private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            var wasDragging = _isDragging;
            if (_isPointerCaptured)
            {
                RootGrid.ReleasePointerCapture(e.Pointer);
                _isPointerCaptured = false;
            }

            MiniSquare.Scale = Vector3.One;

            if (wasDragging)
            {
                ReflowIntoCurrentWorkArea();
                _suppressNextTap = true;
                // Tapped is raised before this queued callback for a completed pointer gesture.
                DispatcherQueue.TryEnqueue(() =>
                {
                    _isDragging = false;
                    _suppressNextTap = false;
                });
            }
        }

        private void RootGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isPointerCaptured = false;
            _isDragging = false;
            MiniSquare.Scale = Vector3.One;
        }

        // 悬停效果
        private void MiniSquare_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            HoverOverlay.Opacity = 1;
        }
        private void MiniSquare_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            HoverOverlay.Opacity = 0;
        }

        // 右键菜单

        private void MiniSquare_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            // ContextFlyout 自动处理
        }

        private void OnContextOpenFolder(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", _context.Folder.Path); }
            catch { }
        }

        private void OnContextBackup(object sender, RoutedEventArgs e)
        {
            _ = ExecuteBackupAsync("");
        }

        private void OnContextToggleExpandDirection(object sender, RoutedEventArgs e)
        {
            if (_expansionState != MiniWindowExpansionState.Collapsed) CollapseInputPanel();

            _context.ExpandDirection = _context.ExpandDirection == MiniExpandDirection.Right
                ? MiniExpandDirection.Left
                : MiniExpandDirection.Right;

            MenuItemExpandLeft.Text = _context.ExpandDirection == MiniExpandDirection.Right
                ? I18n.GetString("MiniWindow_Menu_ExpandLeft")
                : I18n.GetString("MiniWindow_Menu_ExpandRight");

            MenuItemExpandLeft.Icon = new FontIcon
            {
                Glyph = _context.ExpandDirection == MiniExpandDirection.Right ? "\uE76B" : "\uE76C"
            };
        }

        private void OnContextClose(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        // 清理

        private bool IsWindowExpanded => _expansionState != MiniWindowExpansionState.Collapsed;

        private void OnThemeChanged(ElementTheme theme)
        {
            ThemeService.ApplyThemeToWindow(this);
            SetVisualState(_visualState);
        }

        private void MiniWindow_Closed(object sender, WindowEventArgs args)
        {
            _watchTimer?.Stop();
            _watchTimer = null;

            _transitionCts?.Cancel();
            _transitionCts?.Dispose();
            _transitionCts = null;

            if (_themeChangedHandler != null)
            {
                ThemeService.ThemeChanged -= _themeChangedHandler;
                _themeChangedHandler = null;
            }

            if (_subclassDelegate != null)
            {
                try
                {
                    var hwnd = WindowNative.GetWindowHandle(this);
                    RemoveWindowSubclass(hwnd, _subclassDelegate, 1);
                }
                catch { }
                _subclassDelegate = null;
            }
        }
    }
}

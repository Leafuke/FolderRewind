using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics;
using WinRT.Interop;

namespace FolderRewind.Views
{
    public sealed partial class MiniWindow : Window
    {
        private readonly MiniWindowContext _context;
        private MiniWindowVisualState _visualState = MiniWindowVisualState.Normal;
        private DispatcherTimer _watchTimer;
        private bool _isExpanded = false;
        private bool _isDragging = false;
        private bool _isPointerCaptured = false;
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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWCP_ROUND = 2;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOOWNERZORDER = 0x0200;

        // 尺寸常量

        private const int SquareSize = 48;
        private const int PanelColumnWidth = 220;
        private const int GapWidth = 4;
        private const int ExpandedExtraWidth = PanelColumnWidth + GapWidth;

        public MiniWindow(MiniWindowContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            this.InitializeComponent();

            ConfigureWindow();
            SetupUI();
            SetupTransitions();
            StartWatchTimer();
            ApplyLocalizedStrings();

            this.Closed += (_, _) => StopTimers();
        }

        // 窗口配置

        /// <summary>
        /// 配置窗口：无标题栏、置顶、小尺寸、圆角、隐藏任务栏图标
        /// </summary>
        private void ConfigureWindow()
        {
            ExtendsContentIntoTitleBar = true;

            var appWindow = this.AppWindow;
            if (appWindow == null) return;

            // CompactOverlay更符合“观测器/触发器”的迷你悬浮窗定位：默认置顶、占用空间小。
            try
            {
                var presenter = CompactOverlayPresenter.Create();
                presenter.InitialSize = CompactOverlaySize.Small;
                appWindow.SetPresenter(presenter);
            }
            catch
            {
                // 某些系统/运行时条件下可能不支持 CompactOverlay，回退到普通悬浮置顶。
                if (appWindow.Presenter is OverlappedPresenter fallback)
                {
                    fallback.IsAlwaysOnTop = true;
                    fallback.IsResizable = false;
                    fallback.IsMaximizable = false;
                    fallback.IsMinimizable = false;
                    fallback.SetBorderAndTitleBar(false, false);
                }
            }

            // 尽可能移除窗口边框/标题栏（CompactOverlay 下通常无边框；Overlapped 回退时显式关闭）
            if (appWindow.Presenter is OverlappedPresenter overlapped)
            {
                overlapped.IsAlwaysOnTop = true;
                overlapped.IsResizable = false;
                overlapped.IsMaximizable = false;
                overlapped.IsMinimizable = false;
                overlapped.SetBorderAndTitleBar(false, false);

                
            }

            // 设置初始尺寸
            ResizeToCollapsed();
            CollapseToSquare(false);

            appWindow.Title = $"Mini - {_context.Folder?.DisplayName ?? "Folder"}";

            var hwnd = WindowNative.GetWindowHandle(this);

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

            // 取消窗口阴影 - 暂时做不到……
            

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
                ThemeService.ThemeChanged += (_) => ThemeService.ApplyThemeToWindow(this);
            }
            catch { }

            // 折叠标题栏
            try
            {
                appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;

                // 避免出现标题栏按钮残留底色（尤其是部分 presenter/系统组合下）
                appWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                appWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            }
            catch { }

            // 确保内容层背景为透明（移除 SystemBackdrop 后，靠控件自身绘制外观）
            try
            {
                var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                if (Content is Panel panel)
                    panel.Background = transparent;
                else if (Content is Control control)
                    control.Background = transparent;
            }
            catch { }
        }

        private void SetupUI()
        {
            UpdateTooltip();
        }

        /// <summary>
        /// 设置隐式动画过渡，为展开收起与悬停提供流畅效果
        /// </summary>
        private void SetupTransitions()
        {
            // 输入面板：淡入淡出 + 平移
            LeftInputPanel.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(200) };
            RightInputPanel.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(200) };
            LeftInputPanel.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(250) };
            RightInputPanel.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(250) };

            // 丝带环 hover 缩放
            RibbonBorder.CenterPoint = new Vector3((SquareSize - 8) / 2f, (SquareSize - 8) / 2f, 0);
            RibbonBorder.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(150) };
        }

        private void ApplyLocalizedStrings()
        {
            try
            {
                var placeholder = I18n.GetString("MiniWindow_CommentPlaceholder");
                if (!string.IsNullOrWhiteSpace(placeholder) && placeholder != "MiniWindow_CommentPlaceholder")
                {
                    LeftCommentBox.PlaceholderText = placeholder;
                    RightCommentBox.PlaceholderText = placeholder;
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
                folder.FullPath,
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

        private void WatchTimer_Tick(object sender, object e)
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

            // 更新丝带颜色
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
            if (_isDragging) return;
            ToggleInputPanel();
        }

        private void ToggleInputPanel()
        {
            if (_isExpanded)
                CollapseInputPanel();
            else
                ExpandInputPanel();
        }

        private void ExpandInputPanel()
        {
            _isExpanded = true;

            bool isLeft = _context.ExpandDirection == MiniExpandDirection.Left;
            var panel = isLeft ? LeftInputPanel : RightInputPanel;
            var column = isLeft ? LeftExpandColumn : RightExpandColumn;
            var otherPanel = isLeft ? RightInputPanel : LeftInputPanel;
            var otherColumn = isLeft ? RightExpandColumn : LeftExpandColumn;
            var commentBox = isLeft ? LeftCommentBox : RightCommentBox;

            // 设置动画初始状态（面板不可见时设置，不触发可见过渡）
            panel.Opacity = 0;
            panel.Translation = isLeft ? new Vector3(20, 0, 0) : new Vector3(-20, 0, 0);

            // 激活面板布局
            panel.Visibility = Visibility.Visible;
            column.Width = new GridLength(PanelColumnWidth);
            otherPanel.Visibility = Visibility.Collapsed;
            otherColumn.Width = new GridLength(0);

            // 使用 SetWindowPos 原子化 resize + move，防止闪烁
            ResizeToExpanded();

            // 等待一帧布局完成后触发动画
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                panel.Opacity = 1;
                panel.Translation = Vector3.Zero;
                commentBox.Focus(FocusState.Programmatic);
            });
        }

        private async void CollapseInputPanel()
        {
            if (!_isExpanded) return;
            _isExpanded = false;

            bool isLeft = _context.ExpandDirection == MiniExpandDirection.Left;
            var panel = isLeft ? LeftInputPanel : RightInputPanel;

            // 启动退出动画
            panel.Opacity = 0;
            panel.Translation = isLeft ? new Vector3(20, 0, 0) : new Vector3(-20, 0, 0);

            // 清空输入
            LeftCommentBox.Text = "";
            RightCommentBox.Text = "";

            // 等待退出动画完成
            await Task.Delay(220);

            // 清理布局
            LeftInputPanel.Visibility = Visibility.Collapsed;
            RightInputPanel.Visibility = Visibility.Collapsed;
            LeftExpandColumn.Width = new GridLength(0);
            RightExpandColumn.Width = new GridLength(0);

            // 原子化恢复位置和尺寸
            CollapseToSquare(isLeft);
        }

        // 窗口尺寸管理

        private void ResizeToCollapsed()
        {
            try
            {
                var scale = GetScaleFactor();
                int size = (int)(SquareSize * scale);
                AppWindow?.Resize(new SizeInt32(size, size));
            }
            catch { }
        }

        private void ResizeToExpanded()
        {
            try
            {
                var scale = GetScaleFactor();
                int squarePixels = (int)(SquareSize * scale);
                int extraPixels = (int)(ExpandedExtraWidth * scale);
                int totalWidth = squarePixels + extraPixels;
                int height = squarePixels;

                var hwnd = WindowNative.GetWindowHandle(this);
                var pos = AppWindow.Position;

                if (_context.ExpandDirection == MiniExpandDirection.Left)
                {
                    // 向左展开: 原子化移动+resize，方块位置不变
                    int newX = Math.Max(0, pos.X - extraPixels);
                    SetWindowPos(hwnd, IntPtr.Zero, newX, pos.Y, totalWidth, height,
                        SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER);
                }
                else
                {
                    // 向右展开: 仅 resize
                    SetWindowPos(hwnd, IntPtr.Zero, pos.X, pos.Y, totalWidth, height,
                        SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER);
                }
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
                int size = (int)(SquareSize * scale);
                var hwnd = WindowNative.GetWindowHandle(this);
                var pos = AppWindow.Position;

                if (wasLeftExpanded)
                {
                    // 左展开收起：方块在窗口右端，需将窗口右移到方块位置
                    int extraPixels = (int)(ExpandedExtraWidth * scale);
                    SetWindowPos(hwnd, IntPtr.Zero, pos.X + extraPixels, pos.Y, size + 7, size,
                        SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER);
                }
                else
                {
                    // 右展开收起：方块在窗口左端，直接 resize
                    SetWindowPos(hwnd, IntPtr.Zero, pos.X, pos.Y, size + 7, size,
                        SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER);
                }
            }
            catch { }
        }

        private double GetScaleFactor()
        {
            try { return RootGrid?.XamlRoot?.RasterizationScale ?? 1.0; }
            catch { return 1.0; }
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
                if (!_isExpanded) return;
                var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot);
                if (focused is not TextBox tb || (tb != LeftCommentBox && tb != RightCommentBox))
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

                await BackupService.BackupFolderAsync(_context.Config, _context.Folder, backupComment);
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
            _isPointerCaptured = RootGrid.CapturePointer(e.Pointer);

            if (_isPointerCaptured)
            {
                // 使用屏幕坐标而非相对坐标，彻底消除拖拽反馈回弹
                GetCursorPos(out _dragStartCursorPos);
                _windowStartPos = AppWindow.Position;
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
            if (!_isDragging && (Math.Abs(deltaX) > 3 || Math.Abs(deltaY) > 3))
            {
                _isDragging = true;
            }

            if (_isDragging)
            {
                int newX = _windowStartPos.X + deltaX;
                int newY = _windowStartPos.Y + deltaY;
                AppWindow.Move(new PointInt32(newX, newY));
            }
        }

        private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isPointerCaptured)
            {
                RootGrid.ReleasePointerCapture(e.Pointer);
                _isPointerCaptured = false;
            }

            if (_isDragging)
            {
                // 延迟重置，防止 Tapped 误触
                DispatcherQueue.TryEnqueue(() => _isDragging = false);
            }
        }

        private void RootGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isPointerCaptured = false;
            _isDragging = false;
        }

        // 悬停效果
        private void MiniSquare_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            RibbonBorder.Scale = new Vector3(1.08f, 1.08f, 1f);
        }
        private void MiniSquare_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            RibbonBorder.Scale = Vector3.One;
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
            if (_isExpanded) CollapseInputPanel();

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

        private void StopTimers()
        {
            _watchTimer?.Stop();
            _watchTimer = null;
        }
    }
}
using FolderRewind.Services;
using FolderRewind.Services.Hotkeys;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Foundation;
using Windows.UI;

namespace FolderRewind
{
    /// <summary>
    /// 主窗口：承载壳层页面、标题栏和关闭行为控制。
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        #region 常量与状态

        private const double TitleBarHorizontalPadding = 12;
        private const int WindowMinWidth = 1000;
        private const int WindowMinHeight = 600;

        private bool _allowCloseOnce;
        private bool _closeDialogShowing;

        #endregion

        #region 构造与初始化

        public MainWindow()
        {
            InitializeComponent();

            // 等窗口真正激活后再做依赖句柄的初始化（热键/图标等）。
            Activated += MainWindow_Activated;

            // 用 Shell 的 XAML 区域接管标题栏，后面再动态同步左右 inset。
            ExtendsContentIntoTitleBar = true;
            if (ShellRoot?.AppTitleBarElement != null)
            {
                SetTitleBar(ShellRoot.AppTitleBarElement);
            }

            ThemeService.ThemeChanged += ThemeService_ThemeChanged;

            // 先应用当前主题，再刷标题栏按钮色，避免首次显示时色彩闪一下。
            var currentTheme = ThemeService.GetCurrentTheme();
            ThemeService.ApplyThemeToWindow(this);

            InitializeIntegratedTitleBar();
            UpdateTitleBar(currentTheme);

            // 窗口关闭时清理 KnotLink 服务
            Closed += MainWindow_Closed;

            try
            {
                AppWindow.Closing += AppWindow_Closing;
            }
            catch
            {
            }
        }

        #endregion

        #region 窗口激活与最小尺寸

        private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            // 这个事件会多次触发，这里只需要第一次（句柄和可视树已就绪）。
            Activated -= MainWindow_Activated;

            ApplyMinimumWindowSize();

            try
            {
                HotkeyManager.Initialize(this, ShellRoot);
            }
            catch
            {
            }

            await WindowIconHelper.TryApplyAsync(this);
        }

        private void ApplyMinimumWindowSize()
        {
            try
            {
                if (AppWindow?.Presenter is OverlappedPresenter presenter)
                {
                    var scale = ShellRoot?.XamlRoot?.RasterizationScale ?? 1d;
                    presenter.PreferredMinimumWidth = Convert.ToInt32(WindowMinWidth * scale);
                    presenter.PreferredMinimumHeight = Convert.ToInt32(WindowMinHeight * scale);
                }
            }
            catch
            {
            }
        }

        #endregion

        #region 关闭行为控制

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            // 关闭 KnotLink 服务，释放网络资源
            try
            {
                KnotLinkService.Shutdown();
            }
            catch
            {
                // 忽略关闭时的异常
            }
        }

        #endregion

        #region 主题与标题栏

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            // 托盘菜单触发的“退出”会先打标记，直接放行关闭。
            if (App.ForceExitRequested)
            {
                return;
            }

            // 用户在确认弹窗中点“退出”后会再次触发 Closing，这次应放行。
            if (_allowCloseOnce)
            {
                _allowCloseOnce = false;
                return;
            }

            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            if (settings == null)
            {
                return;
            }

            // 已记住选择：直接执行
            if (settings.RememberCloseBehavior)
            {
                if (settings.CloseBehavior == Models.CloseBehavior.MinimizeToTray)
                {
                    args.Cancel = true;
                    HideToTray();
                    return;
                }

                if (settings.CloseBehavior == Models.CloseBehavior.Exit)
                {
                    return; // 放行关闭
                }
            }

            // 未记住：弹窗询问（Closing 不能 await，因此先 Cancel，再异步弹窗）
            if (_closeDialogShowing)
            {
                args.Cancel = true;
                return;
            }

            args.Cancel = true;
            _closeDialogShowing = true;

            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var remember = new CheckBox
                    {
                        Content = I18n.GetString("CloseDialog_Remember"),
                        IsChecked = false,
                        Margin = new Thickness(0, 12, 0, 0)
                    };

                    var content = new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = I18n.GetString("CloseDialog_Content"),
                                TextWrapping = TextWrapping.Wrap
                            },
                            remember
                        }
                    };

                    var dialog = new ContentDialog
                    {
                        Title = I18n.GetString("CloseDialog_Title"),
                        Content = content,
                        PrimaryButtonText = I18n.GetString("CloseDialog_MinimizeToTray"),
                        SecondaryButtonText = I18n.GetString("CloseDialog_Exit"),
                        CloseButtonText = I18n.GetString("Common_Cancel"),
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = (Content as FrameworkElement)?.XamlRoot
                    };

                    var op = dialog.ShowAsync();
                    op.Completed = (info, status) =>
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            try
                            {
                                if (status != AsyncStatus.Completed)
                                {
                                    return;
                                }

                                var result = info.GetResults();

                                if (result == ContentDialogResult.None)
                                {
                                    return;
                                }

                                bool doRemember = remember.IsChecked == true;

                                if (result == ContentDialogResult.Primary)
                                {
                                    if (doRemember)
                                    {
                                        settings.CloseBehavior = Models.CloseBehavior.MinimizeToTray;
                                        settings.RememberCloseBehavior = true;
                                        ConfigService.Save();
                                    }

                                    HideToTray();
                                }
                                else if (result == ContentDialogResult.Secondary)
                                {
                                    if (doRemember)
                                    {
                                        settings.CloseBehavior = Models.CloseBehavior.Exit;
                                        settings.RememberCloseBehavior = true;
                                        ConfigService.Save();
                                    }

                                    // 二次 Close 时绕过本方法的拦截分支。
                                    _allowCloseOnce = true;
                                    Close();
                                }
                            }
                            catch
                            {
                            }
                            finally
                            {
                                _closeDialogShowing = false;
                            }
                        });
                    };
                }
                catch
                {
                    _closeDialogShowing = false;
                }
            });
        }

        internal void HideToTray()
        {
            try
            {
                AppWindow?.Hide();
            }
            catch
            {
            }
        }

        private void ThemeService_ThemeChanged(ElementTheme theme)
        {
            ThemeService.ApplyThemeToWindow(this);
            UpdateTitleBar(theme);
        }

        private void InitializeIntegratedTitleBar()
        {
            var titleBar = AppWindow.TitleBar;
            if (titleBar == null)
            {
                return;
            }

            titleBar.ExtendsContentIntoTitleBar = true;

            // 某些 SDK 组合下拿不到 LayoutMetricsChanged，这里退一步用 SizeChanged 同步 inset。
            SizeChanged += (_, __) => UpdateTitleBarLayout();
            UpdateTitleBarLayout();
        }

        private void UpdateTitleBarLayout()
        {
            if (AppWindow.TitleBar == null)
            {
                return;
            }

            // 预留系统标题栏按钮区域，避免覆盖自定义标题栏内容。
            if (ShellRoot?.AppTitleBarElement != null)
            {
                var left = TitleBarHorizontalPadding + AppWindow.TitleBar.LeftInset;
                var right = TitleBarHorizontalPadding + AppWindow.TitleBar.RightInset;
                ShellRoot.AppTitleBarElement.Padding = new Thickness(left, 0, right, 0);
            }
        }

        private static Color WithAlpha(Color color, byte alpha)
        {
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        private void UpdateTitleBar(ElementTheme theme)
        {
            if (AppWindow.TitleBar != null)
            {
                var titleBar = AppWindow.TitleBar;

                // 背景保持透明，让 Mica 透出来，只调整前景与按钮态颜色。
                bool isDark = theme == ElementTheme.Dark;

                // 显式指定亮/暗主题配色，保证 Win10/Win11 下表现一致。
                var foreground = isDark ? Colors.White : Colors.Black;
                var inactiveForeground = isDark ? Color.FromArgb(255, 190, 190, 190) : Color.FromArgb(255, 80, 80, 80);

                // 悬停/按下采用轻量叠色，避免破坏 Mica 的通透感。
                var hoverBackground = isDark
                    ? WithAlpha(Colors.White, 26)
                    : WithAlpha(Colors.Black, 18);
                var pressedBackground = isDark
                    ? WithAlpha(Colors.White, 44)
                    : WithAlpha(Colors.Black, 34);

                titleBar.BackgroundColor = Colors.Transparent;
                titleBar.ForegroundColor = foreground;
                titleBar.InactiveBackgroundColor = Colors.Transparent;
                titleBar.InactiveForegroundColor = inactiveForeground;

                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonForegroundColor = foreground;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveForegroundColor = inactiveForeground;

                titleBar.ButtonHoverBackgroundColor = hoverBackground;
                titleBar.ButtonHoverForegroundColor = foreground;
                titleBar.ButtonPressedBackgroundColor = pressedBackground;
                titleBar.ButtonPressedForegroundColor = foreground;
            }
        }

        #endregion
    }
}

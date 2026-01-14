using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FolderRewind.Services;
using FolderRewind.Models;
using FolderRewind.Services.Hotkeys;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Foundation;
using Windows.UI;
using System;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FolderRewind
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private const double TitleBarHorizontalPadding = 12;

        private bool _allowCloseOnce;
        private bool _closeDialogShowing;

        public MainWindow()
        {
            InitializeComponent();

            // 状态栏图标
            Activated += MainWindow_Activated;

            // WinUI 3 Gallery-like: extend content into title bar and provide a XAML title bar.
            ExtendsContentIntoTitleBar = true;
            if (ShellRoot?.AppTitleBarElement != null)
            {
                SetTitleBar(ShellRoot.AppTitleBarElement);
            }

            ThemeService.ThemeChanged += ThemeService_ThemeChanged;
            
            // Apply initial theme
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

        private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            // Apply once after the window handle is ready.
            Activated -= MainWindow_Activated;

            try
            {
                HotkeyManager.Initialize(this, ShellRoot);
            }
            catch
            {
            }

            await WindowIconHelper.TryApplyAsync(this);
        }

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

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (App.ForceExitRequested)
            {
                return;
            }

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
                    return; // allow close
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

        private void HideToTray()
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

            // AppWindowTitleBar doesn't expose LayoutMetricsChanged in all SDK shapes;
            // update insets opportunistically when the window size changes.
            SizeChanged += (_, __) => UpdateTitleBarLayout();
            UpdateTitleBarLayout();
        }

        private void UpdateTitleBarLayout()
        {
            if (AppWindow.TitleBar == null)
            {
                return;
            }

            // Keep caption buttons from overlapping the XAML title bar content.
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

                // WinUI 3 Gallery-like: let Mica show through; style caption buttons using theme resources.
                bool isDark = theme == ElementTheme.Dark;

                // Explicit theme-based colors (reliable on both Win10/Win11). Background stays transparent to blend with Mica.
                var foreground = isDark ? Colors.White : Colors.Black;
                var inactiveForeground = isDark ? Color.FromArgb(255, 190, 190, 190) : Color.FromArgb(255, 80, 80, 80);

                // Subtle overlays for hover/pressed that blend with Mica like WinUI.
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
    }
}

using Microsoft.UI.Xaml;
using FolderRewind.Services;
using FolderRewind.Services.Hotkeys;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.UI;

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

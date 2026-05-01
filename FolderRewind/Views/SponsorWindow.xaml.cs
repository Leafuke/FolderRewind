using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace FolderRewind.Views
{
    public sealed partial class SponsorWindow : Window
    {
        public SponsorWindowViewModel ViewModel { get; } = new();

        public SponsorWindow()
        {
            InitializeComponent();
            ConfigureSystemTitleBar();
            ThemeService.ApplyThemeToWindow(this);
            ThemeService.ApplyPersonalizationToWindow(this);
            _ = WindowIconHelper.TryApplyAsync(this);
        }

        private void ConfigureSystemTitleBar()
        {
            try
            {
                ExtendsContentIntoTitleBar = true;
                SetTitleBar(TitleBarDragRegion);
            }
            catch
            {
            }

            try
            {
                if (AppWindow?.TitleBar == null)
                {
                    return;
                }

                var titleBar = AppWindow.TitleBar;
                titleBar.ExtendsContentIntoTitleBar = true;
                titleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

                // 让系统关闭按钮悬在 Mica/Acrylic 上，窗口本身不绘制传统标题栏。
                titleBar.BackgroundColor = Colors.Transparent;
                titleBar.InactiveBackgroundColor = Colors.Transparent;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(32, 128, 128, 128);
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(48, 128, 128, 128);
            }
            catch
            {
            }
        }
    }
}

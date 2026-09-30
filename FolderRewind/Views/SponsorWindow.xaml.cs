using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;

namespace FolderRewind.Views
{
    public sealed partial class SponsorWindow : Window
    {
        public SponsorWindowViewModel ViewModel { get; } = new();

        public SponsorWindow()
        {
            InitializeComponent();
            WindowChromeHelper.ApplySystemTitleBar(this, TitleBarDragRegion);
            ThemeService.ApplyThemeToWindow(this);
            ThemeService.ApplyPersonalizationToWindow(this);
            _ = WindowIconHelper.TryApplyAsync(this);
        }
    }
}

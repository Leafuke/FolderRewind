using FolderRewind.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace FolderRewind.ViewModels
{
    public sealed class ShellPageViewModel : ViewModelBase, IDisposable
    {
        private bool _disposed;

        public string TitleText => GetTitleText();

        public string TitleIconGlyph => GetTitleIconGlyph();

        public bool IsSponsorBackgroundVisible => GetSponsorBackgroundImageSource() != null;

        public ImageSource? SponsorBackgroundImageSource => GetSponsorBackgroundImageSource();

        public Stretch SponsorBackgroundStretch => GetSponsorBackgroundStretch();

        public double SponsorBackgroundOpacity => Math.Clamp(
            ConfigService.CurrentConfig?.GlobalSettings?.SponsorBackgroundImageOpacity ?? 0.28,
            0,
            1);

        public double SponsorBackgroundOverlayOpacity => Math.Clamp(
            ConfigService.CurrentConfig?.GlobalSettings?.SponsorBackgroundOverlayOpacity ?? 0.62,
            0,
            1);

        public Brush SponsorBackgroundOverlayBrush => new SolidColorBrush(GetBackgroundOverlayColor());

        public ShellPageViewModel()
        {
            ConfigService.Saved += OnStateChanged;
            SponsorService.StateChanged += OnStateChanged;
        }

        public void RefreshTitleBar()
        {
            OnPropertyChanged(nameof(TitleText));
            OnPropertyChanged(nameof(TitleIconGlyph));
            OnPropertyChanged(nameof(IsSponsorBackgroundVisible));
            OnPropertyChanged(nameof(SponsorBackgroundImageSource));
            OnPropertyChanged(nameof(SponsorBackgroundStretch));
            OnPropertyChanged(nameof(SponsorBackgroundOpacity));
            OnPropertyChanged(nameof(SponsorBackgroundOverlayBrush));
            OnPropertyChanged(nameof(SponsorBackgroundOverlayOpacity));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ConfigService.Saved -= OnStateChanged;
            SponsorService.StateChanged -= OnStateChanged;
        }

        private void OnStateChanged()
        {
            EnqueueOnUiThread(RefreshTitleBar);
        }

        private static string GetTitleText()
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            if (SponsorService.IsUnlocked && !string.IsNullOrWhiteSpace(settings?.SponsorTitleText))
            {
                return settings.SponsorTitleText.Trim();
            }

            return I18n.GetString("App_WindowTitle");
        }

        private static string GetTitleIconGlyph()
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            if (SponsorService.IsUnlocked && !string.IsNullOrWhiteSpace(settings?.SponsorTitleIconGlyph))
            {
                return settings.SponsorTitleIconGlyph;
            }

            return IconCatalog.DefaultConfigIconGlyph;
        }

        private static ImageSource? GetSponsorBackgroundImageSource()
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            if (!SponsorService.IsUnlocked
                || settings?.SponsorBackgroundEnabled != true
                || string.IsNullOrWhiteSpace(settings.SponsorBackgroundImagePath)
                || !File.Exists(settings.SponsorBackgroundImagePath))
            {
                return null;
            }

            try
            {
                // BitmapImage 直接指向复制后的本地文件；用户原始路径不会被长期依赖。
                return new BitmapImage(new Uri(settings.SponsorBackgroundImagePath, UriKind.Absolute));
            }
            catch (Exception ex)
            {
                LogService.LogWarning(I18n.Format("Sponsor_Log_BackgroundLoadFailed", ex.Message), nameof(ShellPageViewModel));
                return null;
            }
        }

        private static Stretch GetSponsorBackgroundStretch()
        {
            var index = ConfigService.CurrentConfig?.GlobalSettings?.SponsorBackgroundStretchIndex ?? 0;
            return Math.Clamp(index, 0, 2) switch
            {
                1 => Stretch.Uniform,
                2 => Stretch.Fill,
                _ => Stretch.UniformToFill
            };
        }

        private static Color GetBackgroundOverlayColor()
        {
            var theme = ThemeService.GetCurrentTheme();
            if (theme == Microsoft.UI.Xaml.ElementTheme.Dark)
            {
                return Color.FromArgb(255, 0, 0, 0);
            }

            if (theme == Microsoft.UI.Xaml.ElementTheme.Light)
            {
                return Color.FromArgb(255, 255, 255, 255);
            }

            try
            {
                var systemBackground = new UISettings().GetColorValue(UIColorType.Background);
                var luminance = (0.2126 * systemBackground.R) + (0.7152 * systemBackground.G) + (0.0722 * systemBackground.B);
                return luminance < 128
                    ? Color.FromArgb(255, 0, 0, 0)
                    : Color.FromArgb(255, 255, 255, 255);
            }
            catch
            {
                return Color.FromArgb(255, 255, 255, 255);
            }
        }
    }
}

using FolderRewind.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace FolderRewind.ViewModels
{
    public sealed class ShellPageViewModel : ViewModelBase, IDisposable
    {
        private bool _disposed;
        private BitmapImage? _sponsorBackgroundImageSource;
        private bool _isSponsorBackgroundVisible;
        private SponsorBackgroundImageState _backgroundImageState;
        private int _backgroundLoadVersion;

        public string TitleText => GetTitleText();

        public string TitleIconGlyph => GetTitleIconGlyph();

        public bool IsSponsorBackgroundVisible => _isSponsorBackgroundVisible;

        public ImageSource? SponsorBackgroundImageSource => _sponsorBackgroundImageSource;

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

        public async Task RefreshVisualsAsync(bool forceBackgroundImageReload = false)
        {
            if (_disposed)
            {
                return;
            }

            OnPropertyChanged(nameof(TitleText));
            OnPropertyChanged(nameof(TitleIconGlyph));
            OnPropertyChanged(nameof(SponsorBackgroundStretch));
            OnPropertyChanged(nameof(SponsorBackgroundOpacity));
            OnPropertyChanged(nameof(SponsorBackgroundOverlayBrush));
            OnPropertyChanged(nameof(SponsorBackgroundOverlayOpacity));

            await RefreshBackgroundImageAsync(forceBackgroundImageReload);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _backgroundLoadVersion++;
            ConfigService.Saved -= OnStateChanged;
            SponsorService.StateChanged -= OnStateChanged;
        }

        private void OnStateChanged()
        {
            EnqueueOnUiThread(() => _ = RefreshVisualsAsync());
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

        private async Task RefreshBackgroundImageAsync(bool forceReload)
        {
            if (_disposed)
            {
                return;
            }

            var currentState = GetSponsorBackgroundImageState();
            if (SponsorBackgroundImageCachePolicy.ShouldClear(currentState))
            {
                _backgroundImageState = currentState;
                _backgroundLoadVersion++;
                SetSponsorBackgroundImage(null, isVisible: false);
                return;
            }

            var shouldReload = SponsorBackgroundImageCachePolicy.ShouldReload(
                _backgroundImageState,
                currentState,
                _sponsorBackgroundImageSource != null,
                forceReload);
            if (!shouldReload)
            {
                return;
            }

            _backgroundImageState = currentState;
            var requestVersion = ++_backgroundLoadVersion;

            try
            {
                var bitmap = await LoadSponsorBackgroundImageAsync(currentState.Path);
                if (!SponsorBackgroundImageCachePolicy.IsCurrentLoad(
                        requestVersion,
                        _backgroundLoadVersion,
                        _disposed))
                {
                    return;
                }

                // 保留旧图直到新图完整解码成功，避免导航或换图时出现空白帧。
                SetSponsorBackgroundImage(bitmap, isVisible: true);
            }
            catch (Exception ex)
            {
                if (!SponsorBackgroundImageCachePolicy.IsCurrentLoad(
                        requestVersion,
                        _backgroundLoadVersion,
                        _disposed))
                {
                    return;
                }

                LogService.LogWarning(
                    I18n.Format("Sponsor_Log_BackgroundLoadFailed", ex.Message),
                    nameof(ShellPageViewModel));

                // 如果没有旧图，失败后保持隐藏；如果已有旧图，则继续显示旧图，
                // 避免一次加载失败让整个 Shell 突然变空。
                if (_sponsorBackgroundImageSource == null)
                {
                    SetSponsorBackgroundImage(null, isVisible: false);
                }
            }
        }

        private static SponsorBackgroundImageState GetSponsorBackgroundImageState()
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            var path = settings?.SponsorBackgroundImagePath?.Trim() ?? string.Empty;
            var fileExists = !string.IsNullOrWhiteSpace(path) && File.Exists(path);

            return new SponsorBackgroundImageState(
                path,
                settings?.SponsorBackgroundEnabled == true,
                SponsorService.IsUnlocked,
                fileExists);
        }

        private static async Task<BitmapImage> LoadSponsorBackgroundImageAsync(string path)
        {
            // BitmapImage 是 UI 绑定对象；此方法由 UI 调度入口调用，且不使用
            // ConfigureAwait(false)，保证创建和填充过程留在 XAML 所属线程。
            var file = await StorageFile.GetFileFromPathAsync(path);
            var bitmap = new BitmapImage();
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }

        private void SetSponsorBackgroundImage(BitmapImage? image, bool isVisible)
        {
            var imageChanged = !ReferenceEquals(_sponsorBackgroundImageSource, image);
            var visibilityChanged = _isSponsorBackgroundVisible != isVisible;

            _sponsorBackgroundImageSource = image;
            _isSponsorBackgroundVisible = isVisible;

            if (imageChanged)
            {
                OnPropertyChanged(nameof(SponsorBackgroundImageSource));
            }

            if (visibilityChanged)
            {
                OnPropertyChanged(nameof(IsSponsorBackgroundVisible));
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

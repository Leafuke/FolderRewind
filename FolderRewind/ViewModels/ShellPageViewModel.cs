using FolderRewind.Services;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace FolderRewind.ViewModels
{
    public sealed class ShellPageViewModel : ViewModelBase, IDisposable
    {
        private bool _disposed;
        private string? _sponsorBackgroundImagePath;
        private bool _isSponsorBackgroundVisible;
        private SponsorBackgroundImageState _backgroundImageState;
        private int _backgroundLoadVersion;

        public string TitleText => GetTitleText();

        public FolderRewind.Models.GlobalSettings? Settings => ConfigService.CurrentConfig?.GlobalSettings;

        public void PersistPaneState(bool isOpen)
        {
            if (Settings is not { } settings || settings.IsNavPaneOpen == isOpen) return;
            var previous = settings.IsNavPaneOpen;
            TaskObserver.Observe(ConfigEditTransaction.ApplyAsync(() => settings.IsNavPaneOpen = isOpen,
                () => { if (settings.IsNavPaneOpen == isOpen) settings.IsNavPaneOpen = previous; },
                () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed")), nameof(ShellPageViewModel));
        }

        public string TitleIconGlyph => GetTitleIconGlyph();

        public bool IsSponsorBackgroundVisible => _isSponsorBackgroundVisible;

        public string? SponsorBackgroundImagePath => _sponsorBackgroundImagePath;

        public int SponsorBackgroundStretchIndex => GetSponsorBackgroundStretchIndex();

        public double SponsorBackgroundOpacity => Math.Clamp(
            ConfigService.CurrentConfig?.GlobalSettings?.SponsorBackgroundImageOpacity ?? 0.28,
            0,
            1);

        public double SponsorBackgroundOverlayOpacity => Math.Clamp(
            ConfigService.CurrentConfig?.GlobalSettings?.SponsorBackgroundOverlayOpacity ?? 0.62,
            0,
            1);

        public Color SponsorBackgroundOverlayColor => GetBackgroundOverlayColor();

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
            OnPropertyChanged(nameof(SponsorBackgroundStretchIndex));
            OnPropertyChanged(nameof(SponsorBackgroundOpacity));
            OnPropertyChanged(nameof(SponsorBackgroundOverlayColor));
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
            CoreFeatureValidationService.TryScheduleInitialValidation();
            EnqueueOnUiThread(() => TaskObserver.Observe(RefreshVisualsAsync(), nameof(ShellPageViewModel)));
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
                _sponsorBackgroundImagePath != null,
                forceReload);
            if (!shouldReload)
            {
                return;
            }

            _backgroundImageState = currentState;
            var requestVersion = ++_backgroundLoadVersion;

            try
            {
                var imagePath = GetValidImagePath(currentState.Path);
                if (!SponsorBackgroundImageCachePolicy.IsCurrentLoad(
                        requestVersion,
                        _backgroundLoadVersion,
                        _disposed))
                {
                    return;
                }

                SetSponsorBackgroundImage(imagePath, isVisible: true);
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
                if (_sponsorBackgroundImagePath == null)
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

        private static string? GetValidImagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }
            return path;
        }

        private void SetSponsorBackgroundImage(string? imagePath, bool isVisible)
        {
            var imageChanged = _sponsorBackgroundImagePath != imagePath;
            var visibilityChanged = _isSponsorBackgroundVisible != isVisible;

            _sponsorBackgroundImagePath = imagePath;
            _isSponsorBackgroundVisible = isVisible;

            if (imageChanged)
            {
                OnPropertyChanged(nameof(SponsorBackgroundImagePath));
            }

            if (visibilityChanged)
            {
                OnPropertyChanged(nameof(IsSponsorBackgroundVisible));
            }
        }

        private static int GetSponsorBackgroundStretchIndex()
        {
            var index = ConfigService.CurrentConfig?.GlobalSettings?.SponsorBackgroundStretchIndex ?? 0;
            return Math.Clamp(index, 0, 2);
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

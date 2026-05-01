using FolderRewind.Services;
using System;

namespace FolderRewind.ViewModels
{
    public sealed class ShellPageViewModel : ViewModelBase, IDisposable
    {
        private bool _disposed;

        public string TitleText => GetTitleText();

        public string TitleIconGlyph => GetTitleIconGlyph();

        public bool IsSponsorBadgeVisible => SponsorService.IsUnlocked
            && (ConfigService.CurrentConfig?.GlobalSettings?.ShowSponsorBadge ?? true);

        public ShellPageViewModel()
        {
            ConfigService.Saved += OnStateChanged;
            SponsorService.StateChanged += OnStateChanged;
        }

        public void RefreshTitleBar()
        {
            OnPropertyChanged(nameof(TitleText));
            OnPropertyChanged(nameof(TitleIconGlyph));
            OnPropertyChanged(nameof(IsSponsorBadgeVisible));
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
    }
}

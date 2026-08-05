using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static class SponsorPersonalizationService
    {
        private const string ServiceName = nameof(SponsorPersonalizationService);
        private const string BackgroundDirectoryName = "SponsorBackground";
        private static readonly string[] SupportedBackgroundExtensions =
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".bmp",
            ".gif",
            ".webp"
        };

        public static async Task<bool> ApplyBackgroundImageAsync(string sourcePath)
        {
            if (!SponsorService.IsUnlocked)
            {
                NotificationService.ShowInfo(I18n.GetString("Sponsor_BackgroundLocked"), I18n.GetString("Sponsor_Title"));
                return false;
            }

            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                NotificationService.ShowError(I18n.GetString("Sponsor_BackgroundFileMissing"), I18n.GetString("Sponsor_Title"));
                return false;
            }

            var extension = Path.GetExtension(sourcePath);
            if (!IsSupportedBackgroundExtension(extension))
            {
                NotificationService.ShowWarning(I18n.GetString("Sponsor_BackgroundUnsupportedFormat"), I18n.GetString("Sponsor_Title"));
                return false;
            }

            try
            {
                var targetDir = Path.Combine(ConfigService.ConfigDirectory, BackgroundDirectoryName);
                var targetPath = Path.Combine(targetDir, $"background{extension.ToLowerInvariant()}");

                // 文件 IO 可以放到线程池，但 GlobalSettings 会被 XAML x:Bind 监听，
                // 其属性变更必须在 UI 线程触发，否则绑定会跨线程更新 ToggleSwitch。
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(targetDir);
                    File.Copy(sourcePath, targetPath, overwrite: true);
                }).ConfigureAwait(false);

                await UiDispatcherService.RunOnUiAsync(() =>
                {
                    var settings = ConfigService.CurrentConfig.GlobalSettings;
                    settings.SponsorBackgroundImagePath = targetPath;
                    settings.SponsorBackgroundEnabled = true;
                    ConfigService.Save();

                    MainWindowService.ApplySponsorVisuals();
                    NotificationService.ShowSuccess(
                        I18n.GetString("Sponsor_BackgroundApplied"),
                        I18n.GetString("Sponsor_Title"));
                }).ConfigureAwait(false);

                return true;
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("Sponsor_Log_BackgroundApplyFailed", ex.Message), ServiceName, ex);
                NotificationService.ShowError(I18n.Format("Sponsor_BackgroundApplyFailed", ex.Message), I18n.GetString("Sponsor_Title"));
                return false;
            }
        }

        public static bool ClearBackgroundImage()
        {
            if (!SponsorService.IsUnlocked)
            {
                return false;
            }

            try
            {
                var cleared = false;
                UiDispatcherService.RunOnUiAsync(() =>
                {
                    var settings = ConfigService.CurrentConfig.GlobalSettings;
                    settings.SponsorBackgroundEnabled = false;
                    settings.SponsorBackgroundImagePath = string.Empty;
                    ConfigService.Save();

                    MainWindowService.ApplySponsorVisuals();
                    NotificationService.ShowSuccess(
                        I18n.GetString("Sponsor_BackgroundCleared"),
                        I18n.GetString("Sponsor_Title"));
                    cleared = true;
                }).GetAwaiter().GetResult();

                return cleared;
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("Sponsor_Log_BackgroundClearFailed", ex.Message), ServiceName, ex);
                NotificationService.ShowError(I18n.Format("Sponsor_BackgroundClearFailed", ex.Message), I18n.GetString("Sponsor_Title"));
                return false;
            }
        }

        public static bool IsSupportedBackgroundExtension(string? extension)
        {
            return !string.IsNullOrWhiteSpace(extension)
                && SupportedBackgroundExtensions.Contains(extension.Trim(), StringComparer.OrdinalIgnoreCase);
        }
    }
}

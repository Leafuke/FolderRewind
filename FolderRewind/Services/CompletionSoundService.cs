using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace FolderRewind.Services
{
    public static class CompletionSoundService
    {
        private const string ServiceName = nameof(CompletionSoundService);
        private const string SoundDirectoryName = "CompletionSound";
        private const uint DefaultBeep = 0xFFFFFFFF;

        private static readonly string[] SupportedAudioExtensions =
        {
            ".wav",
            ".mp3",
            ".m4a",
            ".aac",
            ".wma",
            ".flac"
        };

        private static MediaPlayer? _customPlayer;

        public const int PresetCount = 2;

        public static string GetPresetName(int index)
        {
            return Math.Clamp(index, 0, PresetCount - 1) switch
            {
                1 => I18n.GetString("CompletionSound_Default"),
                _ => I18n.GetString("CompletionSound_None")
            };
        }

        public static void PlayConfiguredCompletionSound(bool success)
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            Play(settings?.CompletionSoundIndex ?? 0);
        }

        public static void PreviewConfiguredSound()
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            Play(settings?.CompletionSoundIndex ?? 0);
        }

        public static async Task<bool> ApplyCustomSoundAsync(string sourcePath)
        {
            if (!SponsorService.IsUnlocked)
            {
                NotificationService.ShowInfo(I18n.GetString("CompletionSound_CustomLocked"), I18n.GetString("Sponsor_Title"));
                return false;
            }

            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                NotificationService.ShowError(I18n.GetString("CompletionSound_FileMissing"), I18n.GetString("Sponsor_Title"));
                return false;
            }

            var extension = Path.GetExtension(sourcePath);
            if (!IsSupportedAudioExtension(extension))
            {
                NotificationService.ShowWarning(I18n.GetString("CompletionSound_UnsupportedFormat"), I18n.GetString("Sponsor_Title"));
                return false;
            }

            try
            {
                var targetDir = Path.Combine(ConfigService.ConfigDirectory, SoundDirectoryName);
                Directory.CreateDirectory(targetDir);

                var targetPath = Path.Combine(targetDir, $"completion{extension.ToLowerInvariant()}");
                await Task.Run(() => File.Copy(sourcePath, targetPath, overwrite: true)).ConfigureAwait(false);

                var settings = ConfigService.CurrentConfig.GlobalSettings;
                settings.CompletionSoundCustomPath = targetPath;
                settings.CompletionSoundIndex = 1;
                ConfigService.Save();

                NotificationService.ShowSuccess(I18n.GetString("CompletionSound_CustomApplied"), I18n.GetString("Sponsor_Title"));
                return true;
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("CompletionSound_Log_CustomApplyFailed", ex.Message), ServiceName, ex);
                NotificationService.ShowError(I18n.Format("CompletionSound_CustomApplyFailed", ex.Message), I18n.GetString("Sponsor_Title"));
                return false;
            }
        }

        public static bool ClearCustomSound()
        {
            if (!SponsorService.IsUnlocked)
            {
                return false;
            }

            try
            {
                ConfigService.CurrentConfig.GlobalSettings.CompletionSoundCustomPath = string.Empty;
                ConfigService.Save();
                NotificationService.ShowSuccess(I18n.GetString("CompletionSound_CustomCleared"), I18n.GetString("Sponsor_Title"));
                return true;
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("CompletionSound_Log_CustomClearFailed", ex.Message), ServiceName, ex);
                NotificationService.ShowError(I18n.Format("CompletionSound_CustomClearFailed", ex.Message), I18n.GetString("Sponsor_Title"));
                return false;
            }
        }

        public static bool IsSupportedAudioExtension(string? extension)
        {
            return !string.IsNullOrWhiteSpace(extension)
                && SupportedAudioExtensions.Contains(extension.Trim(), StringComparer.OrdinalIgnoreCase);
        }

        private static void Play(int index)
        {
            if (Math.Clamp(index, 0, PresetCount - 1) == 0)
            {
                return;
            }

            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            if (SponsorService.IsUnlocked
                && !string.IsNullOrWhiteSpace(settings?.CompletionSoundCustomPath)
                && File.Exists(settings.CompletionSoundCustomPath))
            {
                PlayCustom(settings.CompletionSoundCustomPath);
                return;
            }

            PlayDefault();
        }

        private static void PlayDefault()
        {
            try
            {
                _ = Task.Run(() => MessageBeep(DefaultBeep));
            }
            catch (Exception ex)
            {
                LogService.LogWarning(I18n.Format("CompletionSound_Log_PlayFailed", ex.Message), ServiceName);
            }
        }

        private static void PlayCustom(string path)
        {
            try
            {
                _customPlayer ??= new MediaPlayer();
                _customPlayer.Source = MediaSource.CreateFromUri(new Uri(path, UriKind.Absolute));
                _customPlayer.Play();
            }
            catch (Exception ex)
            {
                LogService.LogWarning(I18n.Format("CompletionSound_Log_PlayFailed", ex.Message), ServiceName);
                PlayDefault();
            }
        }

        [DllImport("user32.dll")]
        private static extern bool MessageBeep(uint uType);
    }
}

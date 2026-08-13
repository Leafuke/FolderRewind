using FolderRewind.Services.Plugins.V3;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public sealed class MinecraftOnboardingResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public bool MineBackupModReminderRequired { get; init; }
    }

    public static class MinecraftOnboardingService
    {
        public static async Task<MinecraftOnboardingResult> InstallPresetAsync(
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            try
            {
                var result = await PluginPresetService.ExecuteAsync(
                    PluginPresetService.MinecraftEnhancedExperiencePath,
                    new InteractiveExternalInstallerConsent(),
                    progress,
                    ct);
                var message = string.Join(Environment.NewLine, result.Steps.Select(value => $"{value.ActionId}: {value.Message}"));
                if (result.Success) NotificationService.ShowSuccess(message, I18n.GetString("MinecraftOnboarding_Title"), 8000);
                else NotificationService.ShowError(message, I18n.GetString("MinecraftOnboarding_Title"));
                return new MinecraftOnboardingResult
                {
                    Success = result.Success,
                    Message = message,
                    MineBackupModReminderRequired = true
                };
            }
            catch (OperationCanceledException)
            {
                return new MinecraftOnboardingResult { Success = false, Message = I18n.GetString("Common_Canceled") };
            }
            catch (Exception ex)
            {
                LogService.LogError(ex.Message, nameof(MinecraftOnboardingService), ex);
                return new MinecraftOnboardingResult { Success = false, Message = ex.Message };
            }
        }

        private sealed class InteractiveExternalInstallerConsent : IPluginPresetConsentBroker
        {
            public ValueTask<bool> ConfirmExternalDownloadAsync(string name, string url, string sha256, CancellationToken cancellationToken)
                => new(MainWindowService.ConfirmAsync(
                    $"Download {name}?",
                    $"Official URL:\n{url}\n\nExpected SHA-256:\n{sha256}",
                    "Download"));
            public ValueTask<bool> ConfirmExternalLaunchAsync(string name, string localPath, CancellationToken cancellationToken)
                => new(MainWindowService.ConfirmAsync(
                    $"Launch {name} installer?",
                    $"The downloaded file passed SHA-256 verification.\n\n{localPath}",
                    "Launch"));
        }
    }
}

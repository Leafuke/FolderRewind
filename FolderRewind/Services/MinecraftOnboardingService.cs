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
                var message = string.Join(
                    Environment.NewLine,
                    result.Steps.Select(value => I18n.Format(
                        "PluginPreset_StepResult",
                        PluginPresetService.GetActionDisplayName(value.ActionId),
                        value.Message)));
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
                    I18n.Format("PluginPreset_ConsentDownloadTitle", name),
                    I18n.Format("PluginPreset_ConsentDownloadContent", url, sha256),
                    I18n.GetString("PluginPreset_ConsentDownloadButton")));
            public ValueTask<bool> ConfirmExternalLaunchAsync(string name, string localPath, CancellationToken cancellationToken)
                => new(MainWindowService.ConfirmAsync(
                    I18n.Format("PluginPreset_ConsentLaunchTitle", name),
                    I18n.Format("PluginPreset_ConsentLaunchContent", localPath),
                    I18n.GetString("PluginPreset_ConsentLaunchButton")));
        }
    }
}

using FolderRewind.History.Application;
using FolderRewind.History.Cloud;
using FolderRewind.Models;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static partial class CloudSyncService
{
    private static void PublishJobNotification(HistoryRuntime runtime, string jobId)
    {
        try
        {
            var job = runtime.CloudJobs.Read().Jobs.Single(j => j.JobId == jobId);
            var config = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(c => runtime.ConfigId.Matches(c.Id));
            var success = job.Phase == CloudJobPhase.Succeeded;
            var active = job.Phase is CloudJobPhase.Resolving or CloudJobPhase.Uploading or CloudJobPhase.Verifying or CloudJobPhase.SyncingMetadata;
            var failed = job.Phase is CloudJobPhase.NeedsAttention or CloudJobPhase.RetryableFailure;
            var phase = I18n.GetString("CloudJob_Phase_" + job.Phase);
            NotificationService.ShowInfoBar(new InAppNotificationRequest
            {
                Correlation = new("cloud:" + job.ProjectId, job.JobId, job.CreatedUtc, job.UpdatedUtc ?? DateTimeOffset.UtcNow),
                Title = I18n.Format("CloudNotice_Title", config?.Name ?? job.ProjectId, phase),
                Message = I18n.Format("CloudNotice_Body", UserDisplayFormatter.LongDateTime((job.UpdatedUtc ?? job.CreatedUtc).LocalDateTime),
                    job.JobId[..8], failed ? CloudFailurePresentation.Message(CloudFailurePresentation.Kind("", job.Diagnostic)) : phase),
                Severity = success ? NotificationSeverity.Success : active || job.Phase is CloudJobPhase.Canceled or CloudJobPhase.Paused
                    ? NotificationSeverity.Informational : NotificationSeverity.Warning,
                ActionText = I18n.GetString("CloudNotice_ViewTask"),
                Action = () => NavigationService.NavigateTo("CloudProject", new CloudSetupNavigationParameter(config?.Id ?? job.ProjectId,
                    Intent: CloudSetupIntent.ProjectStatus, JobId: job.JobId))
            });
        }
        catch (Exception error) { LogService.LogWarning(CloudCommandSecurity.Redact(error.Message), nameof(CloudSyncService)); }
    }

    private static async Task PublishDisplayAfterSuccessAsync(HistoryRuntime runtime, RcloneExecutionContext context,
        BackupConfig config, HistoryCloudJob job)
    {
        try
        {
            using var budget = new CancellationTokenSource(OnboardingOperationBudgets.RemoteBrowse);
            await CloudRepositoryDisplayService.PublishAsync(runtime, context, config.Name, job.Representations, budget.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            LogService.LogWarning("Repository display: " + CloudCommandSecurity.Redact(error.Message), nameof(CloudSyncService));
            var now = DateTimeOffset.UtcNow;
            NotificationService.ShowInfoBar(new InAppNotificationRequest
            {
                Correlation = new("cloud-display:" + job.ProjectId, job.JobId, job.CreatedUtc, now),
                Title = I18n.GetString("Notification_Warning_Title"),
                Message = I18n.Format("CloudNotice_Body", UserDisplayFormatter.LongDateTime(now.LocalDateTime), job.JobId[..8],
                    I18n.Format("CloudRecovery_DisplayPublishFailed", config.Name)),
                Severity = NotificationSeverity.Warning
            });
        }
    }
}

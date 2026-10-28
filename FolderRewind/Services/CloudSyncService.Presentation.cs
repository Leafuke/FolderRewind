using FolderRewind.History.Application;
using FolderRewind.History.Cloud;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static partial class CloudSyncService
{
    private static void PublishJobNotification(HistoryRuntime runtime, string jobId, NotificationTrigger trigger)
    {
        try
        {
            var job = runtime.CloudJobs.Read().Jobs.Single(j => j.JobId == jobId);
            var config = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(c => runtime.ConfigId.Matches(c.Id));
            var success = job.Phase == CloudJobPhase.Succeeded;
            var active = job.Phase is CloudJobPhase.Resolving or CloudJobPhase.Uploading or CloudJobPhase.Verifying or CloudJobPhase.SyncingMetadata;
            var failed = job.Phase is CloudJobPhase.NeedsAttention or CloudJobPhase.RetryableFailure;
            var phase = I18n.GetString("CloudJob_Phase_" + job.Phase);
            var request = new InAppNotificationRequest
            {
                Correlation = new("cloud:" + job.ProjectId + ":" + job.JobId, job.JobId, job.CreatedUtc, job.UpdatedUtc ?? DateTimeOffset.UtcNow),
                RecordId = "cloud:" + job.JobId,
                Category = active || job.Phase is CloudJobPhase.Prepared or CloudJobPhase.Pending or CloudJobPhase.Paused ? NotificationCategory.Progress
                    : job.Phase is CloudJobPhase.Canceled or CloudJobPhase.Aborted ? NotificationCategory.Cancellation
                    : failed ? NotificationCategory.Attention : NotificationCategory.BusinessResult,
                Source = "cloud", FailureCode = failed ? CloudFailurePresentation.Kind("", job.Diagnostic) : null,
                Content = new(NotificationOperationKind.Cloud, trigger,
                    success ? NotificationResultKind.Success : failed ? NotificationResultKind.Failed
                        : job.Phase is CloudJobPhase.Canceled or CloudJobPhase.Aborted ? NotificationResultKind.Canceled : NotificationResultKind.Running,
                    config?.Name ?? job.ProjectId, config?.Name ?? job.ProjectId, Summary:
                        failed ? CloudFailurePresentation.Message(CloudFailurePresentation.Kind("", job.Diagnostic)) : phase),
                Target = new(NotificationTargetKind.CloudProject, job.JobId, config?.Id ?? job.ProjectId, JobId: job.JobId),
                KeepInCenter = success || failed || job.Phase is CloudJobPhase.Canceled or CloudJobPhase.Aborted,
                ResultKey = job.Phase + ":" + CloudCommandSecurity.Redact(job.Diagnostic),
                Details = CloudCommandSecurity.Redact(job.Diagnostic),
                Title = I18n.Format("CloudNotice_Title", config?.Name ?? job.ProjectId, phase),
                Message = I18n.Format("CloudNotice_Body", UserDisplayFormatter.LongDateTime((job.UpdatedUtc ?? job.CreatedUtc).LocalDateTime),
                    job.JobId[..8], failed ? CloudFailurePresentation.Message(CloudFailurePresentation.Kind("", job.Diagnostic)) : phase),
                Severity = success ? NotificationSeverity.Success : job.Phase == CloudJobPhase.NeedsAttention ? NotificationSeverity.Error
                    : active || job.Phase is CloudJobPhase.Canceled or CloudJobPhase.Aborted or CloudJobPhase.Paused
                    ? NotificationSeverity.Informational : NotificationSeverity.Warning,
                ActionText = I18n.GetString("CloudNotice_ViewTask")
            };
            if (failed)
            {
                // 需要人工处理的失败也符合“仅错误”；自动重试失败按“重要”提醒。
                var importance = job.Phase == CloudJobPhase.NeedsAttention
                    ? NotificationImportance.Error : NotificationImportance.Important;
                NotificationService.ShowNotification(request, importance,
                    new Dictionary<string, string> { ["target"] = "Tasks" },
                    tag: "cloud_" + job.ProjectId, group: "cloud");
            }
            else
            {
                if (success) NotificationService.ShowNotification(request, NotificationImportance.Info);
                else NotificationService.ShowInfoBar(request);
            }
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
                Correlation = new("cloud-display:" + job.ProjectId + ":" + job.JobId, job.JobId, job.CreatedUtc, now),
                RecordId = "cloud-display:" + job.JobId, KeepInCenter = true,
                ResultKey = "display-publish-failed:" + CloudCommandSecurity.Redact(error.Message),
                Details = CloudCommandSecurity.Redact(error.Message),
                Target = new(NotificationTargetKind.CloudProject, job.JobId, config.Id, JobId: job.JobId),
                Title = I18n.GetString("Notification_Warning_Title"),
                Message = I18n.Format("CloudNotice_Body", UserDisplayFormatter.LongDateTime(now.LocalDateTime), job.JobId[..8],
                    I18n.Format("CloudRecovery_DisplayPublishFailed", config.Name)),
                Severity = NotificationSeverity.Warning
            });
        }
    }
}

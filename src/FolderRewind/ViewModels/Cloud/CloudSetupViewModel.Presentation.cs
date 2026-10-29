using FolderRewind.History.Application;
using FolderRewind.History.Cloud;
using FolderRewind.History.Domain;
using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed partial class CloudSetupViewModel
{
    public string ConnectionCheckSummary { get; private set; } = I18n.GetString("CloudStatus_CheckUnknown");
    public string LatestJobSummary { get; private set; } = I18n.GetString("CloudStatus_NoJobs");
    public string PendingJobSummary { get; private set; } = "";
    public string FailureKind { get; private set; } = "Unknown";
    public bool HasRepair => MessageState == OnboardingCheckState.NeedsInput;
    public string? RequestedJobId { get; set; }
    public int MessageSeverity => MessageState == OnboardingCheckState.NeedsInput ? 2 : MessageState == OnboardingCheckState.Ready ? 1 : 0;
    private static string ObservationKey(RcloneExecutionContext context) => CloudConnectionObservationStore.Key(context.ExecutablePath,
        context.ConfigPath, context.WorkingDirectory, context.RemoteRoot, context.TargetRevisionEvidence);
    private async Task ObserveConnectionAsync(RcloneExecutionContext context, bool writable, bool succeeded, string failureKind = "Unknown")
    {
        var observation = new CloudConnectionObservation(ObservationKey(context), writable, succeeded, DateTimeOffset.UtcNow, failureKind);
        DisplayObservation(observation);
        try { await CloudConnectionObservationStore.SaveAsync(ConfigService.ConfigDirectory, observation); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { LogService.LogWarning(CloudCommandSecurity.Redact(error.Message), nameof(CloudSetupViewModel)); }
    }
    private void DisplayObservation(CloudConnectionObservation observation)
    {
        ConnectionCheckSummary = I18n.Format("CloudStatus_CheckedAt", UserDisplayFormatter.LongDateTime(observation.CheckedAtUtc.LocalDateTime),
            observation.Succeeded ? I18n.GetString(observation.Writable ? "CloudStatus_WriteVerified" : "CloudStatus_ReadVerified")
                : CloudFailurePresentation.Message(observation.FailureKind));
        OnPropertyChanged(nameof(ConnectionCheckSummary));
    }
    private async Task RestoreConnectionObservationAsync(RcloneExecutionContext context, CancellationToken token)
    {
        var key = ObservationKey(context);
        var observation = await Task.Run(() => CloudConnectionObservationStore.Load(ConfigService.ConfigDirectory, key), token);
        if (_disposed) return;
        if (observation is not null) DisplayObservation(observation);
        else
        {
            var evidence = context.TargetRevisionEvidence + "|" + context.RemoteRoot;
            ConnectionCheckSummary = I18n.GetString(_config?.Cloud.WritableEvidence == evidence ? "CloudStatus_LegacyVerified" : "CloudStatus_CheckUnknown");
            OnPropertyChanged(nameof(ConnectionCheckSummary));
        }
    }
    private static bool CurrentTarget(HistoryCloudJob job, CloudJobTarget target)
    {
        return job.Target.ProfileId == target.ProfileId && job.Target.ProfileRevision == target.ProfileRevision
            && job.Target.BindingRevision == target.BindingRevision && job.Target.RemoteRoot == target.RemoteRoot
            && job.Target.CredentialTargetRevision == target.CredentialTargetRevision;
    }
    private static string JobTime(HistoryCloudJob job) => job.CompletedUtc is { } completed
        ? UserDisplayFormatter.LongDateTime(completed.LocalDateTime) : I18n.GetString("CloudStatus_TimeUnrecorded");
    private static string JobExplanation(HistoryCloudJob job)
    {
        var protection = I18n.GetString(job.Protection == CloudProtectionDisposition.Held ? "CloudJob_ProtectionHeld"
            : job.Protection == CloudProtectionDisposition.Abandoned ? "CloudJob_ProtectionAbandoned"
            : job.Phase == CloudJobPhase.Succeeded ? "CloudStatus_UploadDone" : "CloudJob_ProtectionReleased");
        var failed = job.Phase is CloudJobPhase.NeedsAttention or CloudJobPhase.RetryableFailure;
        return failed ? CloudFailurePresentation.Message(CloudFailurePresentation.Kind("", job.Diagnostic)) + "\n" + protection : protection;
    }

    private async Task<string> RepositoryLabelAsync(RcloneExecutionContext context, string folder, CancellationToken token)
    {
        var descriptorBytes = await RcloneConnectionService.RunBytesAsync(context.CreateStartInfo(["cat",
            CloudRemotePath.Append(context.RemoteRoot, "_folderrewind/history", folder, "repository.json")]), OnboardingOperationBudgets.RemoteBrowse, token, 4096);
        var descriptor = FolderRewind.History.Storage.HistoryRepositoryDescriptor.Parse(descriptorBytes);
        if (FolderRewind.History.Storage.HistoryRepositoryPaths.EncodeConfigPathSegment(descriptor.ConfigId) != folder)
            throw new InvalidDataException("Repository identity differs from its directory.");
        var display = await CloudRepositoryDisplayService.ReadAsync(context, descriptor.ConfigId, token);
        var shortId = descriptor.ConfigId.Value[..Math.Min(12, descriptor.ConfigId.Value.Length)];
        if (display is not null) return I18n.Format("CloudRecovery_NamedOption", display.ProjectName,
            UserDisplayFormatter.LongDateTime(display.LatestVersionUtc.LocalDateTime), shortId);
        // Older repositories only contain source names in their metadata. No payload is downloaded.
        var root = Path.Combine(ConfigService.ConfigDirectory, "onboarding", "cloud-list", Guid.NewGuid().ToString("N"));
        var runtime = new HistoryRuntime(new FolderRewind.History.Storage.FileHistoryRepository(descriptor.ConfigId,
            new FolderRewind.History.Storage.HistoryRepositoryPaths(root)));
        try
        {
            await runtime.InitializeAsync(token);
            var pull = await new FolderRewind.History.Cloud.HistoryMetadataSyncService(runtime, new RcloneNativeHistoryTransport(context)).PullAsync(token);
            if (!pull.Succeeded) return I18n.Format("CloudRecovery_UnnamedOption", shortId);
            var latest = (await runtime.Query.GetAllVersionsAsync(token)).OrderByDescending(v => v.CreatedAtUtc).FirstOrDefault();
            return latest is null ? I18n.Format("CloudRecovery_UnnamedOption", shortId)
                : I18n.Format("CloudRecovery_NamedOption", latest.SourceDescriptorSnapshot.DisplayName,
                    UserDisplayFormatter.LongDateTime(latest.CreatedAtUtc.LocalDateTime), shortId);
        }
        finally
        {
            await runtime.DisposeAsync();
            try { Directory.Delete(root, true); }
            catch (IOException error) { LogService.LogWarning(error.GetType().Name, nameof(CloudSetupViewModel)); }
        }
    }
}

using FolderRewind.History.Application;
using FolderRewind.History.Cloud;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static partial class CloudSyncService
{
    private static readonly ConcurrentDictionary<string, Task> CloudWorkers = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, BackupTask> JobTasks = new(StringComparer.Ordinal);
    private static readonly System.Collections.Generic.HashSet<string> CloudWakeRequests = new(StringComparer.Ordinal);
    private static readonly CancellationTokenSource CloudStopping = new();

    private static CloudJobTarget FreezeTarget(BackupConfig config)
    {
        var settings = config.Cloud;
        var profile = ConfigService.CurrentConfig.CloudConnections.FirstOrDefault(p => p.Id == settings.ConnectionProfileId);
        var credential = profile?.CredentialReference ?? settings.RcloneConfigPath;
        var root = profile is null ? settings.RemoteBasePath : profile.RemoteName + ":" + settings.RemotePathSuffix;
        var revision = "";
        try
        {
            if (Path.IsPathFullyQualified(credential) && File.Exists(credential))
                revision = RcloneCredentialRefresh.TargetRevision(RcloneExecutionContext.ReadConfigurationBytes(credential));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { /* 缺凭据也先保留接纳证据；不把本地成功变成没有 Job。解析/认证失败由执行阶段明确暂停。 */ }
        return new(settings.ConnectionProfileId ?? "legacy", profile?.ProfileRevision ?? revision,
            settings.BindingRevision, root, config.Id, revision,
            Convert.ToHexString(SHA256.HashData(HistoryRepositoryDescriptor.Create(new(config.Id)).ToCanonicalBytes())));
    }

    internal static async Task PrepareCloudAdmissionAsync(BackupConfig config, HistoryRuntime runtime, CancellationToken token)
    {
        await using var gate = await runtime.MutationGate.EnterAsync(token).ConfigureAwait(false);
        try
        {
            // 仅接纳此机器今后的备份提交；升级、开启开关不自动追认旧 History 或导入 Pack。
            runtime.CloudJobs.ConfigureAdmission(config.Cloud.Enabled && !MigrationSetupStateStore.IsCloudPending(ConfigService.ConfigDirectory, config.Id)
                ? FreezeTarget(config) : null);
        }
        catch (Exception ex) { runtime.CloudJobs.ObserveFailure(ex); }
    }
    internal static async Task CloseCloudAdmissionAsync(BackupConfig config, CancellationToken token)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        await using var gate = await runtime.MutationGate.EnterAsync(token).ConfigureAwait(false);
        // 配置目标变更必须先闭合原区间；失败时配置保存失败，不能留下未知投递位置。
        runtime.CloudJobs.ConfigureAdmission(null);
    }
    private static bool IsFrozenTargetCurrent(HistoryCloudJob job, out BackupConfig? current)
    {
        current = ConfigService.CurrentConfig.BackupConfigs.SingleOrDefault(p => p.Id == job.ProjectId);
        if (current is null || current.Cloud.LegacyConversionRequired) return false;
        return FreezeTarget(current) == job.Target;
    }
    internal static void ResumeCloudJobs(BackupConfig config)
    {
        lock (AutomaticSyncLock)
        {
            if (_automaticSyncStopping) return;
            if (CloudWorkers.TryGetValue(config.Id, out var active) && !active.IsCompleted)
            { CloudWakeRequests.Add(config.Id); return; }
            var worker = Task.Run(async () =>
            {
                try { await RunPendingJobsAsync(config.Id, CloudStopping.Token).ConfigureAwait(false); }
                finally
                {
                    lock (AutomaticSyncLock)
                    {
                        CloudWorkers.TryRemove(config.Id, out _);
                        if (CloudWakeRequests.Remove(config.Id) && !_automaticSyncStopping) ResumeCloudJobs(config);
                    }
                }
            });
            CloudWorkers[config.Id] = worker; TaskObserver.Observe(worker, nameof(CloudSyncService));
        }
    }
    private static async Task RunPendingJobsAsync(string projectId, CancellationToken token)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(projectId, token).ConfigureAwait(false);
        while (!_automaticSyncStopping && !token.IsCancellationRequested)
        {
            HistoryCloudJob? job;
            try
            {
                job = runtime.CloudJobs.Read().Jobs.FirstOrDefault(j => j.Phase is CloudJobPhase.Pending or CloudJobPhase.RetryableFailure
                    && j.NextEligibleUtc <= DateTimeOffset.UtcNow);
            }
            catch (Exception ex) { LogService.LogWarning(CloudCommandSecurity.Redact(ex.Message), nameof(CloudSyncService)); return; }
            if (job is null)
            {
                if (!runtime.CloudJobs.Read().Jobs.Any(j => j.Phase is CloudJobPhase.Pending or CloudJobPhase.RetryableFailure)) return;
                await Task.Delay(1000, token).ConfigureAwait(false); continue;
            }
            await ExecuteCloudJobAsync(runtime, job, token).ConfigureAwait(false);
        }
    }

    internal static async Task RetryCloudJobsAsync(BackupConfig config, CancellationToken token = default)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        await using (var gate = await runtime.MutationGate.EnterAsync(token).ConfigureAwait(false))
        {
            foreach (var job in runtime.CloudJobs.Read().Jobs.Where(j => j.Protection == CloudProtectionDisposition.Held
                && j.Phase is CloudJobPhase.NeedsAttention or CloudJobPhase.Paused or CloudJobPhase.RetryableFailure or CloudJobPhase.Canceled))
            {
                if (!IsFrozenTargetCurrent(job, out _)) continue;
                runtime.CloudJobs.Update(job with { Phase = CloudJobPhase.Pending, NextEligibleUtc = DateTimeOffset.UtcNow, Diagnostic = "" });
            }
        }
        ResumeCloudJobs(config);
    }
    internal static async Task CancelCloudJobAsync(string projectId, string jobId, bool abandonProtection, CancellationToken token = default)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(projectId, token).ConfigureAwait(false);
        await using var gate = await runtime.MutationGate.EnterAsync(token).ConfigureAwait(false);
        var job = runtime.CloudJobs.Read().Jobs.Single(j => j.JobId == jobId);
        if (job.Phase is CloudJobPhase.Resolving or CloudJobPhase.Uploading or CloudJobPhase.Verifying or CloudJobPhase.SyncingMetadata)
            throw new InvalidOperationException(I18n.GetString("CloudJob_RunningCannotRelease"));
        runtime.CloudJobs.Update(job with { Phase = CloudJobPhase.Canceled,
            Protection = abandonProtection ? CloudProtectionDisposition.Abandoned : job.Protection });
    }

    private static async Task<HistoryCloudBackupResult> ExecuteCloudJobAsync(HistoryRuntime runtime, HistoryCloudJob initial,
        CancellationToken token, string? explicitLocalPath = null)
    {
        HistoryCloudBackupResult Incomplete(string code) => new([], initial.Representations, null, token.IsCancellationRequested, DateTimeOffset.UtcNow);
        await CommandSemaphore.WaitAsync(token).ConfigureAwait(false);
        var job = initial;
        BackupTask? task = null;
        try
        {
            await using (var gate = await runtime.MutationGate.EnterAsync(token).ConfigureAwait(false))
            {
                job = runtime.CloudJobs.Read().Jobs.Single(j => j.JobId == initial.JobId);
                if (job.Protection != CloudProtectionDisposition.Held || job.Phase is CloudJobPhase.Canceled or CloudJobPhase.Succeeded or CloudJobPhase.Aborted)
                    return Incomplete("cloud.job-terminal");
                job = job with { Phase = CloudJobPhase.Resolving, Attempt = job.Attempt + 1, Diagnostic = "" };
                runtime.CloudJobs.Update(job);
            }
            if (!IsFrozenTargetCurrent(job, out var current) || current is null)
            {
                runtime.CloudJobs.Update(job with { Phase = CloudJobPhase.NeedsAttention, Diagnostic = "cloud.target-changed" });
                return Incomplete("cloud.target-changed");
            }
            if (job.Automatic && !current.Cloud.Enabled)
            {
                runtime.CloudJobs.Update(job with { Phase = CloudJobPhase.Paused, Diagnostic = "cloud.job-paused" }); return Incomplete("cloud.job-paused");
            }
            RequireMigrationCloudPermission(current);
            var config = await PrepareConnectionAsync(current, token).ConfigureAwait(false);
            using var context = CreateBoundConnection(config) ?? throw new InvalidOperationException(I18n.GetString("CloudSetup_ExplicitConfigRequired"));
            if (config.Cloud.WritableEvidence != context.TargetRevisionEvidence + "|" + context.RemoteRoot)
                throw new InvalidOperationException("[connection.write-verification-required] " + I18n.GetString("CloudJob_WriteVerificationRequired"));
            context.TargetGuard = () =>
            {
                if (!IsFrozenTargetCurrent(job, out var now) || now is null || job.Automatic && !now.Cloud.Enabled)
                    throw new InvalidOperationException("[cloud.target-changed] " + I18n.GetString("CloudJob_TargetChanged"));
            };
            using var connection = new RcloneExecutionScope(context, owns: false);
            task = JobTasks.GetOrAdd(job.JobId, _ => new BackupTask { FolderName = config.Name });
            await RunOnUIAsync(() =>
            {
                if (!BackupService.ActiveTasks.Contains(task)) BackupService.ActiveTasks.Add(task);
                task.Log = "JobId: " + job.JobId; task.IsCompleted = false; task.IsSuccess = false;
                task.IsIndeterminate = true; task.Status = I18n.GetString("CloudJob_Uploading");
            }).ConfigureAwait(false);
            var graph = (await runtime.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false)).ToDictionary(r => r.RepresentationId);
            var closure = job.Representations.SelectMany(id => ExactReplicaPreparation.Closure(graph[id], graph)).Select(r => r.RepresentationId).Distinct().ToArray();
            var intents = job.UploadIntents.ToDictionary(i => i.RepresentationId);
            foreach (var id in closure) if (!intents.ContainsKey(id)) intents.Add(id, new(id, ReplicaId.New(), null));
            job = job with { UploadIntents = [.. intents.Values], Phase = CloudJobPhase.Uploading };
            runtime.CloudJobs.Update(job); // 稳定 ReplicaId 必须先于对应远端写入落盘。
            var metadata = new HistoryMetadataSyncService(runtime, CreateHistoryTransport(config));
            var transport = new RcloneNativeHistoryTransport(context, config.Cloud.TimeoutSeconds);
            var descriptor = await transport.ReadDescriptorAsync(runtime.ConfigId, token).ConfigureAwait(false);
            if (descriptor is not null && Convert.ToHexString(SHA256.HashData(HistoryRepositoryDescriptor.Parse(descriptor).ToCanonicalBytes())) != job.Target.DescriptorSha256)
                throw new InvalidDataException("[cloud.history-conflict] Remote repository identity differs.");
            var service = new HistoryCloudBackupService(runtime, transport, ct => metadata.SyncAsync(ct));
            var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
            var result = await service.UploadClosureAsync(job.Representations, (id, _) => Task.FromResult(
                explicitLocalPath is not null && job.Representations.Length == 1 && job.Representations[0] == id ? explicitLocalPath
                    : catalog?.Entries.Where(e => e.RepresentationId == id && e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath)
                        .Select(e => e.Locator.AbsolutePath).FirstOrDefault(File.Exists)), token, id => intents[id].ReplicaId,
                async (manifest, ct) =>
                {
                    var intent = intents[manifest.RepresentationId];
                    if (intent.Manifest is { } previous && !HistoryCloudReplicaValidity.Matches(manifest, previous))
                        throw new InvalidDataException("[cloud.payload-changed] Payload differs from the durable upload intent.");
                    intents[manifest.RepresentationId] = intent with { Manifest = manifest };
                    await using var gate = await runtime.MutationGate.EnterAsync(ct).ConfigureAwait(false);
                    job = job with { UploadIntents = [.. intents.Values] }; runtime.CloudJobs.Update(job);
                }).ConfigureAwait(false);
            if (result.Complete)
            {
                context.TargetGuard();
                await using var gate = await runtime.MutationGate.EnterAsync(CancellationToken.None).ConfigureAwait(false);
                job = job with { Phase = CloudJobPhase.Succeeded, Protection = CloudProtectionDisposition.Released };
                runtime.CloudJobs.Update(job); // 成功终态与解除保护一并原子持久化。
            }
            else
            {
                var diagnostic = result.Canceled ? "cloud.job-canceled" : string.Join("\n", result.Items.Where(i => i.State == HistoryCloudItemState.Failed).Select(i => i.Diagnostic));
                if (result.Missing.Count > 0) diagnostic += "\n[cloud.payload-missing] " + string.Join(", ", result.Missing);
                if (result.Metadata?.Succeeded == false) diagnostic += "\n[cloud.metadata-sync-failed] " + result.Metadata.Diagnostic;
                RecordJobFailure(runtime, job, diagnostic, result.Canceled);
            }
            await RunOnUIAsync(() =>
            {
                task.IsCompleted = true; task.IsSuccess = result.Complete; task.IsIndeterminate = false;
                task.Status = I18n.GetString(result.Complete ? "CloudSetup_UploadComplete" : "CloudJob_ProtectedPending");
                task.ErrorMessage = result.Complete ? "" : task.Status;
            }).ConfigureAwait(false);
            if (result.Complete) NotificationService.ShowSuccess(I18n.GetString("CloudSetup_UploadComplete"));
            else if (job.Attempt == 1 || runtime.CloudJobs.Read().Jobs.Single(j => j.JobId == job.JobId).Phase == CloudJobPhase.NeedsAttention)
                NotificationService.ShowWarning(I18n.GetString("CloudJob_ProtectedPending"));
            return result;
        }
        catch (Exception ex)
        {
            var diagnostic = CloudCommandSecurity.Redact(ex.Message);
            try { RecordJobFailure(runtime, job, diagnostic, token.IsCancellationRequested); }
            catch (Exception journal) { runtime.CloudJobs.ObserveFailure(journal); }
            if (task is not null) await RunOnUIAsync(() => { task.IsCompleted = true; task.IsSuccess = false; task.IsIndeterminate = false; task.Status = I18n.GetString("CloudJob_ProtectedPending"); task.ErrorMessage = diagnostic; }).ConfigureAwait(false);
            LogService.LogWarning("JobId: " + job.JobId + " " + diagnostic, nameof(CloudSyncService));
            if (job.Attempt == 1 || job.Attempt >= 5) NotificationService.ShowWarning(I18n.GetString("CloudJob_ProtectedPending"));
            return Incomplete(diagnostic);
        }
        finally { CommandSemaphore.Release(); }
    }
    private static void RecordJobFailure(HistoryRuntime runtime, HistoryCloudJob job, string diagnostic, bool canceled)
    {
        var retryable = !canceled && job.Attempt < 5 && !diagnostic.Contains("[connection.", StringComparison.Ordinal)
            && !diagnostic.Contains("[cloud.target-changed]", StringComparison.Ordinal) && !diagnostic.Contains("[cloud.manifest-invalid]", StringComparison.Ordinal)
            && !diagnostic.Contains("[cloud.payload-missing]", StringComparison.Ordinal) && !diagnostic.Contains("[cloud.payload-changed]", StringComparison.Ordinal);
        runtime.CloudJobs.Update(job with
        {
            Phase = canceled ? CloudJobPhase.Canceled : retryable ? CloudJobPhase.RetryableFailure : CloudJobPhase.NeedsAttention,
            NextEligibleUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Min(900, 5 * Math.Pow(2, job.Attempt)) + Random.Shared.NextDouble() * 3),
            Diagnostic = diagnostic
        });
    }
}

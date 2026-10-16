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
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> JobCancellation = new(StringComparer.Ordinal);
    private static readonly System.Collections.Generic.HashSet<string> CloudWakeRequests = new(StringComparer.Ordinal);
    private static readonly CancellationTokenSource CloudStopping = new();

    internal static CloudJobTarget FreezeTarget(BackupConfig config)
    {
        var settings = config.Cloud;
        var profile = ConfigService.CurrentConfig.CloudConnections.FirstOrDefault(p => p.Id == settings.ConnectionProfileId);
        var credential = profile?.CredentialReference ?? settings.RcloneConfigPath;
        var root = profile is null ? settings.RemoteBasePath : profile.RemoteName + ":" + settings.RemotePathSuffix;
        var runtimeSettings = ConfigSnapshotMapper.Copy(settings);
        if (profile is not null) { runtimeSettings.ExecutablePath = profile.ExecutablePath; runtimeSettings.WorkingDirectory = profile.WorkingDirectory; }
        var working = runtimeSettings.WorkingDirectory;
        if (TryResolveSharedRcloneRuntime(runtimeSettings, out _, out var effectiveWorking, out _)) working = effectiveWorking;
        var revision = "";
        var credentialRevision = "";
        try
        {
            if (Path.IsPathFullyQualified(credential) && File.Exists(credential))
            {
                var bytes = RcloneExecutionContext.ReadConfigurationBytes(credential);
                revision = RcloneCredentialRefresh.TargetRevision(bytes);
                credentialRevision = Convert.ToHexString(SHA256.HashData(bytes));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { /* 缺凭据也先保留接纳证据；不把本地成功变成没有 Job。解析/认证失败由执行阶段明确暂停。 */ }
        return new(settings.ConnectionProfileId ?? "legacy", profile?.ProfileRevision ?? revision,
            settings.BindingRevision, root, new HistoryConfigId(config.Id).Value, revision,
            Convert.ToHexString(SHA256.HashData(HistoryRepositoryDescriptor.Create(new(config.Id)).ToCanonicalBytes())), credentialRevision, working);
    }

    internal static async Task PrepareCloudAdmissionAsync(BackupConfig config, HistoryRuntime runtime, CancellationToken token)
    {
        await using var gate = await runtime.MutationGate.EnterAsync(token).ConfigureAwait(false);
        try
        {
            // 仅接纳此机器今后的备份提交；升级、开启开关不自动追认旧 History 或导入 Pack。
            runtime.CloudJobs.ConfigureAdmission(config.Cloud.Enabled && config.Cloud.WritableEvidence.Length != 0
                && !MigrationSetupStateStore.IsCloudPending(ConfigService.ConfigDirectory, config.Id)
                ? FreezeTarget(config) : null);
        }
        catch (Exception ex) { runtime.CloudJobs.ObserveFailure(ex); }
    }
    internal static async Task InitializeMissingCloudAdmissionAsync(BackupConfig config, CancellationToken token)
    {
        if (config.Cloud.WritableEvidence.Length == 0) throw new InvalidOperationException(I18n.GetString("CloudJob_WriteVerificationRequired"));
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        await using var gate = await runtime.MutationGate.EnterAsync(token).ConfigureAwait(false);
        runtime.CloudJobs.InitializeMissingAdmissionFromNow(FreezeTarget(config));
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
        current = ConfigService.CurrentConfig.BackupConfigs.SingleOrDefault(p => new HistoryConfigId(job.ProjectId).Matches(p.Id));
        if (current is null || current.Cloud.LegacyConversionRequired) return false;
        var target = FreezeTarget(current);
        if (target with { CredentialRevision = job.Target.CredentialRevision } != job.Target) return false;
        var settings = CloudConnectionCatalog.Resolve(current);
        return RcloneCredentialRefresh.AcceptsFrozenRevision(settings.RcloneConfigPath, job.Target.CredentialRevision,
            RcloneExecutionContext.ReadConfigurationBytes(settings.RcloneConfigPath));
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
                job = runtime.CloudJobs.Read().Jobs.FirstOrDefault(j => j.Protection == CloudProtectionDisposition.Held && (j.Phase is CloudJobPhase.Pending or CloudJobPhase.RetryableFailure)
                    && j.NextEligibleUtc <= DateTimeOffset.UtcNow);
            }
            catch (Exception ex) { LogService.LogWarning(CloudCommandSecurity.Redact(ex.Message), nameof(CloudSyncService)); return; }
            if (job is null)
            {
                if (!runtime.CloudJobs.Read().Jobs.Any(j => j.Protection == CloudProtectionDisposition.Held && (j.Phase is CloudJobPhase.Pending or CloudJobPhase.RetryableFailure))) return;
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
        if (job.Protection != CloudProtectionDisposition.Held) return;
        if (JobCancellation.ContainsKey(jobId) || job.Phase is CloudJobPhase.Resolving or CloudJobPhase.Uploading or CloudJobPhase.Verifying or CloudJobPhase.SyncingMetadata)
        {
            if (abandonProtection) throw new InvalidOperationException(I18n.GetString("CloudJob_RunningCannotRelease"));
            if (JobCancellation.TryGetValue(jobId, out var active)) active.Cancel();
            return;
        }
        runtime.CloudJobs.Update(job with { Phase = CloudJobPhase.Canceled,
            Protection = abandonProtection ? CloudProtectionDisposition.Abandoned : job.Protection });
        if (abandonProtection)
        {
            var config = ConfigService.CurrentConfig.BackupConfigs.Single(c => runtime.ConfigId.Matches(c.Id));
            await CompleteNativeJobTransfersAsync(config, runtime, jobId, token).ConfigureAwait(false);
        }
    }

    private static async Task<HistoryCloudBackupResult> ExecuteCloudJobAsync(HistoryRuntime runtime, HistoryCloudJob initial,
        CancellationToken token, string? explicitLocalPath = null)
    {
        HistoryCloudBackupResult Incomplete(string code) => new([], initial.Representations, null, token.IsCancellationRequested, DateTimeOffset.UtcNow);
        await CommandSemaphore.WaitAsync(token).ConfigureAwait(false);
        if (_automaticSyncStopping) { CommandSemaphore.Release(); return Incomplete("cloud.job-exit-pending"); }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = cancellation.Token; JobCancellation[initial.JobId] = cancellation;
        var job = initial;
        HistoryCloudBackupResult? delivered = null;
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
            var current = ConfigService.CurrentConfig.BackupConfigs.SingleOrDefault(c => new HistoryConfigId(job.ProjectId).Matches(c.Id));
            if (current is not null)
            {
                var credentials = CloudConnectionCatalog.Resolve(current).RcloneConfigPath;
                if (Path.IsPathFullyQualified(credentials) && File.Exists(credentials))
                    await RcloneCredentialRefresh.PrepareStoreAsync(credentials, ConfigService.RcloneTasksDirectory, CloudConnectionCatalog.IsManaged(credentials), token).ConfigureAwait(false);
            }
            if (!IsFrozenTargetCurrent(job, out current) || current is null)
            {
                await UpdateCloudJobAsync(runtime, job with { Phase = CloudJobPhase.NeedsAttention, Diagnostic = "cloud.target-changed" }).ConfigureAwait(false);
                return Incomplete("cloud.target-changed");
            }
            if (job.Automatic && !current.Cloud.Enabled)
            {
                await UpdateCloudJobAsync(runtime, job with { Phase = CloudJobPhase.Paused, Diagnostic = "cloud.job-paused" }).ConfigureAwait(false); return Incomplete("cloud.job-paused");
            }
            RequireMigrationCloudPermission(current);
            var profile = ConfigService.CurrentConfig.CloudConnections.SingleOrDefault(p => p.Id == job.Target.ProfileId);
            using var dependency = profile?.DependsOnLocalOpenList == true ? await OpenListRuntimeService.AcquireDependencyAsync(token).ConfigureAwait(false) : null;
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
            await runtime.EnsureIndexCurrentAsync(token).ConfigureAwait(false);
            var graph = (await runtime.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false)).ToDictionary(r => r.RepresentationId);
            var closure = job.Representations.SelectMany(id => ExactReplicaPreparation.Closure(graph[id], graph)).Select(r => r.RepresentationId).Distinct().ToArray();
            var intents = job.UploadIntents.ToDictionary(i => i.RepresentationId);
            foreach (var id in closure) if (!intents.ContainsKey(id)) intents.Add(id, new(id, ReplicaId.New(), null));
            job = job with { UploadIntents = [.. intents.Values], Phase = CloudJobPhase.Uploading };
            await UpdateCloudJobAsync(runtime, job).ConfigureAwait(false); // 稳定 ReplicaId 必须先于对应远端写入落盘。
            var metadata = new HistoryMetadataSyncService(runtime, CreateHistoryTransport(config));
            var transport = new RcloneNativeHistoryTransport(context, config.Cloud.TimeoutSeconds);
            async Task ObservePhaseAsync(CloudJobPhase phase, CancellationToken ct)
            {
                await using (var gate = await runtime.MutationGate.EnterAsync(ct).ConfigureAwait(false))
                {
                    job = job with { Phase = phase };
                    runtime.CloudJobs.Update(job);
                }
                await RunOnUIAsync(() => task.Status = I18n.GetString("CloudJob_Phase_" + phase)).ConfigureAwait(false);
            }
            transport.ObserveJobPhase = ObservePhaseAsync;
            var descriptor = await transport.ReadDescriptorAsync(runtime.ConfigId, token).ConfigureAwait(false);
            if (descriptor is not null && Convert.ToHexString(SHA256.HashData(HistoryRepositoryDescriptor.Parse(descriptor).ToCanonicalBytes())) != job.Target.DescriptorSha256)
                throw new InvalidDataException("[cloud.history-conflict] Remote repository identity differs.");
            var service = new HistoryCloudBackupService(runtime, transport, async ct =>
            {
                await ObservePhaseAsync(CloudJobPhase.SyncingMetadata, ct).ConfigureAwait(false);
                return await metadata.SyncAsync(ct).ConfigureAwait(false);
            });
            var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
            Task<string?> LocalPath(RepresentationId id, CancellationToken _) => Task.FromResult(
                explicitLocalPath is not null && job.Representations.Length == 1 && job.Representations[0] == id ? explicitLocalPath
                    : catalog?.Entries.Where(e => e.RepresentationId == id && e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath)
                        .Select(e => e.Locator.AbsolutePath).FirstOrDefault(File.Exists));
            HistoryReplicaPayloadProtocol payloads = new(LocalPath);
            if (closure.Any(id => graph[id].Kind == RepresentationKind.PluginArtifact)) payloads = new NativeArtifactReplicaService(Plugins.V3.NativeArtifactHostService.Store(config),
                new HistoryReplicaSyncService(runtime, transport, ct => metadata.SyncAsync(ct), new UploadOnlyRetirementGuard()))
                .ForJob(runtime, job, LocalPath, config.Id);
            var result = await service.UploadClosureAsync(job.Representations, LocalPath, token, id => intents[id].ReplicaId,
                async (manifest, ct) =>
                {
                    var intent = intents[manifest.RepresentationId];
                    if (intent.Manifest is { } previous && !HistoryCloudReplicaValidity.Matches(manifest, previous))
                        throw new InvalidDataException("[cloud.payload-changed] Payload differs from the durable upload intent.");
                    intents[manifest.RepresentationId] = intent with { Manifest = manifest };
                    await using var gate = await runtime.MutationGate.EnterAsync(ct).ConfigureAwait(false);
                    job = job with { UploadIntents = [.. intents.Values] }; runtime.CloudJobs.Update(job);
                }, payloads).ConfigureAwait(false);
            if (result.Complete)
            {
                context.TargetGuard();
                await using var gate = await runtime.MutationGate.EnterAsync(CancellationToken.None).ConfigureAwait(false);
                job = job with { Phase = CloudJobPhase.Succeeded, Protection = CloudProtectionDisposition.Released };
                runtime.CloudJobs.Update(job); // 成功终态与解除保护一并原子持久化。
                delivered = result;
                try { await CompleteNativeJobTransfersAsync(config, runtime, job.JobId, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception cleanup) { LogService.LogWarning(CloudCommandSecurity.Redact(cleanup.Message), nameof(CloudSyncService)); }
            }
            else
            {
                var diagnostic = result.Canceled ? "cloud.job-canceled" : string.Join("\n", result.Items.Where(i => i.State == HistoryCloudItemState.Failed).Select(i => i.Diagnostic));
                if (result.Missing.Count > 0) diagnostic += "\n[cloud.payload-missing] " + string.Join(", ", result.Missing);
                if (result.Metadata?.Succeeded == false) diagnostic += "\n[cloud.metadata-sync-failed] " + result.Metadata.Diagnostic;
                await RecordJobFailureAsync(runtime, job, diagnostic, result.Canceled).ConfigureAwait(false);
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
            if (delivered is not null)
            {
                LogService.LogWarning("JobId: " + job.JobId + " " + diagnostic, nameof(CloudSyncService));
                return delivered;
            }
            try { await RecordJobFailureAsync(runtime, job, diagnostic, token.IsCancellationRequested).ConfigureAwait(false); }
            catch (Exception journal) { runtime.CloudJobs.ObserveFailure(journal); }
            if (task is not null) await RunOnUIAsync(() => { task.IsCompleted = true; task.IsSuccess = false; task.IsIndeterminate = false; task.Status = I18n.GetString("CloudJob_ProtectedPending"); task.ErrorMessage = diagnostic; }).ConfigureAwait(false);
            LogService.LogWarning("JobId: " + job.JobId + " " + diagnostic, nameof(CloudSyncService));
            if (job.Attempt == 1 || job.Attempt >= 5) NotificationService.ShowWarning(I18n.GetString("CloudJob_ProtectedPending"));
            return Incomplete(diagnostic);
        }
        finally { JobCancellation.TryRemove(initial.JobId, out _); CommandSemaphore.Release(); }
    }
    private static Task CompleteNativeJobTransfersAsync(BackupConfig config, HistoryRuntime runtime, string jobId, CancellationToken token)
    {
        if (!System.IO.Directory.Exists(Plugin.Runtime.Artifacts.NativeArtifactStore.ForConfig(config.DestinationPath, config.Id))) return Task.CompletedTask;
        return NativeArtifactReplicaService.CompleteJobAsync(Plugins.V3.NativeArtifactHostService.Store(config), jobId, token);
    }

    private static async Task UpdateCloudJobAsync(HistoryRuntime runtime, HistoryCloudJob job)
    {
        await using var gate = await runtime.MutationGate.EnterAsync(CancellationToken.None).ConfigureAwait(false);
        runtime.CloudJobs.Update(job);
    }
    private static async Task RecordJobFailureAsync(HistoryRuntime runtime, HistoryCloudJob job, string diagnostic, bool canceled)
    {
        await using var gate = await runtime.MutationGate.EnterAsync(CancellationToken.None).ConfigureAwait(false);
        job = runtime.CloudJobs.Read().Jobs.Single(j => j.JobId == job.JobId);
        if (job.Protection != CloudProtectionDisposition.Held) return;
        var current = ConfigService.CurrentConfig.BackupConfigs.SingleOrDefault(c => new HistoryConfigId(job.ProjectId).Matches(c.Id));
        var maximumAttempts = Math.Clamp(current?.Cloud.RetryCount ?? 0, 0, 5) + 1;
        var paused = job.Automatic && current?.Cloud.Enabled == false;
        var retryable = !canceled && job.Attempt < maximumAttempts && !diagnostic.Contains("[connection.", StringComparison.Ordinal)
            && !diagnostic.Contains("[cloud.target-changed]", StringComparison.Ordinal) && !diagnostic.Contains("[cloud.manifest-invalid]", StringComparison.Ordinal)
            && !diagnostic.Contains("[cloud.payload-missing]", StringComparison.Ordinal) && !diagnostic.Contains("[cloud.payload-changed]", StringComparison.Ordinal);
        runtime.CloudJobs.Update(job with
        {
            Phase = paused ? CloudJobPhase.Paused : canceled ? CloudJobPhase.Canceled : retryable ? CloudJobPhase.RetryableFailure : CloudJobPhase.NeedsAttention,
            NextEligibleUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Min(900, 5 * Math.Pow(2, job.Attempt)) + Random.Shared.NextDouble() * 3),
            Diagnostic = diagnostic
        });
    }
}

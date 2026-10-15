using FolderRewind.History.Application;
using FolderRewind.History.Cloud;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static partial class CloudSyncService
    {
        private static readonly object AutomaticSyncLock = new();
        private static volatile bool _automaticSyncStopping;

        internal static async Task DrainForExitAsync()
        {
            Task[] active;
            lock (AutomaticSyncLock) { _automaticSyncStopping = true; active = CloudWorkers.Values.ToArray(); }
            await Task.WhenAll(active).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (!await CommandSemaphore.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false))
                throw new TimeoutException("Wait for active cloud transfers before restarting.");
            CommandSemaphore.Release();
        }
        internal static void ResumeAfterExitFailure()
        {
            lock (AutomaticSyncLock) _automaticSyncStopping = false;
            foreach (var config in ConfigService.CurrentConfig.BackupConfigs) ResumeCloudJobs(config);
        }
        public static void QueueNativeHistorySync(BackupConfig? config, HistoryCommitBatch batch)
        {
            if (config is null) return;
            // Job 已在 History durable boundary 前准备；此处只唤醒持久队列，不再登记意图。
            ResumeCloudJobs(config);
        }

        public static async Task<bool> UploadRepresentationAsync(
            BackupConfig config,
            RepresentationId representationId,
            string localPath,
            CancellationToken cancellationToken = default)
        {
            try
            {
                RequireMigrationCloudPermission(config);
                config = await PrepareConnectionAsync(config, cancellationToken).ConfigureAwait(false);
                using var connection = CaptureConnection(config);
                var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
                var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Value;
                if (catalog is null || !catalog.Entries.Any(e => e.RepresentationId == representationId
                    && e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath
                    && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(e.Locator.AbsolutePath), Path.GetFullPath(localPath))))
                    throw new InvalidDataException("[cloud.payload-not-registered] " + I18n.GetString("CloudJob_RegistrationNeedsAttention"));
                var packs = await runtime.Repository.ReadAllPacksAsync(cancellationToken).ConfigureAwait(false);
                var pack = packs.First(p => p.Pack.Objects.Any(o => o.Kind == HistoryObjectKinds.VersionRepresentation && o.Id == representationId.ToString())).Pack;
                HistoryCloudJob job;
                await using (var gate = await runtime.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false))
                    job = runtime.CloudJobs.RegisterManual(pack, FreezeTarget(config), [representationId]);
                var result = await ExecuteCloudJobAsync(runtime, job, cancellationToken, localPath).ConfigureAwait(false);
                return result.Complete;
            }
            catch (Exception ex)
            {
                LogService.LogWarning(
                    "[Cloud Replica] " + CloudCommandSecurity.Redact(ex.Message),
                    nameof(CloudSyncService));
                return false;
            }
        }

        public static async Task<bool> DownloadRepresentationAsync(
            BackupConfig config,
            ManagedFolder folder,
            RepresentationId representationId,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            try
            {
                config = await PrepareConnectionAsync(config, cancellationToken).ConfigureAwait(false);
                using var connection = CaptureConnection(config);
                var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
                var replicas = await runtime.Query.GetStorageReplicasAsync(representationId, cancellationToken)
                    .ConfigureAwait(false);
                StorageReplica? replica = null;
                foreach (var candidate in replicas
                             .Where(item => item.ProviderKind == ReplicaProviderKind.Cloud
                                 && item.ExpectedSize is not null
                                 && !string.IsNullOrWhiteSpace(item.ExpectedStorageSha256))
                             .OrderBy(item => item.ReplicaId.ToString(), StringComparer.Ordinal))
                {
                    var lifecycle = await runtime.Query.GetReplicaLifecycleUpdatesAsync(
                        candidate.ReplicaId,
                        cancellationToken).ConfigureAwait(false);
                    var parentIds = lifecycle.SelectMany(item => item.ParentUpdateIds).ToHashSet();
                    if (lifecycle.Where(item => !parentIds.Contains(item.UpdateId))
                        .Any(item => item.State == ReplicaLifecycleState.Active))
                    {
                        replica = candidate;
                        break;
                    }
                }
                if (replica is null) return false;
                var expectedSize = replica.ExpectedSize
                    ?? throw new InvalidDataException("Cloud replica metadata does not include an expected size.");
                var expectedStorageSha256 = replica.ExpectedStorageSha256
                    ?? throw new InvalidDataException("Cloud replica metadata does not include an expected storage hash.");
                if (!BackupStoragePathService.TryResolveBackupStoragePaths(
                        config.DestinationPath,
                        folder.DisplayName,
                        folder.Path,
                        out _,
                        out var backupDirectory,
                        out _)) return false;
                Directory.CreateDirectory(backupDirectory);
                var safeName = Path.GetFileName(fileName);
                if (string.IsNullOrWhiteSpace(safeName) || !StringComparer.Ordinal.Equals(safeName, fileName)) return false;
                var manifest = new HistoryReplicaManifest(
                    representationId,
                    replica.ReplicaId,
                    null,
                    replica.ObjectKey,
                    expectedSize,
                    expectedStorageSha256);
                var service = new HistoryReplicaSyncService(
                    runtime,
                    CreateReplicaTransport(config),
                    token => new HistoryMetadataSyncService(runtime, CreateHistoryTransport(config)).SyncAsync(token),
                    new UploadOnlyRetirementGuard());
                var result = await service.DownloadAsync(
                    manifest,
                    Path.Combine(backupDirectory, safeName),
                    cancellationToken).ConfigureAwait(false);
                return result.Status == HistoryReplicaOperationStatus.Succeeded;
            }
            catch (Exception ex)
            {
                LogService.LogWarning(
                    "[Cloud Replica] " + CloudCommandSecurity.Redact(ex.Message),
                    nameof(CloudSyncService));
                return false;
            }
        }

        private static IHistoryReplicaTransport CreateReplicaTransport(BackupConfig config)
            => new RcloneNativeHistoryTransport(RcloneExecutionScope.Current
                ?? throw new InvalidOperationException(I18n.GetString("CloudSetup_ExplicitConfigRequired")), config.Cloud.TimeoutSeconds);

        private sealed class UploadOnlyRetirementGuard : IHistoryReplicaRetirementGuard
        {
            public Task EnsureRetirementSafeAsync(
                StorageReplica replica,
                bool releaseVersion,
                CancellationToken cancellationToken)
                => throw new InvalidOperationException("Replica retirement requires an explicit retention workflow.");
        }

    }
}

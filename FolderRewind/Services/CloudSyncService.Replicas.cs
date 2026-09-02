using FolderRewind.History.Application;
using FolderRewind.History.Cloud;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
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
        public static void QueueNativeHistorySync(
            BackupConfig? config,
            IEnumerable<RepresentationId> representationIds)
        {
            var ids = representationIds?.Distinct().ToArray() ?? [];
            if (config?.Cloud?.Enabled != true || ids.Length == 0 || !CanUseManualCloudActions(config)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config).ConfigureAwait(false);
                    var metadataTransport = CreateHistoryTransport(config);
                    var metadata = new HistoryMetadataSyncService(runtime, metadataTransport);
                    var replicas = new HistoryReplicaSyncService(
                        runtime,
                        CreateReplicaTransport(config),
                        token => metadata.SyncAsync(token),
                        new UploadOnlyRetirementGuard());
                    var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync().ConfigureAwait(false)).Value;
                    foreach (var representationId in ids)
                    {
                        var path = catalog?.Entries
                            .Where(item => item.RepresentationId == representationId
                                && item.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath)
                            .Select(item => item.Locator.AbsolutePath)
                            .FirstOrDefault(File.Exists);
                        if (path is null) continue;
                        var result = await replicas.UploadAsync(representationId, path).ConfigureAwait(false);
                        if (result.Status != HistoryReplicaOperationStatus.Succeeded)
                            LogService.LogWarning("[Cloud Replica] " + result.Diagnostic, nameof(CloudSyncService));
                    }
                    if (config.Cloud.SyncHistoryAfterUpload)
                        _ = await metadata.SyncAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogService.LogWarning("[Cloud Replica] " + ex.Message, nameof(CloudSyncService));
                }
            });
        }

        public static async Task<bool> UploadRepresentationAsync(
            BackupConfig config,
            RepresentationId representationId,
            string localPath,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
                var metadata = new HistoryMetadataSyncService(runtime, CreateHistoryTransport(config));
                var service = new HistoryReplicaSyncService(
                    runtime,
                    CreateReplicaTransport(config),
                    token => metadata.SyncAsync(token),
                    new UploadOnlyRetirementGuard());
                var result = await service.UploadAsync(
                    representationId,
                    localPath,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return result.Status == HistoryReplicaOperationStatus.Succeeded;
            }
            catch (Exception ex)
            {
                LogService.LogWarning("[Cloud Replica] " + ex.Message, nameof(CloudSyncService));
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
                LogService.LogWarning("[Cloud Replica] " + ex.Message, nameof(CloudSyncService));
                return false;
            }
        }

        private static RcloneHistoryReplicaTransport CreateReplicaTransport(BackupConfig config)
        {
            if (!TryResolveSharedRcloneRuntime(
                    config.Cloud,
                    out var executable,
                    out var workingDirectory,
                    out var error))
                throw new InvalidOperationException(error);
            return new(
                executable,
                workingDirectory,
                config.Cloud ?? new CloudSettings(),
                config.Cloud?.RemoteBasePath ?? GetSuggestedRemoteBasePath());
        }

        private sealed class UploadOnlyRetirementGuard : IHistoryReplicaRetirementGuard
        {
            public Task EnsureRetirementSafeAsync(
                StorageReplica replica,
                bool releaseVersion,
                CancellationToken cancellationToken)
                => throw new InvalidOperationException("Replica retirement requires an explicit retention workflow.");
        }

        private sealed class RcloneHistoryReplicaTransport : IHistoryReplicaTransport
        {
            private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
            private readonly string _executable;
            private readonly string _workingDirectory;
            private readonly CloudSettings _settings;
            private readonly string _remoteBasePath;

            public RcloneHistoryReplicaTransport(
                string executable,
                string workingDirectory,
                CloudSettings settings,
                string remoteBasePath)
            {
                _executable = executable;
                _workingDirectory = workingDirectory;
                _settings = settings;
                _remoteBasePath = remoteBasePath;
            }

            public Task UploadAsync(ReplicaId replicaId, string localPath, CancellationToken cancellationToken)
                => CopyToAsync(localPath, Payload(replicaId), immutable: true, cancellationToken);

            public async Task<HistoryReplicaVerification> VerifyRemoteAsync(
                ReplicaId replicaId,
                CancellationToken cancellationToken)
            {
                var temporary = Temporary();
                try
                {
                    await CopyToAsync(Payload(replicaId), temporary, immutable: false, cancellationToken)
                        .ConfigureAwait(false);
                    await using var stream = File.OpenRead(temporary);
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                        .ToLowerInvariant();
                    return new(true, stream.Length, hash, string.Empty);
                }
                catch (Exception ex)
                {
                    return new(false, 0, string.Empty, ex.Message);
                }
                finally
                {
                    TryDeleteTempFile(temporary);
                }
            }

            public Task CommitManifestOnceAsync(
                HistoryReplicaManifest manifest,
                CancellationToken cancellationToken)
                => UploadBytesOnceAsync(
                    Manifest(manifest.ReplicaId),
                    JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions),
                    cancellationToken);

            public Task DownloadAsync(
                HistoryReplicaManifest manifest,
                string stagingPath,
                CancellationToken cancellationToken)
                => CopyToAsync(Payload(manifest.ReplicaId), stagingPath, immutable: false, cancellationToken);

            public async Task DeletePhysicalAsync(
                HistoryReplicaManifest manifest,
                CancellationToken cancellationToken)
            {
                await DeleteAsync(Payload(manifest.ReplicaId), cancellationToken).ConfigureAwait(false);
                await DeleteAsync(Manifest(manifest.ReplicaId), cancellationToken).ConfigureAwait(false);
            }

            private string Root(ReplicaId replicaId)
                => AppendRemotePath(
                    _remoteBasePath,
                    InternalCloudStateDirectoryName,
                    "replicas",
                    replicaId.ToString());

            private string Payload(ReplicaId replicaId) => AppendRemotePath(Root(replicaId), "payload");
            private string Manifest(ReplicaId replicaId) => AppendRemotePath(Root(replicaId), "manifest.json");

            private async Task UploadBytesOnceAsync(
                string remotePath,
                byte[] bytes,
                CancellationToken cancellationToken)
            {
                var temporary = Temporary();
                try
                {
                    await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                    await CopyToAsync(temporary, remotePath, immutable: true, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    TryDeleteTempFile(temporary);
                }
            }

            private async Task CopyToAsync(
                string source,
                string destination,
                bool immutable,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var arguments = BuildRcloneCopyToArguments(source, destination)
                    + (immutable ? " --immutable" : string.Empty);
                var result = await RunSilentCommandAsync(
                    CreateDirectCommand(_executable, _workingDirectory, arguments),
                    Math.Clamp(_settings.TimeoutSeconds, 10, MaxTimeoutSeconds)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!result.Success) throw new IOException(result.ErrorMessage);
            }

            private async Task DeleteAsync(string remotePath, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await RunSilentCommandAsync(
                    CreateDirectCommand(_executable, _workingDirectory, $"deletefile {Quote(remotePath)}"),
                    Math.Clamp(_settings.TimeoutSeconds, 10, MaxTimeoutSeconds)).ConfigureAwait(false);
                if (!result.Success) throw new IOException(result.ErrorMessage);
            }

            private static string Temporary()
                => Path.Combine(Path.GetTempPath(), $"FolderRewind-replica-{Guid.NewGuid():N}.tmp");
        }
    }
}

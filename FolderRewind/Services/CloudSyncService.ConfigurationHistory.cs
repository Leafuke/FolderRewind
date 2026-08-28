using FolderRewind.History.Application;
using FolderRewind.History.Cloud;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static partial class CloudSyncService
    {
        public static async Task<(bool Success, int RecoveredCount, string Message)> DownloadConfigurationHistoryAsync(
            BackupConfig? config)
        {
            var result = await SyncNativeHistoryAsync(config).ConfigureAwait(false);
            return (result.Success, result.Downloaded, result.Message);
        }

        public static async Task<ConfigCloudHistoryAnalysisResult> AnalyzeConfigurationHistoryAsync(
            BackupConfig? config)
        {
            if (config is null || !CanUseManualCloudActions(config))
                return new() { Success = false, Message = I18n.GetString("CloudSync_Notification_HistoryImportFailed") };
            try
            {
                var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
                var transport = CreateHistoryTransport(config);
                var local = (await runtime.Repository.ReadAllPacksAsync().ConfigureAwait(false))
                    .Select(item => item.Pack.PackId).ToHashSet();
                var remote = (await transport.ListPacksAsync(runtime.ConfigId, CancellationToken.None).ConfigureAwait(false))
                    .ToHashSet();
                return new()
                {
                    Success = true,
                    Message = "Commit Pack union is ready.",
                    TotalRemoteEntries = remote.Count,
                    MatchedEntries = remote.Count(local.Contains),
                    ImportableEntries = remote.Count(item => !local.Contains(item)),
                    UnmappedEntries = 0,
                    AmbiguousEntries = 0
                };
            }
            catch (Exception ex)
            {
                return new() { Success = false, Message = ex.Message };
            }
        }

        public static async Task<ConfigCloudSyncResult> SyncConfigurationFromCloudAsync(
            BackupConfig? config,
            ConfigCloudSyncMode mode)
        {
            var sync = await SyncNativeHistoryAsync(config).ConfigureAwait(false);
            return new()
            {
                Success = sync.Success,
                Message = sync.Message,
                ImportedHistoryCount = sync.Downloaded,
                DuplicateHistoryCount = 0,
                RecoveredBackupCount = 0,
                Analysis = await AnalyzeConfigurationHistoryAsync(config).ConfigureAwait(false)
            };
        }

        public static async Task<(bool Success, string Message)> ImportConfigFromCloudAsync(string remoteBasePath)
        {
            var remoteConfigPath = AppendRemotePath(remoteBasePath, "config.json");
            return await ImportJsonFromCloudAsync(
                remoteConfigPath,
                I18n.GetString("CloudSync_Task_ConfigImportName"),
                I18n.GetString("CloudSync_Notification_ConfigImportSucceeded"),
                I18n.GetString("CloudSync_Notification_ConfigImportFailed"),
                ConfigService.ImportConfig).ConfigureAwait(false);
        }

        public static async Task<(bool Success, string Message)> ExportConfigToCloudAsync(string remoteBasePath)
        {
            var remoteConfigPath = AppendRemotePath(remoteBasePath, "config.json");
            return await ExportJsonToCloudAsync(
                remoteConfigPath,
                I18n.GetString("CloudSync_Task_ConfigExportName"),
                I18n.GetString("CloudSync_Notification_ConfigExportSucceeded"),
                I18n.GetString("CloudSync_Notification_ConfigExportFailed"),
                ConfigService.ExportConfig).ConfigureAwait(false);
        }

        public static async Task<ConfigCloudHistoryUploadResult> UploadConfigurationHistoryAsync(
            BackupConfig? config,
            bool showNotifications = true)
        {
            var sync = await SyncNativeHistoryAsync(config).ConfigureAwait(false);
            if (showNotifications)
            {
                if (sync.Success) NotificationService.ShowSuccess(sync.Message);
                else NotificationService.ShowError(sync.Message);
            }
            return new()
            {
                Success = sync.Success,
                Message = sync.Message,
                UploadedEntryCount = sync.Uploaded,
                ReplacedRemoteEntryCount = 0
            };
        }

        private static async Task<(bool Success, int Downloaded, int Uploaded, string Message)> SyncNativeHistoryAsync(
            BackupConfig? config)
        {
            if (config is null || !CanUseManualCloudActions(config))
                return (false, 0, 0, I18n.GetString("CloudSync_Notification_HistoryImportFailed"));
            var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
            var result = await new HistoryMetadataSyncService(runtime, CreateHistoryTransport(config))
                .SyncAsync().ConfigureAwait(false);
            var message = result.Succeeded
                ? $"Commit Pack union synchronized: {result.DownloadedPacks} downloaded, {result.UploadedPacks} uploaded."
                : result.Diagnostic;
            return (result.Succeeded, result.DownloadedPacks, result.UploadedPacks, message);
        }

        private static RcloneHistoryMetadataTransport CreateHistoryTransport(BackupConfig config)
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

        private sealed class RcloneHistoryMetadataTransport : IHistoryMetadataTransport
        {
            private readonly string _executable;
            private readonly string _workingDirectory;
            private readonly CloudSettings _settings;
            private readonly string _remoteBasePath;

            public RcloneHistoryMetadataTransport(
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

            public async Task<byte[]?> ReadDescriptorAsync(
                HistoryConfigId configId,
                CancellationToken cancellationToken)
            {
                var path = AppendRemotePath(Root(configId), "repository.json");
                return await ExistsAsync(path, cancellationToken).ConfigureAwait(false)
                    ? await DownloadAsync(path, cancellationToken).ConfigureAwait(false)
                    : null;
            }

            public Task CreateDescriptorOnceAsync(
                HistoryConfigId configId,
                byte[] canonicalBytes,
                CancellationToken cancellationToken)
                => UploadOnceAsync(
                    AppendRemotePath(Root(configId), "repository.json"),
                    canonicalBytes,
                    cancellationToken);

            public async Task<IReadOnlyList<PackId>> ListPacksAsync(
                HistoryConfigId configId,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // List from the repository root because object-storage remotes do not
                // materialize an empty packs/ directory before the first upload.
                var root = Root(configId);
                var command = CreateDirectCommand(
                    _executable,
                    _workingDirectory,
                    $"lsf {Quote(root)} --files-only --recursive");
                var result = await RunSilentCommandAsync(
                    command,
                    Math.Clamp(_settings.TimeoutSeconds, 10, MaxTimeoutSeconds)).ConfigureAwait(false);
                if (!result.Success) throw new IOException(result.ErrorMessage);
                var ids = new List<PackId>();
                foreach (var line in result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var name = Path.GetFileName(line.Trim().Replace('/', Path.DirectorySeparatorChar));
                    if (!name.EndsWith(".frpack", StringComparison.OrdinalIgnoreCase)) continue;
                    try { ids.Add(PackId.Parse(Path.GetFileNameWithoutExtension(name))); }
                    catch (FormatException ex) { throw new HistoryIntegrityConflictException(ex.Message); }
                }
                return ids.Distinct().OrderBy(item => item.ToString(), StringComparer.Ordinal).ToArray();
            }

            public Task<byte[]> DownloadPackAsync(
                HistoryConfigId configId,
                PackId packId,
                CancellationToken cancellationToken)
                => DownloadAsync(PackPath(configId, packId), cancellationToken);

            public Task UploadPackOnceAsync(
                HistoryConfigId configId,
                PackId packId,
                byte[] bytes,
                CancellationToken cancellationToken)
                => UploadOnceAsync(PackPath(configId, packId), bytes, cancellationToken);

            public Task<bool> LegacyHistoryExistsAsync(
                HistoryConfigId configId,
                CancellationToken cancellationToken)
                => Task.FromResult(false);

            private string Root(HistoryConfigId configId)
                => AppendRemotePath(
                    _remoteBasePath,
                    InternalCloudStateDirectoryName,
                    "history",
                    HistoryRepositoryPaths.EncodeConfigPathSegment(configId));

            private string PackPath(HistoryConfigId configId, PackId packId)
            {
                var id = packId.ToString();
                return AppendRemotePath(Root(configId), "packs", id[..2], id + ".frpack");
            }

            private async Task<bool> ExistsAsync(string remotePath, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SplitRemotePath(remotePath, out var parent, out var name);
                var command = CreateDirectCommand(
                    _executable,
                    _workingDirectory,
                    BuildRcloneListFileArguments(parent));
                var result = await RunSilentCommandAsync(
                    command,
                    Math.Clamp(_settings.TimeoutSeconds, 10, MaxTimeoutSeconds)).ConfigureAwait(false);
                return result.Success && result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Any(item => StringComparer.Ordinal.Equals(item.Trim(), name));
            }

            private async Task<byte[]> DownloadAsync(string remotePath, CancellationToken cancellationToken)
            {
                var temporary = Path.Combine(Path.GetTempPath(), $"FolderRewind-pack-{Guid.NewGuid():N}.tmp");
                try
                {
                    var command = CreateDirectCommand(
                        _executable,
                        _workingDirectory,
                        BuildRcloneCopyToArguments(remotePath, temporary));
                    var result = await RunSilentCommandAsync(
                        command,
                        Math.Clamp(_settings.TimeoutSeconds, 10, MaxTimeoutSeconds)).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!result.Success || !File.Exists(temporary))
                        throw new IOException(result.ErrorMessage);
                    return await File.ReadAllBytesAsync(temporary, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    TryDeleteTempFile(temporary);
                }
            }

            private async Task UploadOnceAsync(
                string remotePath,
                byte[] bytes,
                CancellationToken cancellationToken)
            {
                var temporary = Path.Combine(Path.GetTempPath(), $"FolderRewind-pack-{Guid.NewGuid():N}.tmp");
                try
                {
                    await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                    var command = CreateDirectCommand(
                        _executable,
                        _workingDirectory,
                        BuildRcloneCopyToArguments(temporary, remotePath) + " --immutable");
                    var result = await RunSilentCommandAsync(
                        command,
                        Math.Clamp(_settings.TimeoutSeconds, 10, MaxTimeoutSeconds)).ConfigureAwait(false);
                    if (result.Success) return;
                    var existing = await DownloadAsync(remotePath, cancellationToken).ConfigureAwait(false);
                    if (!existing.AsSpan().SequenceEqual(bytes))
                        throw new HistoryIntegrityConflictException("Remote create-once object exists with different bytes.");
                }
                finally
                {
                    TryDeleteTempFile(temporary);
                }
            }

            private static void SplitRemotePath(string remotePath, out string parent, out string name)
            {
                var separator = remotePath.LastIndexOf('/');
                if (separator < 0) throw new ArgumentException("Remote path has no parent.", nameof(remotePath));
                parent = remotePath[..separator];
                name = remotePath[(separator + 1)..];
            }
        }
    }
}

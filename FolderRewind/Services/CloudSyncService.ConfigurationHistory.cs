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
        public static async Task<HistoryMetadataSyncResult> PullConfigurationHistoryAsync(BackupConfig config, CancellationToken token = default)
        {
            config = await PrepareConnectionAsync(config, token).ConfigureAwait(false);
            using var dependency = await AcquireHistoryDependencyAsync(config, token).ConfigureAwait(false);
            using var connection = CaptureConnection(config);
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
            return await new HistoryMetadataSyncService(runtime, CreateHistoryTransport(config)).PullAsync(token).ConfigureAwait(false);
        }
        public static async Task<(bool Success, int RecoveredCount, string Message)> DownloadConfigurationHistoryAsync(
            BackupConfig? config)
        {
            var result = await SyncNativeHistoryAsync(config, allowUploads: false).ConfigureAwait(false);
            return (result.Success, result.Downloaded, result.Message);
        }

        public static async Task<ConfigCloudHistoryAnalysisResult> AnalyzeConfigurationHistoryAsync(
            BackupConfig? config)
        {
            if (config is null || !CanUseManualCloudActions(config))
                return new() { Success = false, Message = I18n.GetString("CloudSync_Notification_HistoryImportFailed") };
            try
            {
                config = await PrepareConnectionAsync(config).ConfigureAwait(false);
                using var dependency = await AcquireHistoryDependencyAsync(config, CancellationToken.None).ConfigureAwait(false);
                using var connection = CaptureConnection(config);
                var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config).ConfigureAwait(false);
                var transport = CreateHistoryTransport(config);
                var local = (await runtime.Repository.ReadAllPacksAsync().ConfigureAwait(false))
                    .Select(item => item.Pack.PackId).ToHashSet();
                var remote = (await transport.ListPacksAsync(runtime.ConfigId, CancellationToken.None).ConfigureAwait(false))
                    .ToHashSet();
                return new()
                {
                    Success = true,
                    Message = I18n.GetString("CloudHistory_AnalysisReady"),
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
            BackupConfig? config)
        {
            var sync = await SyncNativeHistoryAsync(config, allowUploads: false).ConfigureAwait(false);
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

        public static async Task<(bool Success, string Message)> ImportConfigFromCloudAsync(CloudSettings connectionSettings,
            Func<string, Task<ConfigImportResult>>? prepareImport = null, string? fileName = null)
        {
            var settings = await PrepareTransferConnectionAsync(connectionSettings).ConfigureAwait(false);
            using var connection = CaptureConnection(settings);
            var selected = fileName ?? "config.json";
            if (selected != "config.json" && (!selected.StartsWith("config-", StringComparison.Ordinal) || !selected.EndsWith(".json", StringComparison.Ordinal)
                || selected.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '.')))
                throw new InvalidDataException(I18n.GetString("CloudConfig_InvalidExport"));
            var remoteConfigPath = selected == "config.json" ? AppendRemotePath(settings.RemoteBasePath, selected)
                : AppendRemotePath(settings.RemoteBasePath, "_folderrewind", "config-exports", selected);
            return await ImportJsonFromCloudAsync(
                remoteConfigPath,
                I18n.GetString("CloudSync_Task_ConfigImportName"),
                I18n.GetString("CloudSync_Notification_ConfigImportSucceeded"),
                I18n.GetString("CloudSync_Notification_ConfigImportFailed"),
                prepareImport ?? (path => ConfigService.StageConfigImportAsync(path)), settings).ConfigureAwait(false);
        }

        public static async Task<(bool Success, string Message)> ExportConfigToCloudAsync(CloudSettings connectionSettings)
        {
            var settings = await PrepareTransferConnectionAsync(connectionSettings).ConfigureAwait(false);
            using var connection = CaptureConnection(settings);
            var fileName = "config-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N") + ".json";
            var remoteConfigPath = AppendRemotePath(settings.RemoteBasePath, "_folderrewind", "config-exports", fileName);
            return await ExportJsonToCloudAsync(
                remoteConfigPath,
                I18n.GetString("CloudSync_Task_ConfigExportName"),
                I18n.Format("CloudConfig_ExportCreated", fileName),
                I18n.GetString("CloudSync_Notification_ConfigExportFailed"),
                ConfigService.ExportConfig, settings).ConfigureAwait(false);
        }
        public static async Task<IReadOnlyList<string>> ListCloudConfigurationExportsAsync(CloudSettings input, CancellationToken token = default)
        {
            var settings = await PrepareTransferConnectionAsync(input).ConfigureAwait(false);
            using var scope = CaptureConnection(settings);
            var context = RcloneExecutionScope.Current ?? throw new InvalidOperationException(I18n.GetString("CloudSetup_ExplicitConfigRequired"));
            var names = new List<string>();
            try
            {
                await RcloneConnectionService.RunLinesAsync(context.CreateStartInfo(["lsf", CloudRemotePath.Append(context.RemoteRoot, "_folderrewind/config-exports"), "--files-only"]),
                    TimeSpan.FromSeconds(settings.TimeoutSeconds), token, line =>
                    {
                        if (line.StartsWith("config-", StringComparison.Ordinal) && line.EndsWith(".json", StringComparison.Ordinal)
                            && line.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.')) names.Add(line);
                        if (names.Count > 10000) throw new IOException(I18n.GetString("CloudSetup_OutputTruncated"));
                    }).ConfigureAwait(false);
            }
            catch (RcloneProcessException ex) when (ex.ExitCode is 3 or 4) { }
            return names.OrderDescending(StringComparer.Ordinal).Take(200).Append("config.json").ToArray();
        }

        private static CloudSettings FreezeGlobalTransferConnection(CloudSettings input)
        {
            ArgumentNullException.ThrowIfNull(input);
            if (input.CommandMode != CloudCommandMode.Rclone || string.IsNullOrWhiteSpace(input.RcloneConfigPath))
                throw new InvalidOperationException(I18n.GetString("CloudSetup_ExplicitConfigRequired"));
            return new CloudSettings
            {
                CommandMode = CloudCommandMode.Rclone, ExecutablePath = input.ExecutablePath,
                WorkingDirectory = input.WorkingDirectory, RcloneConfigPath = input.RcloneConfigPath,
                RemoteBasePath = input.RemoteBasePath, TimeoutSeconds = input.TimeoutSeconds, RetryCount = input.RetryCount
            };
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
            BackupConfig? config, bool allowUploads = true)
        {
            if (config is null || !CanUseManualCloudActions(config))
                return (false, 0, 0, I18n.GetString("CloudSync_Notification_HistoryImportFailed"));
            if (allowUploads) RequireMigrationCloudPermission(config);
            config = await PrepareConnectionAsync(config).ConfigureAwait(false);
            using var dependency = await AcquireHistoryDependencyAsync(config, CancellationToken.None).ConfigureAwait(false);
            using var connection = CaptureConnection(config);
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config).ConfigureAwait(false);
            var service = new HistoryMetadataSyncService(runtime, CreateHistoryTransport(config));
            var result = allowUploads ? await service.SyncAsync().ConfigureAwait(false) : await service.PullAsync().ConfigureAwait(false);
            var message = result.Succeeded
                ? I18n.Format("CloudHistory_MetadataSynchronized", result.DownloadedPacks, result.UploadedPacks)
                : result.Diagnostic;
            if (result.Succeeded && (result.DivergentBranches.Length != 0 || result.BranchNameCollisions.Length != 0))
                message += "\n" + I18n.Format("CloudHistory_Conflicts", result.DivergentBranches.Length, result.BranchNameCollisions.Length);
            return (result.Succeeded, result.DownloadedPacks, result.UploadedPacks, message);
        }

        private static Task<IDisposable?> AcquireHistoryDependencyAsync(BackupConfig config, CancellationToken token)
            => OpenListRuntimeService.AcquireForConnectionAsync(ConfigService.CurrentConfig.CloudConnections
                .Any(p => p.Id == config.Cloud.ConnectionProfileId && p.DependsOnLocalOpenList), token);

        private static IHistoryMetadataTransport CreateHistoryTransport(BackupConfig config)
            => new RcloneNativeHistoryTransport(RcloneExecutionScope.Current
                ?? throw new InvalidOperationException(I18n.GetString("CloudSetup_ExplicitConfigRequired")), config.Cloud.TimeoutSeconds);
    }
}

using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static partial class CloudSyncService
    {
        private static async Task<ConfigCloudHistoryAnalysisResult> AnalyzeConfigurationHistoryCoreAsync(BackupConfig? config)
        {
            if (config == null)
            {
                return new ConfigCloudHistoryAnalysisResult
                {
                    Success = false,
                    Message = I18n.GetString("CloudSync_Notification_ConfigurationDownloadFailed")
                };
            }

            if (!CanUseManualCloudActions(config))
            {
                return new ConfigCloudHistoryAnalysisResult
                {
                    Success = false,
                    Message = I18n.GetString("CloudSync_Notification_RcloneOnly")
                };
            }

            if (config.SourceFolders == null || config.SourceFolders.Count == 0)
            {
                return new ConfigCloudHistoryAnalysisResult
                {
                    Success = false,
                    Message = I18n.GetString("CloudSync_Notification_ConfigurationNoFolders")
                };
            }

            var remoteHistoryResult = await DownloadRemoteHistoryItemsAsync(config).ConfigureAwait(false);
            if (!remoteHistoryResult.Success)
            {
                return new ConfigCloudHistoryAnalysisResult
                {
                    Success = false,
                    Message = remoteHistoryResult.Message
                };
            }

            var remoteItems = remoteHistoryResult.Items;
            string remoteConfigRoot = AppendRemotePath(config.Cloud?.RemoteBasePath ?? string.Empty, config.Name ?? string.Empty);
            // 优先按 ConfigId 精确匹配；旧历史可能没有 ConfigId，再退化到远端路径前缀匹配。
            var exactMatches = remoteItems
                .Where(item => string.Equals(item.ConfigId, config.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var matchedItems = exactMatches.Count > 0
                ? exactMatches
                : remoteItems.Where(item => HistoryItemMatchesRemoteConfigRoot(item, remoteConfigRoot)).ToList();

            var activeManifest = await DownloadActiveHistoryManifestAsync(config).ConfigureAwait(false);
            if (activeManifest != null)
            {
                matchedItems = matchedItems
                    .Where(item => ManifestContainsHistoryItem(activeManifest, item))
                    .ToList();
            }

            var mappedItems = new List<HistoryItem>();
            int unmappedEntries = 0;
            int ambiguousEntries = 0;

            foreach (var remoteItem in matchedItems)
            {
                var mappedFolder = ResolveMappedFolder(config, remoteItem, out bool isAmbiguous);
                if (mappedFolder == null)
                {
                    if (isAmbiguous)
                    {
                        ambiguousEntries++;
                    }
                    else
                    {
                        unmappedEntries++;
                    }

                    continue;
                }

                mappedItems.Add(CloneMappedHistoryItem(config, mappedFolder, remoteItem));
            }

            string message = mappedItems.Count > 0
                ? I18n.Format("CloudSync_ConfigSync_AnalysisSummary", matchedItems.Count, mappedItems.Count, unmappedEntries, ambiguousEntries)
                : I18n.Format("CloudSync_ConfigSync_AnalysisEmpty", matchedItems.Count, unmappedEntries, ambiguousEntries);

            LogService.LogInfo(
                $"[CloudSyncService] Analyze config history: config={config.Name}, total={remoteItems.Count}, matched={matchedItems.Count}, mapped={mappedItems.Count}, unmapped={unmappedEntries}, ambiguous={ambiguousEntries}",
                nameof(CloudSyncService));

            return new ConfigCloudHistoryAnalysisResult
            {
                Success = true,
                Message = message,
                TotalRemoteEntries = remoteItems.Count,
                MatchedEntries = matchedItems.Count,
                ImportableEntries = mappedItems.Count,
                UnmappedEntries = unmappedEntries,
                AmbiguousEntries = ambiguousEntries,
                MappedItems = mappedItems
            };
        }

        private static async Task<(bool Success, string Message, List<HistoryItem> Items)> DownloadRemoteHistoryItemsAsync(BackupConfig config)
        {
            string remoteHistoryPath = AppendRemotePath(config.Cloud?.RemoteBasePath ?? string.Empty, "history.json");
            if (string.IsNullOrWhiteSpace(remoteHistoryPath))
            {
                return (false, I18n.GetString("CloudSync_Notification_HistoryImportFailed"), new List<HistoryItem>());
            }

            if (!TryResolveSharedRcloneRuntime(config.Cloud, out var executablePath, out var workingDirectory, out var errorMessage))
            {
                return (false, errorMessage, new List<HistoryItem>());
            }

            string tempFilePath = Path.Combine(Path.GetTempPath(), $"FolderRewind_cloud_history_analysis_{Guid.NewGuid():N}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(tempFilePath) ?? Path.GetTempPath());

            await CommandSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var command = CreateDirectCommand(executablePath, workingDirectory, BuildRcloneCopyToArguments(remoteHistoryPath, tempFilePath));
                var result = await RunSilentCommandAsync(command, Math.Clamp(config.Cloud?.TimeoutSeconds ?? 600, 10, MaxTimeoutSeconds)).ConfigureAwait(false);
                if (!result.Success || !File.Exists(tempFilePath))
                {
                    string message = string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? I18n.GetString("CloudSync_Notification_HistoryImportFailed")
                        : I18n.Format("CloudSync_Notification_ImportFailedWithReason", result.ErrorMessage);
                    LogService.LogWarning(I18n.Format("CloudSync_Log_CommandFailed", config.Name ?? string.Empty, message), nameof(CloudSyncService));
                    return (false, message, new List<HistoryItem>());
                }

                string json = await File.ReadAllTextAsync(tempFilePath).ConfigureAwait(false);
                var items = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListHistoryItem) ?? new List<HistoryItem>();
                return (true, string.Empty, items);
            }
            catch (Exception ex)
            {
                LogService.LogError($"[CloudSyncService] Failed to analyze remote history: {ex.Message}", nameof(CloudSyncService), ex);
                return (false, I18n.Format("CloudSync_Notification_ImportFailedWithReason", ex.Message), new List<HistoryItem>());
            }
            finally
            {
                CommandSemaphore.Release();
                TryDeleteTempFile(tempFilePath);
            }
        }

        private static async Task<List<HistoryItem>> DownloadRemoteHistoryItemsOptionalAsync(
            string executablePath,
            string workingDirectory,
            CloudSettings settings,
            string remoteHistoryPath,
            BackupTask task)
        {
            bool remoteExists = await RemoteFileExistsAsync(executablePath, workingDirectory, settings, remoteHistoryPath).ConfigureAwait(false);
            if (!remoteExists)
            {
                return new List<HistoryItem>();
            }

            string tempFilePath = Path.Combine(Path.GetTempPath(), $"FolderRewind_config_history_optional_{Guid.NewGuid():N}.json");
            try
            {
                var command = CreateDirectCommand(executablePath, workingDirectory, BuildRcloneCopyToArguments(remoteHistoryPath, tempFilePath));
                var result = await ExecuteCommandWithRetryAsync(
                    task,
                    settings,
                    command,
                    I18n.GetString("CloudSync_Task_DownloadingConfigurationHistory"),
                    "history.json").ConfigureAwait(false);
                if (!result.Success || !File.Exists(tempFilePath))
                {
                    LogService.LogWarning(I18n.Format("CloudSync_Log_CommandFailed", "history.json", result.ErrorMessage), nameof(CloudSyncService));
                    return new List<HistoryItem>();
                }

                string json = await File.ReadAllTextAsync(tempFilePath).ConfigureAwait(false);
                return JsonSerializer.Deserialize(json, AppJsonContext.Default.ListHistoryItem) ?? new List<HistoryItem>();
            }
            finally
            {
                TryDeleteTempFile(tempFilePath);
            }
        }

        private static async Task<CloudActiveHistoryManifest?> DownloadActiveHistoryManifestAsync(BackupConfig config)
        {
            string activeHistoryRemotePath = BuildActiveHistoryManifestRemotePath(config);
            if (string.IsNullOrWhiteSpace(activeHistoryRemotePath))
            {
                return null;
            }

            if (!TryResolveSharedRcloneRuntime(config.Cloud, out var executablePath, out var workingDirectory, out _))
            {
                return null;
            }

            string tempFilePath = Path.Combine(Path.GetTempPath(), $"FolderRewind_cloud_active_history_{Guid.NewGuid():N}.json");
            try
            {
                bool remoteExists = await RemoteFileExistsAsync(
                    executablePath,
                    workingDirectory,
                    config.Cloud,
                    activeHistoryRemotePath).ConfigureAwait(false);
                if (!remoteExists)
                {
                    return null;
                }

                var command = CreateDirectCommand(executablePath, workingDirectory, BuildRcloneCopyToArguments(activeHistoryRemotePath, tempFilePath));
                var result = await RunSilentCommandAsync(command, Math.Clamp(config.Cloud?.TimeoutSeconds ?? 600, 10, MaxTimeoutSeconds)).ConfigureAwait(false);
                if (!result.Success || !File.Exists(tempFilePath))
                {
                    LogService.LogWarning(
                        I18n.Format("CloudSync_Log_CommandFailed", ActiveHistoryManifestFileName, result.ErrorMessage),
                        nameof(CloudSyncService));
                    return null;
                }

                string json = await File.ReadAllTextAsync(tempFilePath).ConfigureAwait(false);
                return JsonSerializer.Deserialize(json, AppJsonContext.Default.CloudActiveHistoryManifest);
            }
            catch (Exception ex)
            {
                LogService.LogWarning($"[CloudSyncService] Failed to load active history manifest: {ex.Message}", nameof(CloudSyncService));
                return null;
            }
            finally
            {
                TryDeleteTempFile(tempFilePath);
            }
        }

        private static async Task<bool> RemoteFileExistsAsync(
            string executablePath,
            string workingDirectory,
            CloudSettings settings,
            string remoteFilePath)
        {
            if (string.IsNullOrWhiteSpace(remoteFilePath))
            {
                return false;
            }

            SplitRemotePath(remoteFilePath, out var parentPath, out var fileName);
            if (string.IsNullOrWhiteSpace(parentPath) || string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            var remoteFiles = await ListRemoteFilesAsync(executablePath, workingDirectory, settings, parentPath).ConfigureAwait(false);
            return remoteFiles.Any(name => string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase));
        }

        private static void SplitRemotePath(string remoteFilePath, out string parentPath, out string fileName)
        {

            parentPath = string.Empty;
            fileName = string.Empty;
            if (string.IsNullOrWhiteSpace(remoteFilePath))
            {
                return;
            }

            string normalized = remoteFilePath.Trim().TrimEnd('/');
            int lastSlash = normalized.LastIndexOf('/');
            if (lastSlash < 0)
            {
                fileName = normalized;
                return;
            }

            parentPath = normalized[..lastSlash];
            fileName = normalized[(lastSlash + 1)..];
        }

        private static bool HistoryItemMatchesRemoteConfigRoot(HistoryItem item, string remoteConfigRoot)
        {
            if (item == null || string.IsNullOrWhiteSpace(remoteConfigRoot))
            {
                return false;
            }

            return HasRemotePrefix(item.CloudArchiveRemotePath, remoteConfigRoot)
                || HasRemotePrefix(item.CloudMetadataRecordRemotePath, remoteConfigRoot)
                || HasRemotePrefix(item.CloudMetadataStateRemotePath, remoteConfigRoot);
        }

        private static bool HasRemotePrefix(string? value, string remoteConfigRoot)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(remoteConfigRoot))
            {
                return false;
            }

            string normalizedValue = value.Trim();
            string normalizedPrefix = remoteConfigRoot.Trim().TrimEnd('/') + "/";
            return normalizedValue.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedValue.TrimEnd('/'), remoteConfigRoot.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        private static bool BelongsToConfiguration(HistoryItem item, BackupConfig config, string remoteConfigRoot)
        {
            if (item == null || config == null)
            {
                return false;
            }

            if (string.Equals(item.ConfigId, config.Id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return HistoryItemMatchesRemoteConfigRoot(item, remoteConfigRoot);
        }

        private static ManagedFolder? ResolveMappedFolder(BackupConfig config, HistoryItem remoteItem, out bool isAmbiguous)
        {
            isAmbiguous = false;

            if (!string.IsNullOrWhiteSpace(remoteItem.FolderPath))
            {
                var exactFolder = config.SourceFolders.FirstOrDefault(folder =>
                    string.Equals(folder.Path, remoteItem.FolderPath, StringComparison.OrdinalIgnoreCase));
                if (exactFolder != null)
                {
                    return exactFolder;
                }
            }

            if (string.IsNullOrWhiteSpace(remoteItem.FolderName))
            {
                return null;
            }

            // 显示名可能重复，只有唯一命中才自动映射，避免把历史导入到错误目录。
            var displayNameMatches = config.SourceFolders
                .Where(folder => string.Equals(folder.DisplayName?.Trim(), remoteItem.FolderName.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (displayNameMatches.Count == 1)
            {
                return displayNameMatches[0];
            }

            if (displayNameMatches.Count > 1)
            {
                isAmbiguous = true;
            }

            return null;
        }

        private static HistoryItem CloneMappedHistoryItem(BackupConfig config, ManagedFolder folder, HistoryItem remoteItem)
        {
            bool hasCloudCopy = remoteItem.IsCloudArchived || !string.IsNullOrWhiteSpace(remoteItem.CloudArchiveRemotePath);
            return new HistoryItem
            {
                ConfigId = config.Id,
                FolderPath = folder.Path ?? string.Empty,
                FolderName = folder.DisplayName ?? remoteItem.FolderName ?? string.Empty,
                FileName = remoteItem.FileName ?? string.Empty,
                Timestamp = remoteItem.Timestamp,
                BackupType = remoteItem.BackupType ?? string.Empty,
                Comment = remoteItem.Comment ?? string.Empty,
                IsImportant = remoteItem.IsImportant,
                IsCloudArchived = hasCloudCopy,
                CloudArchivedAtUtc = remoteItem.CloudArchivedAtUtc,
                CloudArchiveRemotePath = remoteItem.CloudArchiveRemotePath ?? string.Empty,
                CloudMetadataRecordRemotePath = remoteItem.CloudMetadataRecordRemotePath ?? string.Empty,
                CloudMetadataStateRemotePath = remoteItem.CloudMetadataStateRemotePath ?? string.Empty
            };
        }

        private static HistoryItem CloneHistoryItemForCloudSync(HistoryItem item)
        {
            return new HistoryItem
            {
                ConfigId = item.ConfigId ?? string.Empty,
                FolderPath = item.FolderPath ?? string.Empty,
                FolderName = item.FolderName ?? string.Empty,
                FileName = item.FileName ?? string.Empty,
                Timestamp = item.Timestamp,
                BackupType = item.BackupType ?? string.Empty,
                Comment = item.Comment ?? string.Empty,
                IsImportant = item.IsImportant,
                IsCloudArchived = item.IsCloudArchived,
                CloudArchivedAtUtc = item.CloudArchivedAtUtc,
                CloudArchiveRemotePath = item.CloudArchiveRemotePath ?? string.Empty,
                CloudMetadataRecordRemotePath = item.CloudMetadataRecordRemotePath ?? string.Empty,
                CloudMetadataStateRemotePath = item.CloudMetadataStateRemotePath ?? string.Empty
            };
        }

        private static CloudActiveHistoryManifest BuildActiveHistoryManifest(BackupConfig config, IEnumerable<HistoryItem> items)
        {
            return new CloudActiveHistoryManifest
            {
                ConfigId = config.Id ?? string.Empty,
                ConfigName = config.Name ?? string.Empty,
                UpdatedAtUtc = DateTime.UtcNow,
                Entries = items?
                    .Where(item => item != null)
                    .Select(item => new CloudActiveHistoryEntry
                    {
                        FolderPath = item.FolderPath ?? string.Empty,
                        FolderName = item.FolderName ?? string.Empty,
                        FileName = item.FileName ?? string.Empty,
                        Timestamp = item.Timestamp
                    })
                    .OrderBy(entry => entry.Timestamp)
                    .ThenBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<CloudActiveHistoryEntry>()
            };
        }

        private static string BuildActiveHistoryManifestRemotePath(BackupConfig config)
        {
            return AppendRemotePath(
                config.Cloud?.RemoteBasePath ?? string.Empty,
                config.Name ?? string.Empty,
                InternalCloudStateDirectoryName,
                ActiveHistoryManifestFileName);
        }

        private static bool ManifestContainsHistoryItem(CloudActiveHistoryManifest manifest, HistoryItem item)
        {
            if (manifest?.Entries == null || item == null)
            {
                return false;
            }

            return manifest.Entries.Any(entry =>
                entry.Timestamp == item.Timestamp
                && string.Equals(entry.FileName, item.FileName, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(entry.FolderPath, item.FolderPath, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(entry.FolderName)
                        && string.Equals(entry.FolderName, item.FolderName, StringComparison.OrdinalIgnoreCase))));
        }

        private static ManagedFolder? ResolveFolderForHistoryItem(BackupConfig config, HistoryItem item)
        {
            if (config?.SourceFolders == null || item == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(item.FolderPath))
            {
                var byPath = config.SourceFolders.FirstOrDefault(folder =>
                    string.Equals(folder.Path, item.FolderPath, StringComparison.OrdinalIgnoreCase));
                if (byPath != null)
                {
                    return byPath;
                }
            }

            if (!string.IsNullOrWhiteSpace(item.FolderName))
            {
                var byName = config.SourceFolders
                    .Where(folder => string.Equals(folder.DisplayName, item.FolderName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (byName.Count == 1)
                {
                    return byName[0];
                }
            }

            return null;
        }

        private static List<HistoryItem> BuildRequiredRestoreHistoryChain(
            BackupConfig config,
            ManagedFolder folder,
            HistoryItem targetItem,
            IEnumerable<HistoryItem> candidateItems)
        {
            if (config == null || folder == null || targetItem == null)
            {
                return new List<HistoryItem>();
            }

            var relevantItems = (candidateItems ?? HistoryService.GetEntriesForConfig(config.Id))
                .Where(item => item != null)
                .Where(item =>
                    string.Equals(item.FileName, targetItem.FileName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.FolderPath, folder.Path, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.FolderName, folder.DisplayName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Timestamp)
                .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var effectiveTarget = relevantItems.FirstOrDefault(item =>
                string.Equals(item.FileName, targetItem.FileName, StringComparison.OrdinalIgnoreCase)
                && item.Timestamp == targetItem.Timestamp)
                ?? relevantItems.LastOrDefault(item =>
                    string.Equals(item.FileName, targetItem.FileName, StringComparison.OrdinalIgnoreCase))
                ?? targetItem;

            bool targetIsIncremental = BackupArchiveTypePolicy.IsIncremental(effectiveTarget.BackupType)
                || BackupArchiveTypePolicy.InferFromFileName(effectiveTarget.FileName).Equals("Smart", StringComparison.OrdinalIgnoreCase);
            var plan = BackupChainPlanner.Build(
                relevantItems,
                effectiveTarget,
                targetIsIncremental,
                new BackupChainPlanOptions<HistoryItem>
                {
                    GetTimestamp = item => item.Timestamp,
                    GetIdentity = item => item.FileName,
                    IsFull = item =>
                        string.Equals(item.BackupType, "Full", StringComparison.OrdinalIgnoreCase)
                        || BackupArchiveTypePolicy.InferFromFileName(item.FileName).Equals("Full", StringComparison.OrdinalIgnoreCase),
                    IsIncremental = item =>
                        BackupArchiveTypePolicy.IsIncremental(item.BackupType)
                        || BackupArchiveTypePolicy.InferFromFileName(item.FileName).Equals("Smart", StringComparison.OrdinalIgnoreCase),
                    SelectBaseFull = candidates => candidates
                        .OrderBy(item => item.Timestamp)
                        .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                        .LastOrDefault(),
                    OrderChain = candidates => candidates
                        .OrderBy(item => item.Timestamp)
                        .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase),
                    IdentityComparer = StringComparer.OrdinalIgnoreCase,
                    IncludeTargetInWindow = true
                });

            return plan.Status == BackupChainPlanStatus.Success
                ? plan.Items.ToList()
                : new List<HistoryItem>();
        }

    }
}

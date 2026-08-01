using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static partial class HistoryService
    {
        public static void AddEntry(BackupConfig config, ManagedFolder folder, string fileName, string type, string comment, string? folderNameOverride = null, bool isPartialBackup = false)
        {
            Initialize();

            var item = new HistoryItem
            {
                ConfigId = config.Id,
                FolderPath = folder.Path,
                FolderName = string.IsNullOrWhiteSpace(folderNameOverride) ? folder.DisplayName : folderNameOverride,
                FileName = fileName,
                Timestamp = DateTime.Now,
                BackupType = type,
                Comment = comment,
                IsPartialBackup = isPartialBackup,
                IsImportant = false
            };

            lock (_historyLock)
            {
                _allHistory.Add(item);
            }

            ScheduleSave();
        }

        /// <summary>
        /// 获取特定文件夹的历史记录 (用于 HistoryPage 展示)
        /// </summary>
        public static ObservableCollection<HistoryItem> GetHistoryForFolder(BackupConfig config, ManagedFolder folder)
        {
            Initialize(); // 确保加载

            List<HistoryItem> targetList;
            lock (_historyLock)
            {
                targetList = _allHistory
                    .Where(x => x.ConfigId == config.Id && x.FolderPath == folder.Path)
                    .OrderByDescending(x => x.Timestamp)
                    .ToList();
            }

            var collection = new ObservableCollection<HistoryItem>();
            var thresholdKB = ConfigService.CurrentConfig?.GlobalSettings?.FileSizeWarningThresholdKB ?? 5;
            foreach (var item in targetList)
            {
                // 尝试获取文件大小，如果文件还在的话
                var fullPath = GetBackupFilePath(config, folder, item);
                var exists = !string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath);
                // 云可用性要同时看“已归档标记 + 远端路径”，避免旧数据误显示为可下载。
                var hasCloudCopy = item.IsCloudArchived
                    && !string.IsNullOrWhiteSpace(item.CloudArchiveRemotePath);

                item.HasLocalFile = exists;
                item.HasCloudCopy = hasCloudCopy;
                item.IsCloudOnly = !exists && hasCloudCopy;
                item.IsMissing = !exists && !hasCloudCopy;
                item.IsSmallFile = false;

                if (exists)
                {
                    long size = new FileInfo(fullPath!).Length;
                    string sizeStr = $"{size / 1024.0 / 1024.0:F2} MB";

                    // 检查文件大小是否低于警告阈值
                    if (thresholdKB > 0 && (size / 1024.0) < thresholdKB)
                    {
                        item.IsSmallFile = true;
                        item.FileSizeDisplay = I18n.Format("History_FileSizeSmall", sizeStr);
                    }
                    else
                    {
                        item.FileSizeDisplay = sizeStr;
                    }
                }
                else if (hasCloudCopy)
                {
                    item.FileSizeDisplay = string.Empty;
                }
                else
                {
                    item.FileSizeDisplay = I18n.Format("History_FileMissing");
                }
                collection.Add(item);
            }
            return collection;
        }

        /// <summary>
        /// 计算某条历史记录对应的备份文件路径。
        /// 优先使用 HistoryItem.FolderName（避免重命名源文件夹时路径拼接错误）。
        /// </summary>
        public static string? GetBackupFilePath(BackupConfig config, ManagedFolder folder, HistoryItem item)
        {
            if (config == null || folder == null || item == null) return null;
            if (string.IsNullOrWhiteSpace(config.DestinationPath)) return null;
            if (string.IsNullOrWhiteSpace(item.FileName)) return null;

            string backupFolderName = string.IsNullOrWhiteSpace(item.FolderName)
                ? folder.DisplayName
                : item.FolderName;

            if (string.IsNullOrWhiteSpace(backupFolderName))
            {
                backupFolderName = folder.DisplayName;
            }

            if (!BackupStoragePathService.IsSafeSinglePathSegment(backupFolderName)
                || !BackupStoragePathService.IsSafeSinglePathSegment(item.FileName))
            {
                LogService.Log($"[HistoryService] Rejected unsafe history path: folder='{backupFolderName}', file='{item.FileName}'", LogLevel.Warning);
                return null;
            }

            try
            {
                string destinationRoot = Path.GetFullPath(config.DestinationPath);
                string backupFolderPath = Path.GetFullPath(Path.Combine(destinationRoot, backupFolderName));
                if (!BackupStoragePathService.IsPathInsideRoot(backupFolderPath, destinationRoot))
                {
                    LogService.Log($"[HistoryService] Rejected history folder outside destination root: {backupFolderPath}", LogLevel.Warning);
                    return null;
                }

                string backupFilePath = Path.GetFullPath(Path.Combine(backupFolderPath, item.FileName));
                if (!BackupStoragePathService.IsPathInsideRoot(backupFilePath, backupFolderPath))
                {
                    LogService.Log($"[HistoryService] Rejected history file outside backup folder: {backupFilePath}", LogLevel.Warning);
                    return null;
                }

                return backupFilePath;
            }
            catch (Exception ex)
            {
                LogService.Log($"[HistoryService] Failed to resolve backup path: {ex.Message}", LogLevel.Warning);
                return null;
            }
        }

        private static bool IsSafeHistoryItem(HistoryItem item)
        {
            if (item == null)
            {
                return false;
            }

            if (!BackupStoragePathService.IsSafeSinglePathSegment(item.FileName))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(item.FolderName)
                && !BackupStoragePathService.IsSafeSinglePathSegment(item.FolderName))
            {
                return false;
            }

            return true;
        }

        public static int RemoveMissingEntries(BackupConfig config, ManagedFolder folder)
        {
            Initialize();

            List<HistoryItem> toRemove;
            lock (_historyLock)
            {
                toRemove = _allHistory
                    .Where(x => x.ConfigId == config.Id && x.FolderPath == folder.Path)
                    .Where(x =>
                    {
                        var p = GetBackupFilePath(config, folder, x);
                        bool hasLocalFile = !string.IsNullOrWhiteSpace(p) && File.Exists(p);
                        bool hasCloudCopy = x.IsCloudArchived && !string.IsNullOrWhiteSpace(x.CloudArchiveRemotePath);
                        return !hasLocalFile && !hasCloudCopy;
                    })
                    .ToList();

                foreach (var item in toRemove)
                {
                    _allHistory.Remove(item);
                }
            }

            if (toRemove.Count > 0)
            {
                ScheduleSave();
            }

            return toRemove.Count;
        }

    }
}

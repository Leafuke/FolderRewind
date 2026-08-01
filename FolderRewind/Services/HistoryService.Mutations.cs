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
        public static void RemoveEntry(HistoryItem item)
        {
            bool removed = false;
            lock (_historyLock)
            {
                if (_allHistory.Contains(item))
                {
                    _allHistory.Remove(item);
                    removed = true;
                }
            }

            if (removed)
            {
                ScheduleSave();
            }
        }

        public static HistoryItem? TryGetEntry(string configId, string folderPath, string fileName)
        {
            if (string.IsNullOrWhiteSpace(configId)
                || string.IsNullOrWhiteSpace(folderPath)
                || string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            Initialize();
            lock (_historyLock)
            {
                return _allHistory
                    .Where(x => string.Equals(x.ConfigId, configId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.Timestamp)
                    .FirstOrDefault();
            }
        }

        public static int RemoveEntriesForFile(string configId, string folderName, string fileName)
        {
            if (string.IsNullOrWhiteSpace(configId)
                || string.IsNullOrWhiteSpace(folderName)
                || string.IsNullOrWhiteSpace(fileName))
            {
                return 0;
            }

            Initialize();
            int removedCount = 0;
            lock (_historyLock)
            {
                removedCount = _allHistory.RemoveAll(x =>
                    string.Equals(x.ConfigId, configId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.FolderName, folderName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.FileName, fileName, StringComparison.OrdinalIgnoreCase));
            }

            if (removedCount > 0)
            {
                ScheduleSave();
            }

            return removedCount;
        }

        public static int RemoveEntriesForConfig(string configId)
        {
            if (string.IsNullOrWhiteSpace(configId))
            {
                return 0;
            }

            Initialize();
            int removedCount = 0;
            lock (_historyLock)
            {
                removedCount = _allHistory.RemoveAll(x =>
                    string.Equals(x.ConfigId, configId, StringComparison.OrdinalIgnoreCase));
            }

            if (removedCount > 0)
            {
                ScheduleSave();
            }

            return removedCount;
        }

        public static List<HistoryItem> GetEntriesForConfig(string configId)
        {
            if (string.IsNullOrWhiteSpace(configId))
            {
                return new List<HistoryItem>();
            }

            Initialize();
            lock (_historyLock)
            {
                return _allHistory
                    .Where(x => string.Equals(x.ConfigId, configId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.Timestamp)
                    .ToList();
            }
        }

        internal static HistoryFolderIdentityUpdate UpdateFolderIdentities(
            IReadOnlyList<FolderRenameReferencePlan> references)
        {
            if (references == null || references.Count == 0)
            {
                return new HistoryFolderIdentityUpdate();
            }

            Initialize();
            int updated = 0;
            var snapshots = new List<HistoryFolderIdentitySnapshot>();

            lock (_historyLock)
            {
                foreach (var item in _allHistory)
                {
                    if (!FolderRenameService.TryResolveHistoryIdentityUpdate(
                            item.ConfigId,
                            item.FolderPath,
                            item.FolderName,
                            references,
                            out string newPath,
                            out string newFolderName))
                    {
                        continue;
                    }

                    snapshots.Add(new HistoryFolderIdentitySnapshot(
                        item,
                        item.FolderPath ?? string.Empty,
                        item.FolderName ?? string.Empty));
                    item.FolderPath = newPath;
                    item.FolderName = newFolderName;
                    updated++;
                }
            }

            return new HistoryFolderIdentityUpdate
            {
                UpdatedCount = updated,
                Snapshots = snapshots
            };
        }

        internal static void RestoreFolderIdentities(
            IReadOnlyList<HistoryFolderIdentitySnapshot> snapshots)
        {
            if (snapshots == null || snapshots.Count == 0)
            {
                return;
            }

            lock (_historyLock)
            {
                foreach (var snapshot in snapshots)
                {
                    snapshot.Item.FolderPath = snapshot.FolderPath;
                    snapshot.Item.FolderName = snapshot.FolderName;
                }
            }
        }

        /// <summary>
        /// 更新历史记录的注释
        /// </summary>
        public static void UpdateComment(HistoryItem item, string newComment)
        {
            if (item == null) return;

            lock (_historyLock)
            {
                // 查找并更新内存中的记录
                var target = _allHistory.FirstOrDefault(x =>
                    x.ConfigId == item.ConfigId &&
                    x.FolderPath == item.FolderPath &&
                    x.FileName == item.FileName &&
                    x.Timestamp == item.Timestamp);

                if (target != null)
                {
                    target.Comment = newComment;
                }

                // 同时更新传入的item（可能是UI绑定的对象）
                item.Comment = newComment;
            }

            ScheduleSave();
        }

        /// <summary>
        /// 切换历史记录的重要标记
        /// </summary>
        public static void ToggleImportant(HistoryItem item)
        {
            if (item == null) return;

            lock (_historyLock)
            {
                var target = _allHistory.FirstOrDefault(x =>
                    x.ConfigId == item.ConfigId &&
                    x.FolderPath == item.FolderPath &&
                    x.FileName == item.FileName &&
                    x.Timestamp == item.Timestamp);

                bool newValue = !item.IsImportant;
                if (target != null)
                {
                    target.IsImportant = newValue;
                }
                item.IsImportant = newValue;
            }

            ScheduleSave();
        }

        /// <summary>
        /// 通过配置ID、文件夹名和文件名设置重要标记（用于 KnotLink 远程命令）
        /// </summary>
        public static bool SetImportant(string configId, string folderName, string fileName, bool isImportant)
        {
            Initialize();
            bool found = false;

            lock (_historyLock)
            {
                var target = _allHistory.FirstOrDefault(x =>
                    x.ConfigId == configId &&
                    string.Equals(x.FolderName, folderName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.FileName, fileName, StringComparison.OrdinalIgnoreCase));

                if (target != null)
                {
                    target.IsImportant = isImportant;
                    found = true;
                }
            }

            if (found)
            {
                ScheduleSave();
            }

            return found;
        }

        /// <summary>
        /// 获取全局最近一条历史记录（按 Timestamp 降序），用于 MARK_IMPORTANT 无参数时标记最近备份。
        /// </summary>
        public static HistoryItem? GetLatestEntry()
        {
            Initialize();
            lock (_historyLock)
            {
                return _allHistory
                    .OrderByDescending(x => x.Timestamp)
                    .FirstOrDefault();
            }
        }

        /// <summary>
        /// 根据 configId 和文件夹名获取历史记录列表（用于安全删除等内部逻辑，
        /// 不创建 ObservableCollection，直接返回快照列表）
        /// </summary>
        public static List<HistoryItem> GetEntriesForFolder(string configId, string folderName)
        {
            Initialize();
            lock (_historyLock)
            {
                return _allHistory
                    .Where(x => x.ConfigId == configId
                        && string.Equals(x.FolderName, folderName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        /// <summary>
        /// 从历史记录中查询某个备份文件的类型。优先用于避免依赖文件名约定推断类型。
        /// </summary>
        public static string? GetBackupTypeForFile(string configId, string folderName, string fileName)
        {
            if (string.IsNullOrWhiteSpace(configId)
                || string.IsNullOrWhiteSpace(folderName)
                || string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            Initialize();
            lock (_historyLock)
            {
                return _allHistory
                    .Where(x => x.ConfigId == configId
                        && string.Equals(x.FolderName, folderName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.Timestamp)
                    .Select(x => x.BackupType)
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            }
        }

        /// <summary>
        /// 重命名历史记录中指定备份文件的条目（用于安全删除中 Smart→Full 升级）
        /// </summary>
        public static void RenameEntry(string oldFileName, string newFileName, string? newBackupType = null)
        {
            Initialize();
            bool modified = false;
            lock (_historyLock)
            {
                foreach (var item in _allHistory)
                {
                    if (string.Equals(item.FileName, oldFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        item.FileName = newFileName;
                        if (!string.IsNullOrEmpty(newBackupType))
                        {
                            item.BackupType = newBackupType;
                        }
                        modified = true;
                        break;
                    }
                }
            }
            if (modified)
            {
                ScheduleSave();
            }
        }

        public static int RenameEntriesForFile(string configId, string folderName, string oldFileName, string newFileName, string? newBackupType = null)
        {
            if (string.IsNullOrWhiteSpace(configId)
                || string.IsNullOrWhiteSpace(folderName)
                || string.IsNullOrWhiteSpace(oldFileName)
                || string.IsNullOrWhiteSpace(newFileName))
            {
                return 0;
            }

            Initialize();
            int modifiedCount = 0;
            lock (_historyLock)
            {
                foreach (var item in _allHistory)
                {
                    if (!string.Equals(item.ConfigId, configId, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(item.FolderName, folderName, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(item.FileName, oldFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    item.FileName = newFileName;
                    if (!string.IsNullOrWhiteSpace(newBackupType))
                    {
                        item.BackupType = newBackupType;
                    }
                    modifiedCount++;
                }
            }

            if (modifiedCount > 0)
            {
                ScheduleSave();
            }

            return modifiedCount;
        }

        public static bool UpdateCloudArchiveState(
            HistoryItem item,
            bool isCloudArchived,
            DateTime? archivedAtUtc,
            string? archiveRemotePath,
            string? metadataRecordRemotePath,
            string? metadataStateRemotePath)
        {
            if (item == null)
            {
                return false;
            }

            Initialize();
            bool updated = false;
            lock (_historyLock)
            {
                // 以“配置 + 文件夹 + 文件名 + 时间戳”定位唯一历史项，避免同名包被误更新。
                var target = _allHistory.FirstOrDefault(x =>
                    x.ConfigId == item.ConfigId &&
                    x.FolderPath == item.FolderPath &&
                    x.FileName == item.FileName &&
                    x.Timestamp == item.Timestamp);

                if (target == null)
                {
                    return false;
                }

                ApplyCloudState(target, isCloudArchived, archivedAtUtc, archiveRemotePath, metadataRecordRemotePath, metadataStateRemotePath);
                if (!ReferenceEquals(target, item))
                {
                    ApplyCloudState(item, isCloudArchived, archivedAtUtc, archiveRemotePath, metadataRecordRemotePath, metadataStateRemotePath);
                }

                updated = true;
            }

            if (updated)
            {
                ScheduleSave();
            }

            return updated;
        }

        public static bool UpdateCloudArchiveState(
            string configId,
            string folderPath,
            string fileName,
            bool isCloudArchived,
            DateTime? archivedAtUtc,
            string? archiveRemotePath,
            string? metadataRecordRemotePath,
            string? metadataStateRemotePath)
        {
            var item = TryGetEntry(configId, folderPath, fileName);
            if (item == null)
            {
                return false;
            }

            return UpdateCloudArchiveState(
                item,
                isCloudArchived,
                archivedAtUtc,
                archiveRemotePath,
                metadataRecordRemotePath,
                metadataStateRemotePath);
        }

        /// <summary>
        /// 导出历史记录到指定路径
        /// </summary>
    }
}

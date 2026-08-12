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
        /// <summary>
        /// 导出历史记录到指定路径
        /// </summary>
        public static bool ExportHistory(string destPath)
        {
            Initialize();
            try
            {
                List<HistoryItem> snapshot;
                lock (_historyLock)
                {
                    snapshot = _allHistory.ToList();
                }
                using var stream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(stream, snapshot, AppJsonContext.Default.ListHistoryItem);
                LogService.Log(I18n.Format("History_ExportSuccess", destPath));
                return true;
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("History_ExportFailed", ex.Message));
                return false;
            }
        }

        /// <summary>
        /// 从指定路径导入历史记录（合并或替换）
        /// </summary>
        public static (bool Success, int Count) ImportHistory(string sourcePath, bool merge = true)
        {
            Initialize();
            try
            {
                if (!File.Exists(sourcePath)) return (false, 0);
                string json = File.ReadAllText(sourcePath);
                var imported = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListHistoryItem);
                if (imported == null) return (false, 0);

                var importResult = ImportHistoryItems(imported, merge);
                return (importResult.Success, importResult.ImportedCount);
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("History_ImportFailed", ex.Message));
                return (false, 0);
            }
        }

        public static (bool Success, int ImportedCount, int DuplicateCount) ImportHistoryItems(IEnumerable<HistoryItem> items, bool merge = true)
        {
            Initialize();
            try
            {
                if (items == null)
                {
                    return (false, 0, 0);
                }

                var imported = items.Where(item => item != null).ToList();
                var safeImported = imported.Where(IsSafeHistoryItem).ToList();
                foreach (var item in safeImported)
                {
                    EnsureHistoryItemIdentity(item);
                }
                int droppedCount = imported.Count - safeImported.Count;
                if (safeImported.Count == 0 && imported.Count > 0)
                {
                    LogService.Log("[HistoryService] Import skipped: all entries were rejected due to unsafe paths.", LogLevel.Warning);
                }

                int importedCount = 0;
                int duplicateCount = 0;
                lock (_historyLock)
                {
                    if (merge)
                    {
                        foreach (var item in safeImported)
                        {
                            bool exists = _allHistory.Any(x =>
                                x.ConfigId == item.ConfigId &&
                                x.FolderPath == item.FolderPath &&
                                x.FileName == item.FileName &&
                                x.Timestamp == item.Timestamp);
                            if (exists)
                            {
                                duplicateCount++;
                                continue;
                            }

                            _allHistory.Add(item);
                            importedCount++;
                        }
                    }
                    else
                    {
                        try
                        {
                            string backupPath = HistoryPath + ".bak";
                            if (File.Exists(HistoryPath)) File.Copy(HistoryPath, backupPath, true);
                        }
                        catch
                        {
                        }

                        _allHistory.Clear();
                        _allHistory.AddRange(safeImported);
                        importedCount = safeImported.Count;
                    }
                }

                ScheduleSave();
                if (droppedCount > 0)
                {
                    LogService.Log($"[HistoryService] Import dropped {droppedCount} unsafe entries.", LogLevel.Warning);
                }

                LogService.Log(I18n.Format("History_ImportSuccess", importedCount.ToString()));
                return (true, importedCount, duplicateCount);
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("History_ImportFailed", ex.Message));
                return (false, 0, 0);
            }
        }

        /// <summary>
        /// 备份文件名正则：匹配 [Full/Smart/Overwrite][yyyy-MM-dd_HH-mm-ss]FolderName [Comment].7z/zip
        /// </summary>
        private static readonly Regex BackupFileNameRegex = new(
            @"^\[(Full|Smart|Overwrite)\]\[(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})\](.+?)(?:\s\[(.+?)\])?\.(7z|zip)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 扫描指定文件夹中的备份文件，为当前选中的文件夹重建历史记录。
        /// 仅匹配当前文件夹名称对应的备份文件，避免引入其他文件夹的记录。
        /// </summary>
        /// <param name="scanDirectory">要扫描的目录路径</param>
        /// <param name="config">当前选中的备份配置</param>
        /// <param name="folder">当前选中的源文件夹</param>
        /// <returns>成功恢复的记录数</returns>
        public static int ScanAndRecoverHistory(string scanDirectory, BackupConfig config, ManagedFolder folder)
        {
            Initialize();

            if (string.IsNullOrWhiteSpace(scanDirectory) || !Directory.Exists(scanDirectory))
                return 0;
            if (config == null || folder == null)
                return 0;

            string folderName = folder.DisplayName;
            if (string.IsNullOrWhiteSpace(folderName))
                return 0;

            int recoveredCount = 0;

            try
            {
                // 扫描目录中所有 .7z 和 .zip 文件
                var archiveFiles = Directory.EnumerateFiles(scanDirectory, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f =>
                    {
                        var ext = Path.GetExtension(f);
                        return string.Equals(ext, ".7z", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

                foreach (var filePath in archiveFiles)
                {
                    string fileName = Path.GetFileName(filePath);
                    var match = BackupFileNameRegex.Match(fileName);
                    if (!match.Success) continue;

                    string backupType = match.Groups[1].Value;  // Full, Smart, Overwrite
                    string timeStr = match.Groups[2].Value;      // yyyy-MM-dd_HH-mm-ss
                    string parsedFolderName = match.Groups[3].Value; // 文件夹名
                    string comment = match.Groups[4].Success ? match.Groups[4].Value : string.Empty;

                    // 仅匹配当前文件夹名称的备份
                    if (!string.Equals(parsedFolderName.Trim(), folderName.Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;

                    // 解析时间戳
                    if (!DateTime.TryParseExact(timeStr, "yyyy-MM-dd_HH-mm-ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime timestamp))
                        continue;

                    // 标准化 BackupType 首字母大写
                    backupType = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(backupType.ToLowerInvariant());

                    // 检查是否已存在相同记录（按 ConfigId + FolderPath + FileName 和 Timestamp 判重）
                    bool alreadyExists;
                    lock (_historyLock)
                    {
                        alreadyExists = _allHistory.Any(x =>
                            x.ConfigId == config.Id &&
                            MatchesFolderIdentity(x, folder) &&
                            string.Equals(x.FileName, fileName, StringComparison.OrdinalIgnoreCase));
                    }

                    if (alreadyExists) continue;

                    var item = new HistoryItem
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        ConfigId = config.Id,
                        FolderId = Guid.TryParse(folder.Id, out var folderId) ? folderId : null,
                        FolderPath = folder.Path,
                        FolderName = folderName,
                        FileName = fileName,
                        Timestamp = timestamp,
                        BackupType = backupType,
                        Comment = comment,
                        Outcome = PersistedOperationOutcome.Success,
                        IsImportant = false
                    };

                    lock (_historyLock)
                    {
                        _allHistory.Add(item);
                    }

                    recoveredCount++;
                }

                if (recoveredCount > 0)
                {
                    ScheduleSave();
                    LogService.Log(I18n.Format("History_ScanRecover_Success", recoveredCount.ToString(), folderName));
                }
                else
                {
                    LogService.Log(I18n.Format("History_ScanRecover_NoMatch", folderName));
                }
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("History_ScanRecover_Error", ex.Message), LogLevel.Error);
            }

            return recoveredCount;
        }

        private static void ApplyCloudState(
            HistoryItem item,
            bool isCloudArchived,
            DateTime? archivedAtUtc,
            string? archiveRemotePath,
            string? metadataRecordRemotePath,
            string? metadataStateRemotePath)
        {
            // 取消云归档时要同步清空远端路径，避免 UI 继续把条目标记为“可云下载”。
            item.IsCloudArchived = isCloudArchived;
            item.CloudArchivedAtUtc = isCloudArchived
                ? (archivedAtUtc ?? DateTime.UtcNow)
                : DateTime.MinValue;
            item.CloudArchiveRemotePath = isCloudArchived ? (archiveRemotePath ?? string.Empty) : string.Empty;
            item.CloudMetadataRecordRemotePath = isCloudArchived ? (metadataRecordRemotePath ?? string.Empty) : string.Empty;
            item.CloudMetadataStateRemotePath = isCloudArchived ? (metadataStateRemotePath ?? string.Empty) : string.Empty;
        }

        private static bool AreSameFolderPath(string? left, string? right)
        {
            string normalizedLeft = NormalizeFolderPath(left);
            string normalizedRight = NormalizeFolderPath(right);

            return !string.IsNullOrWhiteSpace(normalizedLeft)
                && !string.IsNullOrWhiteSpace(normalizedRight)
                && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeFolderPath(string? path)
        {
            string candidate = (path ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return string.Empty;
            }

            string root = Path.GetPathRoot(candidate) ?? string.Empty;
            while (candidate.Length > root.Length
                && (candidate.EndsWith(Path.DirectorySeparatorChar) || candidate.EndsWith(Path.AltDirectorySeparatorChar)))
            {
                candidate = candidate[..^1];
            }

            try
            {
                return Path.GetFullPath(candidate);
            }
            catch
            {
                return candidate;
            }
        }
    }
}

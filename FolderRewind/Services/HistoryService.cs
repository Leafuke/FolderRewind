using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static class HistoryService
    {
        private const string HistoryFileName = "history.json";
        // 与 config.json 同目录（适配打包 / 非打包路径）
        private static string HistoryPath => Path.Combine(ConfigService.ConfigDirectory, HistoryFileName);

        // 内存缓存：所有历史记录
        private static List<HistoryItem> _allHistory = new();
        private static readonly object _initLock = new();
        private static bool _initialized;
        private static readonly object _historyLock = new();

        // 异步防抖保存
        private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(300);
        private static CancellationTokenSource? _saveCts;
        private static Task? _pendingSave;
        private static readonly SemaphoreSlim _saveLock = new(1, 1);

        public static void Initialize()
        {
            if (_initialized) return;
            lock (_initLock)
            {
                if (_initialized) return;

                if (File.Exists(HistoryPath))
                {
                    try
                    {
                        string json = File.ReadAllText(HistoryPath);
                        _allHistory = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListHistoryItem) ?? new List<HistoryItem>();
                    }
                    catch
                    {
                        _allHistory = new List<HistoryItem>();
                    }
                }
                // 修正反序列化后集合为 null 或类型不兼容的情况，防止绑定崩溃
                if (_allHistory == null)
                    _allHistory = new List<HistoryItem>();
                else if (_allHistory.GetType() != typeof(List<HistoryItem>))
                    _allHistory = new List<HistoryItem>(_allHistory);

                _initialized = true;
            }
        }

        public static void Save()
        {
            // 保持兼容：触发后台保存即可，不要阻塞等待
            ScheduleSave();
        }

        /// <summary>
        /// 添加一条新的历史记录
        /// </summary>
        public static void AddEntry(BackupConfig config, ManagedFolder folder, string fileName, string type, string comment)
        {
            Initialize();

            var item = new HistoryItem
            {
                ConfigId = config.Id,
                FolderPath = folder.Path,
                FolderName = folder.DisplayName,
                FileName = fileName,
                Timestamp = DateTime.Now,
                BackupType = type,
                Comment = comment,
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

                item.IsMissing = !exists;
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

            return Path.Combine(config.DestinationPath, backupFolderName, item.FileName);
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
                        return string.IsNullOrWhiteSpace(p) || !File.Exists(p);
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
                string json = JsonSerializer.Serialize(snapshot, AppJsonContext.Default.ListHistoryItem);
                File.WriteAllText(destPath, json);
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

                int count = 0;
                lock (_historyLock)
                {
                    if (merge)
                    {
                        // 合并模式：仅添加不存在的条目（按 ConfigId+FolderPath+FileName+Timestamp 去重）
                        foreach (var item in imported)
                        {
                            bool exists = _allHistory.Any(x =>
                                x.ConfigId == item.ConfigId &&
                                x.FolderPath == item.FolderPath &&
                                x.FileName == item.FileName &&
                                x.Timestamp == item.Timestamp);
                            if (!exists)
                            {
                                _allHistory.Add(item);
                                count++;
                            }
                        }
                    }
                    else
                    {
                        // 替换模式
                        // 先备份
                        try
                        {
                            string backupPath = HistoryPath + ".bak";
                            if (File.Exists(HistoryPath)) File.Copy(HistoryPath, backupPath, true);
                        }
                        catch { }

                        _allHistory.Clear();
                        _allHistory.AddRange(imported);
                        count = imported.Count;
                    }
                }

                ScheduleSave();
                LogService.Log(I18n.Format("History_ImportSuccess", count.ToString()));
                return (true, count);
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("History_ImportFailed", ex.Message));
                return (false, 0);
            }
        }

        private static void ScheduleSave()
        {
            // 取消前一次保存，合并写盘
            _saveCts?.Cancel();
            _saveCts = new CancellationTokenSource();
            var token = _saveCts.Token;

            _pendingSave = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(SaveDelay, token);
                    await PersistAsync(token);
                }
                catch (OperationCanceledException)
                {
                    // 被新的写请求合并，忽略
                }
            }, token);
        }

        private static async Task PersistAsync(CancellationToken ct)
        {
            Initialize();
            await _saveLock.WaitAsync(ct);
            try
            {
                var configDir = Path.GetDirectoryName(HistoryPath);
                if (!Directory.Exists(configDir))
                {
                    Directory.CreateDirectory(configDir!);
                }

                List<HistoryItem> snapshot;
                lock (_historyLock)
                {
                    snapshot = _allHistory.ToList();
                }

                string json = JsonSerializer.Serialize(snapshot, AppJsonContext.Default.ListHistoryItem);
                await File.WriteAllTextAsync(HistoryPath, json, ct);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"History save failed: {ex.Message}");
            }
            finally
            {
                _saveLock.Release();
            }
        }
    }
}
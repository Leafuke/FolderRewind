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
        private static string HistoryFileName = "history.json";
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
            // 保持兼容：同步触发保存，但内部仍走异步防抖
            ScheduleSave();
            _pendingSave?.GetAwaiter().GetResult();
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
            foreach (var item in targetList)
            {
                // 尝试获取文件大小，如果文件还在的话
                var fullPath = GetBackupFilePath(config, folder, item);
                var exists = !string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath);

                item.IsMissing = !exists;

                if (exists)
                {
                    long size = new FileInfo(fullPath!).Length;
                    item.FileSizeDisplay = $"{size / 1024.0 / 1024.0:F2} MB";
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
        /// 重命名历史记录中指定备份文件的条目（用于安全删除中 Smart→Full 升级）
        /// </summary>
        public static void RenameEntry(string oldFileName, string newFileName, string newBackupType = null)
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
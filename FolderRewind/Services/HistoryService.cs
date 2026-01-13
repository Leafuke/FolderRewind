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
            string baseDir = Path.Combine(config.DestinationPath, folder.DisplayName);

            foreach (var item in targetList)
            {
                // 尝试获取文件大小，如果文件还在的话
                string fullPath = Path.Combine(baseDir, item.FileName);
                if (File.Exists(fullPath))
                {
                    long size = new FileInfo(fullPath).Length;
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
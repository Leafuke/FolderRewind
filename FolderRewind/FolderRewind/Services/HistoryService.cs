using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static class HistoryService
    {
        private static string HistoryFileName = "history.json";
        private static string HistoryPath => Path.Combine(AppContext.BaseDirectory, HistoryFileName);

        // 内存缓存：所有历史记录
        private static List<HistoryItem> _allHistory = new();

        public static void Initialize()
        {
            if (File.Exists(HistoryPath))
            {
                try
                {
                    string json = File.ReadAllText(HistoryPath);
                    _allHistory = JsonSerializer.Deserialize<List<HistoryItem>>(json) ?? new List<HistoryItem>();
                }
                catch
                {
                    _allHistory = new List<HistoryItem>();
                }
            }
        }

        public static void Save()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_allHistory, options);
                File.WriteAllText(HistoryPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"History save failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 添加一条新的历史记录
        /// </summary>
        public static void AddEntry(BackupConfig config, ManagedFolder folder, string fileName, string type, string comment)
        {
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

            _allHistory.Add(item);
            Save();
        }

        /// <summary>
        /// 获取特定文件夹的历史记录 (用于 HistoryPage 展示)
        /// </summary>
        public static ObservableCollection<HistoryItem> GetHistoryForFolder(BackupConfig config, ManagedFolder folder)
        {
            Initialize(); // 确保加载

            var targetList = _allHistory
                .Where(x => x.ConfigId == config.Id && x.FolderPath == folder.Path)
                .OrderByDescending(x => x.Timestamp)
                .ToList();

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
                    item.FileSizeDisplay = "文件丢失";
                }
                collection.Add(item);
            }
            return collection;
        }

        public static void RemoveEntry(HistoryItem item)
        {
            if (_allHistory.Contains(item))
            {
                _allHistory.Remove(item);
                Save();
            }
        }
    }
}
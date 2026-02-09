using System;
using System.Collections.Concurrent;
using System.IO;

namespace FolderRewind.Services
{
    /// <summary>
    /// 基于 FileSystemWatcher 的文件夹变更检测服务。
    /// 每个被监视的文件夹对应一个 Watcher 实例，任何文件变更都会将该路径标记为 "已变更"。
    /// </summary>
    public static class FolderWatcherService
    {
        private static readonly ConcurrentDictionary<string, WatcherEntry> _watchers
            = new(StringComparer.OrdinalIgnoreCase);

        private sealed class WatcherEntry : IDisposable
        {
            public FileSystemWatcher Watcher { get; set; }
            public volatile bool HasChanges;

            public void Dispose()
            {
                try
                {
                    Watcher.EnableRaisingEvents = false;
                    Watcher.Dispose();
                }
                catch { }
            }
        }

        /// <summary>
        /// 开始监视指定文件夹。如果已在监视中，则不做处理。
        /// </summary>
        public static void StartWatching(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return;
            if (!Directory.Exists(folderPath)) return;

            if (_watchers.ContainsKey(folderPath)) return;

            try
            {
                var watcher = new FileSystemWatcher(folderPath)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName
                                 | NotifyFilters.DirectoryName
                                 | NotifyFilters.Size
                                 | NotifyFilters.LastWrite,
                    InternalBufferSize = 32768, // 32KB 缓冲区，减少事件丢失概率
                };

                var entry = new WatcherEntry { Watcher = watcher, HasChanges = false };

                watcher.Changed += (_, __) => entry.HasChanges = true;
                watcher.Created += (_, __) => entry.HasChanges = true;
                watcher.Deleted += (_, __) => entry.HasChanges = true;
                watcher.Renamed += (_, __) => entry.HasChanges = true;
                watcher.Error += (_, args) =>
                {
                    LogService.Log(I18n.Format("MiniWindow_Log_WatcherError", folderPath, args.GetException()?.Message ?? "Unknown"));
                };

                watcher.EnableRaisingEvents = true;

                if (!_watchers.TryAdd(folderPath, entry))
                {
                    entry.Dispose();
                }
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("MiniWindow_Log_WatcherStartFailed", folderPath, ex.Message));
            }
        }

        /// <summary>
        /// 停止监视指定文件夹并释放资源。
        /// </summary>
        public static void StopWatching(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return;
            if (_watchers.TryRemove(folderPath, out var entry))
            {
                entry.Dispose();
            }
        }

        /// <summary>
        /// 查询指定文件夹是否发生过变更（自上次 ResetChanges 以来）。
        /// </summary>
        public static bool HasChanges(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return false;
            return _watchers.TryGetValue(folderPath, out var entry) && entry.HasChanges;
        }

        /// <summary>
        /// 重置指定文件夹的变更标记（通常在备份完成后调用）。
        /// </summary>
        public static void ResetChanges(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return;
            if (_watchers.TryGetValue(folderPath, out var entry))
            {
                entry.HasChanges = false;
            }
        }

        /// <summary>
        /// 停止所有监视，释放资源。
        /// </summary>
        public static void StopAll()
        {
            foreach (var kvp in _watchers)
            {
                kvp.Value.Dispose();
            }
            _watchers.Clear();
        }
    }
}

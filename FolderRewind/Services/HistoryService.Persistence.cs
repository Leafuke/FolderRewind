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

        public static event Action? HistoryChanged;

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

        public static Task<HistorySaveResult> SaveNowAsync(
            CancellationToken cancellationToken = default)
            => SaveNowAsync(publishChangedEvent: true, cancellationToken);

        internal static async Task<HistorySaveResult> SaveNowAsync(
            bool publishChangedEvent,
            CancellationToken cancellationToken = default)
        {
            _saveCts?.Cancel();
            var result = await PersistAsync(cancellationToken);
            if (result.Success && publishChangedEvent)
            {
                PublishChanged();
            }

            return result;
        }

        /// <summary>
        /// 添加一条新的历史记录
        /// </summary>

        private static void ScheduleSave()
        {
            PublishChanged();

            // 取消前一次保存，合并写盘
            _saveCts?.Cancel();
            _saveCts = new CancellationTokenSource();
            var token = _saveCts.Token;

            _pendingSave = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(SaveDelay, token);
                    var result = await PersistAsync(token);
                    if (!result.Success)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"History save failed: {result.ErrorMessage}");
                        LogService.Log(
                            I18n.Format("History_SaveFailed", result.ErrorMessage),
                            LogLevel.Error);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 被新的写请求合并，忽略
                }
            }, token);
        }

        internal static void PublishChanged() => HistoryChanged?.Invoke();

        private static async Task<HistorySaveResult> PersistAsync(CancellationToken ct)
        {
            Initialize();
            bool lockTaken = false;
            try
            {
                await _saveLock.WaitAsync(ct);
                lockTaken = true;

                List<HistoryItem> snapshot;
                lock (_historyLock)
                {
                    snapshot = _allHistory.ToList();
                }

                AtomicFileService.Write(
                    HistoryPath,
                    stream => JsonSerializer.Serialize(
                        stream,
                        snapshot,
                        AppJsonContext.Default.ListHistoryItem));
                return new HistorySaveResult { Success = true };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new HistorySaveResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    Exception = ex
                };
            }
            finally
            {
                if (lockTaken)
                {
                    _saveLock.Release();
                }
            }
        }
    }
}

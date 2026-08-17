using FolderRewind.Models;
using FolderRewind.Services.KnotLink;
using FolderRewind.Services.Plugins;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    /// <summary>
    /// KnotLink 互联服务
    /// 参考 MineBackup Console.cpp 实现的远程命令接收与事件广播功能
    /// </summary>
    public static partial class KnotLinkService
    {
        #region 常量与配置

        // 默认应用标识符（与 MineBackup 保持一致）
        private const string DefaultAppId = KnotLinkFuncListService.DefaultAppId;
        private const string DefaultOpenSocketId = KnotLinkFuncListService.DefaultOpenSocketId;
        private const string DefaultSignalId = KnotLinkFuncListService.DefaultSignalId;
        private static string _activeAppId = DefaultAppId;
        private static string _activeOpenSocketId = DefaultOpenSocketId;
        private static string _activeSignalId = DefaultSignalId;

        #endregion

        #region 私有字段

        private static SignalSender? _signalSender;
        private static OpenSocketResponser? _commandResponser;
        private static readonly object _initLock = new();
        private static readonly KnotLinkInitializationState _initializationState = new();
        private static bool _isEnabled;

        // 自动备份任务管理（对应 MineBackup 的 g_active_auto_backups）
        private static readonly ConcurrentDictionary<(string configId, string folderPath), CancellationTokenSource> _activeAutoBackups = new();
        private static readonly AsyncLocal<KnotLinkCommandContext?> _currentCommandContext = new();

        #endregion

        #region 公开属性

        /// <summary>
        /// 服务是否已启用
        /// </summary>
        public static bool IsEnabled => Volatile.Read(ref _isEnabled);

        /// <summary>
        /// 服务是否已初始化
        /// </summary>
        public static bool IsInitialized => _initializationState.IsInitialized;

        /// <summary>
        /// 命令响应器是否已成功初始化
        /// </summary>
        public static bool IsResponserRunning => _initializationState.ResponserInitialized;

        /// <summary>
        /// 信号发送器是否已成功初始化
        /// </summary>
        public static bool IsSenderRunning => _initializationState.SenderInitialized;

        public static KnotLinkCommandContext? CurrentCommandContext => _currentCommandContext.Value;

        public static IDisposable PushCommandContext(KnotLinkCommandContext? context)
        {
            var previous = _currentCommandContext.Value;
            _currentCommandContext.Value = context;
            return new KnotLinkCommandContextScope(previous);
        }

        private sealed class KnotLinkCommandContextScope : IDisposable
        {
            private readonly KnotLinkCommandContext? _previous;
            private bool _disposed;

            public KnotLinkCommandContextScope(KnotLinkCommandContext? previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                if (_disposed) return;

                _currentCommandContext.Value = _previous;
                _disposed = true;
            }
        }

        #endregion

        #region 初始化与销毁

        /// <summary>
        /// 初始化 KnotLink 服务
        /// </summary>
        public static void Initialize()
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            if (settings == null) return;

            _isEnabled = settings.EnableKnotLink;
            if (!_isEnabled)
            {
                LogService.Log(I18n.GetString("KnotLink_Disabled_SkipInit"));
                return;
            }

            lock (_initLock)
            {
                if (IsInitialized) return;

                try
                {
                    var host = string.IsNullOrWhiteSpace(settings.KnotLinkHost) ? "127.0.0.1" : settings.KnotLinkHost.Trim();
                    var appId = string.IsNullOrWhiteSpace(settings.KnotLinkAppId) ? DefaultAppId : settings.KnotLinkAppId.Trim();
                    var openSocketId = string.IsNullOrWhiteSpace(settings.KnotLinkOpenSocketId) ? DefaultOpenSocketId : settings.KnotLinkOpenSocketId.Trim();
                    var signalId = string.IsNullOrWhiteSpace(settings.KnotLinkSignalId) ? DefaultSignalId : settings.KnotLinkSignalId.Trim();

                    _activeAppId = appId;
                    _activeOpenSocketId = openSocketId;
                    _activeSignalId = signalId;

                    // 初始化信号发送器（用于广播事件）
                    var senderInitialized = InitializeSignalSender(appId, signalId, host, logFailure: true);

                    // 初始化命令响应器（用于接收远程命令）
                    var responserInitialized = InitializeCommandResponser(appId, openSocketId, host, logFailure: true);

                    // KnotLink SDK 2.0 does not expose a durable connection-state contract.
                    // In particular, its transport read loop may end after the server closes a
                    // role connection, so that low-level state must not redefine whether the
                    // host service completed initialization.
                    _initializationState.Record(senderInitialized, responserInitialized);
                    if (IsInitialized)
                    {
                        LogService.Log(I18n.Format("KnotLink_InitSuccess", appId, host));
                        BroadcastStartupEvent();
                    }
                }
                catch (Exception ex)
                {
                    _initializationState.Reset();
                    LogService.Log(I18n.Format("KnotLink_InitFailed", ex.Message));
                }
            }
        }

        /// <summary>
        /// 初始化信号发送器
        /// </summary>
        private static bool InitializeSignalSender(string appId, string signalId, string host, bool logFailure)
        {
            try
            {
                try { _signalSender?.Dispose(); } catch { }
                _signalSender = new SignalSender(appId, signalId, host);
                _signalSender.InitializeAsync().GetAwaiter().GetResult();
                _signalSender.OnErrorAsync = ex => HandleTransportErrorAsync("SignalSender", ex);
                LogService.Log(I18n.GetString("KnotLink_SenderInitSuccess"));
                return true;
            }
            catch (Exception ex)
            {
                _signalSender = null;
                if (logFailure)
                {
                    LogService.Log(I18n.Format("KnotLink_SenderInitFailed", ex.Message));
                }
                return false;
            }
        }

        /// <summary>
        /// 初始化命令响应器
        /// </summary>
        private static bool InitializeCommandResponser(string appId, string openSocketId, string host, bool logFailure)
        {
            try
            {
                try { _commandResponser?.Dispose(); } catch { }
                _commandResponser = new OpenSocketResponser(
                    appId,
                    openSocketId,
                    host,
                    onQuestionAsync: HandleQuestionAsync);
                _commandResponser.InitializeAsync().GetAwaiter().GetResult();
                _commandResponser.OnErrorAsync = ex => HandleTransportErrorAsync("OpenSocketResponser", ex);
                LogService.Log(I18n.GetString("KnotLink_ResponderInitSuccess"));
                return true;
            }
            catch (Exception ex)
            {
                _commandResponser = null;
                if (logFailure)
                {
                    LogService.Log(I18n.Format("KnotLink_ResponderInitFailed", ex.Message));
                }
                return false;
            }
        }

        private static async Task<string> HandleQuestionAsync(string question)
        {
            LogService.Log(I18n.Format("KnotLink_CommandReceived", question));
            var response = await ProcessCommandAsync(question).ConfigureAwait(false);
            LogService.Log(I18n.Format("KnotLink_CommandResponse", response));
            return response;
        }

        private static Task HandleTransportErrorAsync(string component, Exception exception)
        {
            LogService.LogWarning($"KnotLink {component} transport error: {exception.Message}", "KnotLink");
            return Task.CompletedTask;
        }

        private static void BroadcastStartupEvent()
        {
            BroadcastEvent(null, "app_startup", new Dictionary<string, string?>
            {
                ["version"] = GetAppVersion()
            });
        }

        /// <summary>
        /// 关闭 KnotLink 服务
        /// </summary>
        public static void Shutdown()
        {
            lock (_initLock)
            {
                _isEnabled = false;

                // 停止所有自动备份任务
                foreach (var kvp in _activeAutoBackups)
                {
                    kvp.Value.Cancel();
                }
                _activeAutoBackups.Clear();

                // 释放资源
                try { _signalSender?.Dispose(); } catch { }
                try { _commandResponser?.Dispose(); } catch { }

                _signalSender = null;
                _commandResponser = null;
                _initializationState.Reset();

                LogService.Log(I18n.GetString("KnotLink_Shutdown"));
            }
        }

        /// <summary>
        /// 重启服务（配置更改后调用）
        /// </summary>
        public static void Restart()
        {
            Shutdown();
            Initialize();
        }

        #endregion

        #region 事件广播

        /// <summary>
        /// 广播事件消息
        /// 对应 MineBackup 的 BroadcastEvent 函数
        /// </summary>
        public static void BroadcastEvent(string eventData)
        {
            _ = BroadcastEventAsync(eventData);
        }

        /// <summary>
        /// 广播事件（可 await）。
        /// </summary>
        public static async Task BroadcastEventAsync(string eventData)
        {
            await TryBroadcastEventAsync(eventData).ConfigureAwait(false);
        }

        /// <summary>
        /// 尝试广播事件，并返回消息是否已成功交给 KnotLink 发送器。
        /// </summary>
        public static async Task<bool> TryBroadcastEventAsync(string eventData)
        {
            if (_signalSender == null || !IsEnabled) return false;

            try
            {
                await _signalSender.EmitAsync(NormalizeEventData(eventData)).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("KnotLink_BroadcastFailed", ex.Message));
                return false;
            }
        }

        public static void BroadcastEvent(
            KnotLinkCommandContext? context,
            string eventName,
            IReadOnlyDictionary<string, string?>? fields = null)
        {
            if (string.IsNullOrWhiteSpace(eventName)) return;

            BroadcastEvent(FormatBroadcastEventData(context, eventName, fields));
        }

        public static Task BroadcastEventAsync(
            KnotLinkCommandContext? context,
            string eventName,
            IReadOnlyDictionary<string, string?>? fields = null)
        {
            if (string.IsNullOrWhiteSpace(eventName))
            {
                return Task.CompletedTask;
            }

            return BroadcastEventAsync(FormatBroadcastEventData(context, eventName, fields));
        }

        public static Task<bool> TryBroadcastEventAsync(
            KnotLinkCommandContext? context,
            string eventName,
            IReadOnlyDictionary<string, string?>? fields = null)
        {
            if (string.IsNullOrWhiteSpace(eventName))
            {
                return Task.FromResult(false);
            }

            return TryBroadcastEventAsync(FormatBroadcastEventData(context, eventName, fields));
        }

        public static void BroadcastCommandLifecycle(
            KnotLinkCommandContext? context,
            string lifecycleEvent,
            IReadOnlyDictionary<string, string?>? fields = null)
        {
            if (string.IsNullOrWhiteSpace(lifecycleEvent)) return;

            var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(context?.Command))
            {
                merged["command"] = context.Command;
            }

            if (fields != null)
            {
                foreach (var pair in fields)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key))
                    {
                        continue;
                    }

                    merged[pair.Key] = pair.Value;
                }
            }

            BroadcastEvent(context, lifecycleEvent, merged);
        }

        private static string FormatBroadcastEventData(
            KnotLinkCommandContext? context,
            string eventName,
            IReadOnlyDictionary<string, string?>? fields)
        {
            return KnotLinkProtocolFormatter.FormatEvent(context, eventName, fields);
        }

        private static string NormalizeEventData(string eventData)
        {
            if (string.IsNullOrWhiteSpace(eventData))
            {
                throw new KnotLinkCommandParseException("Signal payload is empty.");
            }

            try
            {
                var parsed = KnotLinkKeyValueCodec.Parse(eventData);
                return KnotLinkKeyValueCodec.Serialize(parsed.Values.Select(
                    pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)));
            }
            catch (KnotLinkCommandParseException)
            {
                // Transitional safety for in-process callers: normalize their old hand-built
                // key-value strings before anything reaches the KnotLink wire.
                var fields = new List<KeyValuePair<string, string?>>();
                foreach (var segment in eventData.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var separator = segment.IndexOf('=');
                    if (separator <= 0)
                    {
                        throw new KnotLinkCommandParseException($"Invalid signal segment: {segment}");
                    }

                    var rawValue = segment[(separator + 1)..];
                    string value;
                    try { value = Uri.UnescapeDataString(rawValue); }
                    catch { value = rawValue; }
                    fields.Add(new(segment[..separator], value));
                }

                return KnotLinkKeyValueCodec.Serialize(fields);
            }
        }

        /// <summary>
        /// 订阅指定信号频道。
        /// </summary>
        public static IDisposable? SubscribeSignal(string signalId, Func<string, Task> onSignal)
        {
            if (!IsEnabled) return null;
            if (string.IsNullOrWhiteSpace(signalId)) return null;

            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            if (settings == null) return null;

            var host = string.IsNullOrWhiteSpace(settings.KnotLinkHost) ? "127.0.0.1" : settings.KnotLinkHost;
            var appId = string.IsNullOrWhiteSpace(settings.KnotLinkAppId) ? DefaultAppId : settings.KnotLinkAppId;

            try
            {
                var sub = new SignalSubscriber(appId, signalId, host, onSignalAsync: onSignal);
                sub.InitializeAsync().GetAwaiter().GetResult();
                sub.OnErrorAsync = ex => HandleTransportErrorAsync("SignalSubscriber", ex);
                return sub;
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("KnotLink_BroadcastFailed", ex.Message));
                return null;
            }
        }

        /// <summary>
        /// 主动向 KnotLink OpenSocket 发起查询（用于插件/热键触发的联动）。
        /// </summary>
        public static Task<string> QueryAsync(string question, int timeoutMs = 5000)
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            if (settings == null) return Task.FromResult("ERROR:Config not loaded.");
            if (!IsEnabled) return Task.FromResult("ERROR:KnotLink disabled.");

            var host = string.IsNullOrWhiteSpace(settings.KnotLinkHost) ? "127.0.0.1" : settings.KnotLinkHost;
            var appId = string.IsNullOrWhiteSpace(settings.KnotLinkAppId) ? DefaultAppId : settings.KnotLinkAppId;
            var openSocketId = string.IsNullOrWhiteSpace(settings.KnotLinkOpenSocketId) ? DefaultOpenSocketId : settings.KnotLinkOpenSocketId;

            return OpenSocketQueryAdapter.QueryAsync(appId, openSocketId, question, host, 6376, timeoutMs);
        }

        #endregion
    }
}

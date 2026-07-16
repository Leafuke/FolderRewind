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
    public static class KnotLinkService
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
        private static bool _isInitialized;
        private static bool _isEnabled;

        // 自动备份任务管理（对应 MineBackup 的 g_active_auto_backups）
        private static readonly ConcurrentDictionary<(string configId, string folderPath), CancellationTokenSource> _activeAutoBackups = new();
        private static readonly AsyncLocal<KnotLinkCommandContext?> _currentCommandContext = new();

        #endregion

        #region 公开属性

        /// <summary>
        /// 服务是否已启用
        /// </summary>
        public static bool IsEnabled => _isEnabled;

        /// <summary>
        /// 服务是否已初始化
        /// </summary>
        public static bool IsInitialized => _isInitialized;

        /// <summary>
        /// 命令响应器是否正在运行
        /// </summary>
        public static bool IsResponserRunning => _commandResponser != null;

        /// <summary>
        /// 信号发送器是否正在运行
        /// </summary>
        public static bool IsSenderRunning => _signalSender != null;

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
                if (_isInitialized) return;

                try
                {
                    const string host = "127.0.0.1";
                    const string appId = DefaultAppId;
                    const string openSocketId = DefaultOpenSocketId;
                    const string signalId = DefaultSignalId;

                    _activeAppId = appId;
                    _activeOpenSocketId = openSocketId;
                    _activeSignalId = signalId;

                    // 初始化信号发送器（用于广播事件）
                    InitializeSignalSender(appId, signalId, host);

                    // 初始化命令响应器（用于接收远程命令）
                    InitializeCommandResponser(appId, openSocketId, host);

                    _isInitialized = true;
                    LogService.Log(I18n.Format("KnotLink_InitSuccess", appId, host));
                    BroadcastEvent(null, "app_startup", new Dictionary<string, string?>
                    {
                        ["version"] = GetAppVersion()
                    });
                }
                catch (Exception ex)
                {
                    LogService.Log(I18n.Format("KnotLink_InitFailed", ex.Message));
                }
            }
        }

        /// <summary>
        /// 初始化信号发送器
        /// </summary>
        private static void InitializeSignalSender(string appId, string signalId, string host)
        {
            try
            {
                _signalSender = new SignalSender(appId, signalId, host);
                LogService.Log(I18n.GetString("KnotLink_SenderInitSuccess"));
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("KnotLink_SenderInitFailed", ex.Message));
            }
        }

        /// <summary>
        /// 初始化命令响应器
        /// </summary>
        private static void InitializeCommandResponser(string appId, string openSocketId, string host)
        {
            try
            {
                _commandResponser = new OpenSocketResponser(appId, openSocketId, host);
                _commandResponser.OnQuestionAsync = async question =>
                {
                    LogService.Log(I18n.Format("KnotLink_CommandReceived", question));
                    var response = await ProcessCommandAsync(question);
                    LogService.Log(I18n.Format("KnotLink_CommandResponse", response));
                    return response;
                };
                LogService.Log(I18n.GetString("KnotLink_ResponderInitSuccess"));
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("KnotLink_ResponderInitFailed", ex.Message));
            }
        }

        /// <summary>
        /// 关闭 KnotLink 服务
        /// </summary>
        public static void Shutdown()
        {
            lock (_initLock)
            {
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
                _isInitialized = false;

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
            if (_signalSender == null || !_isEnabled) return;

            try
            {
                await _signalSender.EmitAsync(NormalizeEventData(eventData)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("KnotLink_BroadcastFailed", ex.Message));
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
            if (!_isEnabled) return null;
            if (string.IsNullOrWhiteSpace(signalId)) return null;

            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            if (settings == null) return null;

            var host = string.IsNullOrWhiteSpace(settings.KnotLinkHost) ? "127.0.0.1" : settings.KnotLinkHost;
            var appId = string.IsNullOrWhiteSpace(settings.KnotLinkAppId) ? DefaultAppId : settings.KnotLinkAppId;

            try
            {
                var sub = new SignalSubscriber(appId, signalId, host);
                sub.OnSignalAsync = onSignal;
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
            if (!_isEnabled) return Task.FromResult("ERROR:KnotLink disabled.");

            var host = string.IsNullOrWhiteSpace(settings.KnotLinkHost) ? "127.0.0.1" : settings.KnotLinkHost;
            var appId = string.IsNullOrWhiteSpace(settings.KnotLinkAppId) ? DefaultAppId : settings.KnotLinkAppId;
            var openSocketId = string.IsNullOrWhiteSpace(settings.KnotLinkOpenSocketId) ? DefaultOpenSocketId : settings.KnotLinkOpenSocketId;

            return FolderRewind.Services.KnotLink.OpenSocketQuerier.QueryAsync(appId, openSocketId, question, host, 6376, timeoutMs);
        }

        #endregion

        #region 命令处理

        /// <summary>
        /// 处理远程命令
        /// 参考 MineBackup Console.cpp 的 ProcessCommand 函数
        /// </summary>
        private static async Task<string> ProcessCommandAsync(string commandStr)
        {
            var unsupportedProtocolError = GetUnsupportedProtocolError(commandStr);
            if (unsupportedProtocolError != null)
            {
                LogService.LogWarning(unsupportedProtocolError[6..], "KnotLink");
                return unsupportedProtocolError;
            }

            KnotLinkCommandRequest request;
            try
            {
                request = KnotLinkCommandParser.Parse(commandStr);
            }
            catch (KnotLinkCommandParseException ex)
            {
                LogService.LogError(I18n.Format("KnotLink_CommandParseFailed_Log", ex.Message), "KnotLink", ex);
                return KnotLinkKeyValueCodec.Serialize(new Dictionary<string, string?>
                {
                    ["status"] = "error",
                    ["message"] = ex.Message
                });
            }

            var context = new KnotLinkCommandContext(request);
            var command = request.Command;

            try
            {
                var validation = KnotLinkCommandValidator.Validate(context);
                if (!validation.IsValid)
                {
                    return FormatValidationError(context, validation);
                }

                if (KnotLinkCommandValidator.RequiresConversationMetadata(context.Command))
                {
                    BroadcastCommandLifecycle(context, "command_accepted");
                }

                if (!string.Equals(command, "GET_CAPABILITIES", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(command, "PING", StringComparison.OrdinalIgnoreCase))
                {
                    var (pluginHandled, pluginResponse) = await PluginService.TryHandleParameterizedKnotLinkCommandAsync(context).ConfigureAwait(false);
                    if (pluginHandled)
                    {
                        return FormatCommandHandlerResponse(context, pluginResponse);
                    }
                }

                var response = await HandleV2CommandAsync(context).ConfigureAwait(false);
                return response != null
                    ? FormatCommandHandlerResponse(context, response)
                    : KnotLinkProtocolFormatter.FormatError(context, $"Unknown command '{command}'.");
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("KnotLink_CommandExecutionFailed_Log", command, ex.Message), "KnotLink", ex);
                NotificationService.ShowError(I18n.Format("KnotLink_CommandFailed_Notification", ex.Message));
                if (context.Metadata.HasConversation)
                {
                    BroadcastCommandLifecycle(context, "command_failed", new Dictionary<string, string?>
                    {
                        ["reason"] = "exception",
                        ["error"] = ex.Message
                    });
                    BroadcastEvent(context, "command_error", new Dictionary<string, string?>
                    {
                        ["command"] = command,
                        ["error"] = ex.Message
                    });
                    return KnotLinkProtocolFormatter.FormatError(context, ex.Message);
                }

                BroadcastEvent(context, "command_error", new Dictionary<string, string?>
                {
                    ["command"] = command,
                    ["error"] = ex.Message
                });
                return KnotLinkProtocolFormatter.FormatError(context, ex.Message);
            }
        }

        private static string? GetUnsupportedProtocolError(string? payload) =>
            KnotLinkCommandParser.HasV2CommandField(payload)
                ? null
                : "ERROR:KnotLink v1 is no longer supported. Please upgrade the caller to protocol v2 (cmd=...).";

        private static async Task<string?> HandleV2CommandAsync(KnotLinkCommandContext context)
        {
            var request = context.Request;
            return request.Command switch
            {
                "LIST_CONFIGS" => await HandleListConfigs(context),
                "LIST_FOLDERS" => await HandleListFolders(context),
                "LIST_BACKUPS" => await HandleListBackups(context),
                "GET_CONFIG" => await HandleGetConfig(context),
                "GET_STATUS" => await HandleGetStatus(context),
                "GET_CAPABILITIES" => HandleGetCapabilities(context),
                "PING" => HandlePing(),
                "BACKUP" => await HandleBackup(context),
                "RESTORE" => await HandleRestore(context),
                "BACKUP_ALL" => await HandleBackupAll(context),
                "AUTO_BACKUP" => await HandleAutoBackup(context),
                "STOP_AUTO_BACKUP" => await HandleStopAutoBackup(context),
                "MARK_IMPORTANT" => await HandleMarkImportant(context),
                _ => null
            };
        }

        private static string FormatValidationError(KnotLinkCommandContext context, KnotLinkCommandValidationResult validation)
        {
            var message = validation.Error switch
            {
                KnotLinkCommandValidationError.MissingConversationMetadata =>
                    I18n.Format("KnotLink_Error_MissingConversationMetadata", string.Join(", ", validation.MissingMetadataKeys)),
                _ => "Invalid parameterized command."
            };

            return KnotLinkProtocolFormatter.FormatError(context, message);
        }

        private static string FormatCommandHandlerResponse(KnotLinkCommandContext context, string response)
        {
            if (response.StartsWith("status=", StringComparison.OrdinalIgnoreCase)) return response;

            if (response.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)
                && KnotLinkCommandValidator.RequiresConversationMetadata(context.Command)
                && context.Metadata.HasConversation)
            {
                BroadcastCommandLifecycle(context, "command_failed", new Dictionary<string, string?>
                {
                    ["reason"] = "handler_error",
                    ["message"] = response[6..]
                });
            }

            return KnotLinkProtocolFormatter.FormatHandlerResponse(
                context,
                response,
                IsDataResponseCommand(context.Command));
        }

        private static bool IsDataResponseCommand(string command)
        {
            return string.Equals(command, "LIST_CONFIGS", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "LIST_FOLDERS", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "LIST_BACKUPS", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "GET_CONFIG", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "GET_STATUS", StringComparison.OrdinalIgnoreCase);
        }


        private static string HandleGetCapabilities(KnotLinkCommandContext? context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var funcList = KnotLinkFuncListService.BuildRuntime(_activeAppId, _activeOpenSocketId, _activeSignalId);
            var json = KnotLinkFuncListService.Serialize(funcList);
            var fields = new Dictionary<string, string?>
            {
                ["content_type"] = "application/json",
                ["encoding"] = "percent",
                ["manifest_version"] = KnotLinkFuncListService.ManifestVersion,
                ["func_list"] = json
            };
            return KnotLinkProtocolFormatter.FormatOk(context, fields);
        }

        #region 命令处理器实现

        /// <summary>
        /// 心跳检测
        /// </summary>
        private static string HandlePing()
        {
            return "OK:PONG";
        }

        private static Task<string> HandleListConfigs(KnotLinkCommandContext context)
        {
            var configs = ConfigService.CurrentConfig?.BackupConfigs;
            var data = configs == null || configs.Count == 0
                ? string.Empty
                : string.Join(';', configs.Select(config => $"{config.Id},{config.Name}"));

            BroadcastEvent(context, "list_configs", new Dictionary<string, string?>
            {
                ["data"] = data
            });
            return Task.FromResult("OK:" + data);
        }

        private static Task<string> HandleListFolders(KnotLinkCommandContext context)
        {
            if (!TryResolveConfig(context.Request, out var config, out var error))
            {
                return Task.FromResult(error);
            }

            var data = string.Join(';', config!.SourceFolders.Select(folder => folder.DisplayName));
            BroadcastEvent(context, "list_folders", new Dictionary<string, string?>
            {
                ["config"] = config.Id,
                ["data"] = data
            });
            return Task.FromResult("OK:" + data);
        }

        private static Task<string> HandleListBackups(KnotLinkCommandContext context)
        {
            var request = context.Request;
            if (!TryResolveConfig(request, out var config, out var error))
            {
                return Task.FromResult(error);
            }

            if (!TryResolveFolder(request, config!, out var folder, out error))
            {
                return Task.FromResult(error);
            }

            var backupDir = Path.Combine(config!.DestinationPath, folder!.DisplayName);
            var data = string.Empty;
            if (Directory.Exists(backupDir))
            {
                var extensions = new[] { ".7z", ".zip" };
                data = string.Join(
                    ';',
                    Directory.GetFiles(backupDir)
                        .Where(file => extensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                        .Select(Path.GetFileName)
                        .Where(file => !string.IsNullOrWhiteSpace(file)));
            }

            BroadcastEvent(context, "list_backups", new Dictionary<string, string?>
            {
                ["config"] = config.Id,
                ["folder"] = folder.DisplayName,
                ["data"] = data
            });
            return Task.FromResult("OK:" + data);
        }

        private static Task<string> HandleGetConfig(KnotLinkCommandContext context)
        {
            if (!TryResolveConfig(context.Request, out var config, out var error))
            {
                return Task.FromResult(error);
            }

            var data = $"name={config!.Name};backup_mode={config.Archive.Mode};format={config.Archive.Format};keep_count={config.Archive.KeepCount}";
            BroadcastEvent(context, "get_config", new Dictionary<string, string?>
            {
                ["config"] = config.Id,
                ["name"] = config.Name,
                ["backup_mode"] = config.Archive.Mode.ToString(),
                ["format"] = config.Archive.Format,
                ["keep_count"] = config.Archive.KeepCount.ToString()
            });
            return Task.FromResult("OK:" + data);
        }

        private static Task<string> HandleGetStatus(KnotLinkCommandContext context)
        {
            var data = $"enabled={_isEnabled};initialized={_isInitialized};active_auto_backups={_activeAutoBackups.Count};active_tasks={BackupService.ActiveTasks.Count}";
            BroadcastEvent(context, "status", new Dictionary<string, string?>
            {
                ["enabled"] = _isEnabled.ToString(),
                ["initialized"] = _isInitialized.ToString(),
                ["active_auto_backups"] = _activeAutoBackups.Count.ToString(),
                ["active_tasks"] = BackupService.ActiveTasks.Count.ToString()
            });
            return Task.FromResult("OK:" + data);
        }

        private static Task<string> HandleBackup(KnotLinkCommandContext context)
        {
            var request = context.Request;
            if (!TryResolveConfig(request, out var config, out var error))
            {
                return Task.FromResult(error);
            }

            if (!TryResolveFolder(request, config!, out var folder, out error))
            {
                return Task.FromResult(error);
            }

            if (!TryGetBoolOption(request, "force_full", false, out var forceFullBackup, out error))
            {
                return Task.FromResult(error);
            }

            var comment = request.GetStringOrDefault("comment");
            var backupBlacklist = request.GetList("backup_blacklist");
            var backupWhitelist = GetBackupWhitelistOptions(request);
            var backupScopeId = request.GetString("backup_scope");
            var backupScopeParameters = GetScopeParameters(request);
            var effectiveConfig = CreateConfigWithOneShotFilters(
                config!,
                backupBlacklist,
                backupWhitelist,
                Array.Empty<string>(),
                backupScopeId,
                backupScopeParameters);
            var effectiveFolder = ResolveEquivalentFolder(effectiveConfig, folder!);

            _ = Task.Run(async () =>
            {
                using var scope = PushCommandContext(context);
                try
                {
                    await BackupService.BackupFolderAsync(
                        effectiveConfig,
                        effectiveFolder,
                        comment,
                        forceFullBackup,
                        BackupInvocationOptions.ForRemote());
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("KnotLink_CommandExecutionFailed_Log", request.Command, ex.Message), "KnotLink", ex);
                    NotificationService.ShowError(I18n.Format("KnotLink_CommandFailed_Notification", ex.Message));
                    BroadcastCommandLifecycle(context, "command_failed", new Dictionary<string, string?>
                    {
                        ["reason"] = "exception",
                        ["error"] = ex.Message
                    });
                    BroadcastEvent(context, "backup_failed", new Dictionary<string, string?>
                    {
                        ["config"] = config!.Id,
                        ["folder"] = folder!.DisplayName,
                        ["error"] = ex.Message
                    });
                }
            });

            return Task.FromResult($"OK:Backup started for folder '{folder!.DisplayName}'");
        }

        private static Task<string> HandleRestore(KnotLinkCommandContext context)
        {
            var request = context.Request;
            if (!TryResolveConfig(request, out var config, out var error))
            {
                return Task.FromResult(error);
            }

            if (!TryResolveFolder(request, config!, out var folder, out error))
            {
                return Task.FromResult(error);
            }

            var backupFile = request.GetString("file");
            if (string.IsNullOrWhiteSpace(backupFile))
            {
                return Task.FromResult("ERROR:" + I18n.GetString("KnotLink_Error_MissingBackupFile"));
            }

            if (!TryResolveRestoreMode(request, out var mode, out error))
            {
                return Task.FromResult(error);
            }

            if (mode == BackupService.RestoreMode.Clean
                && IsPartialBackup(config!, folder!, backupFile!)
                && !request.GetBoolOrDefault("confirm_partial_clean"))
            {
                return Task.FromResult("ERROR:" + I18n.GetString("KnotLink_Error_PartialCleanRequiresConfirm"));
            }

            var restoreWhitelist = request.GetList("restore_whitelist");
            var effectiveConfig = CreateConfigWithOneShotFilters(config!, Array.Empty<string>(), Array.Empty<string>(), restoreWhitelist);
            var effectiveFolder = ResolveEquivalentFolder(effectiveConfig, folder!);

            _ = Task.Run(async () =>
            {
                using var scope = PushCommandContext(context);
                try
                {
                    await BackupService.RestoreBackupAsync(effectiveConfig, effectiveFolder, backupFile!, mode);
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("KnotLink_CommandExecutionFailed_Log", request.Command, ex.Message), "KnotLink", ex);
                    NotificationService.ShowError(I18n.Format("KnotLink_CommandFailed_Notification", ex.Message));
                    BroadcastCommandLifecycle(context, "command_failed", new Dictionary<string, string?>
                    {
                        ["reason"] = "exception",
                        ["error"] = ex.Message
                    });
                    BroadcastEvent(context, "restore_failed", new Dictionary<string, string?>
                    {
                        ["config"] = config!.Id,
                        ["folder"] = folder!.DisplayName,
                        ["error"] = ex.Message
                    });
                }
            });

            return Task.FromResult($"OK:Restore started for folder '{folder!.DisplayName}'");
        }

        private static Task<string> HandleBackupAll(KnotLinkCommandContext context)
        {
            var request = context.Request;
            if (!TryResolveConfig(request, out var config, out var error))
            {
                return Task.FromResult(error);
            }

            if (!TryGetBoolOption(request, "force_full", false, out var forceFullBackup, out error))
            {
                return Task.FromResult(error);
            }

            var comment = request.GetStringOrDefault("comment");
            var backupBlacklist = request.GetList("backup_blacklist");
            var backupWhitelist = GetBackupWhitelistOptions(request);
            var backupScopeId = request.GetString("backup_scope");
            var backupScopeParameters = GetScopeParameters(request);
            var effectiveConfig = CreateConfigWithOneShotFilters(
                config!,
                backupBlacklist,
                backupWhitelist,
                Array.Empty<string>(),
                backupScopeId,
                backupScopeParameters);

            BroadcastEvent(context, "backup_all_started", new Dictionary<string, string?>
            {
                ["config"] = config!.Id
            });

            _ = Task.Run(async () =>
            {
                using var scope = PushCommandContext(context);
                try
                {
                    BroadcastCommandLifecycle(context, "command_started");
                    bool anyNewBackup = false;
                    if (forceFullBackup || !string.IsNullOrWhiteSpace(comment))
                    {
                        foreach (var folder in effectiveConfig.SourceFolders)
                        {
                            var hasNewBackup = await BackupService.BackupFolderAsync(
                                effectiveConfig,
                                folder,
                                comment,
                                forceFullBackup,
                                BackupInvocationOptions.ForRemote());
                            anyNewBackup = anyNewBackup || hasNewBackup;
                        }
                    }
                    else
                    {
                        anyNewBackup = await BackupService.BackupConfigAsync(
                            effectiveConfig,
                            BackupInvocationOptions.ForRemote());
                    }

                    var result = anyNewBackup ? "created" : "no_changes";
                    BroadcastEvent(context, "backup_all_completed", new Dictionary<string, string?>
                    {
                        ["config"] = config.Id,
                        ["result"] = result
                    });
                    BroadcastCommandLifecycle(context, "command_completed", new Dictionary<string, string?>
                    {
                        ["result"] = result
                    });
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("KnotLink_CommandExecutionFailed_Log", request.Command, ex.Message), "KnotLink", ex);
                    NotificationService.ShowError(I18n.Format("KnotLink_CommandFailed_Notification", ex.Message));
                    BroadcastEvent(context, "backup_all_failed", new Dictionary<string, string?>
                    {
                        ["config"] = config.Id,
                        ["error"] = ex.Message
                    });
                    BroadcastCommandLifecycle(context, "command_failed", new Dictionary<string, string?>
                    {
                        ["reason"] = "exception",
                        ["error"] = ex.Message
                    });
                }
            });

            return Task.FromResult($"OK:Backup all started for config '{config.Name}'");
        }

        private static Task<string> HandleAutoBackup(KnotLinkCommandContext context)
        {
            var request = context.Request;
            if (!TryResolveConfig(request, out var config, out var error)) return Task.FromResult(error);
            if (!TryResolveFolder(request, config!, out var folder, out error)) return Task.FromResult(error);

            var intervalText = request.GetString("interval_minutes");
            if (!int.TryParse(intervalText, out var intervalMinutes) || intervalMinutes < 1)
            {
                return Task.FromResult("ERROR:" + I18n.GetString("KnotLink_Error_InvalidInterval"));
            }

            var taskKey = (config!.Id, folder!.Path);
            var cts = new CancellationTokenSource();
            if (!_activeAutoBackups.TryAdd(taskKey, cts))
            {
                cts.Dispose();
                return Task.FromResult("ERROR:An auto-backup task is already running for this folder.");
            }

            _ = Task.Run(async () =>
            {
                LogService.Log(I18n.Format("KnotLink_AutoBackupStarted", folder.DisplayName, intervalMinutes));
                BroadcastCommandLifecycle(context, "command_started");

                try
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), cts.Token);
                            if (cts.Token.IsCancellationRequested) break;

                            LogService.Log(I18n.Format("KnotLink_AutoBackupExecute", folder.DisplayName));
                            using (PushCommandContext(context))
                            {
                                await BackupService.BackupFolderAsync(
                                    config,
                                    folder,
                                    "Auto backup via KnotLink",
                                    invocationOptions: BackupInvocationOptions.ForAutomatic());
                            }
                            BroadcastEvent(context, "auto_backup_executed", new Dictionary<string, string?>
                            {
                                ["config"] = config.Id,
                                ["folder"] = folder.DisplayName
                            });
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            LogService.Log(I18n.Format("KnotLink_AutoBackupFailed", ex.Message));
                            NotificationService.ShowError(I18n.Format("KnotLink_CommandFailed_Notification", ex.Message));
                            BroadcastEvent(context, "auto_backup_error", new Dictionary<string, string?>
                            {
                                ["config"] = config.Id,
                                ["folder"] = folder.DisplayName,
                                ["error"] = ex.Message
                            });
                        }
                    }
                }
                finally
                {
                    _activeAutoBackups.TryRemove(taskKey, out _);
                    LogService.Log(I18n.Format("KnotLink_AutoBackupStopped", folder.DisplayName));
                    BroadcastCommandLifecycle(context, "command_completed");
                }
            });

            BroadcastEvent(context, "auto_backup_started", new Dictionary<string, string?>
            {
                ["config"] = config.Id,
                ["folder"] = folder.DisplayName,
                ["interval"] = intervalMinutes.ToString()
            });
            return Task.FromResult($"OK:Auto-backup started for folder '{folder.DisplayName}' with interval of {intervalMinutes} minutes.");
        }

        private static Task<string> HandleStopAutoBackup(KnotLinkCommandContext context)
        {
            var request = context.Request;
            if (!TryResolveConfig(request, out var config, out var error)) return Task.FromResult(error);
            if (!TryResolveFolder(request, config!, out var folder, out error)) return Task.FromResult(error);

            var taskKey = (config!.Id, folder!.Path);
            if (!_activeAutoBackups.TryRemove(taskKey, out var cts))
            {
                return Task.FromResult("ERROR:No active auto-backup task found for this folder.");
            }

            cts.Cancel();
            BroadcastEvent(context, "auto_backup_stopped", new Dictionary<string, string?>
            {
                ["config"] = config.Id,
                ["folder"] = folder.DisplayName
            });
            BroadcastCommandLifecycle(context, "command_completed");
            return Task.FromResult($"OK:Auto-backup task for folder '{folder.DisplayName}' has been stopped.");
        }

        private static Task<string> HandleMarkImportant(KnotLinkCommandContext context)
        {
            var request = context.Request;
            if (!TryGetBoolOption(request, "important", true, out var isImportant, out var error)) return Task.FromResult(error);

            var backupFile = request.GetString("file");
            if (!TryResolveConfig(request, out var config, out error)) return Task.FromResult(error);
            if (!TryResolveFolder(request, config!, out var folder, out error)) return Task.FromResult(error);
            if (string.IsNullOrWhiteSpace(backupFile)) return Task.FromResult("ERROR:" + I18n.GetString("KnotLink_Error_MissingBackupFile"));

            bool success = HistoryService.SetImportant(config!.Id, folder!.DisplayName, backupFile!, isImportant);
            if (!success) return Task.FromResult($"ERROR:Backup entry not found: {backupFile}");

            var action = isImportant ? "marked as important" : "unmarked";
            BroadcastEvent(context, "mark_important", new Dictionary<string, string?>
            {
                ["config"] = config.Id,
                ["folder"] = folder.DisplayName,
                ["file"] = backupFile,
                ["important"] = isImportant.ToString()
            });
            BroadcastCommandLifecycle(context, "command_completed");
            return Task.FromResult($"OK:Backup '{backupFile}' {action}");
        }
        #endregion

        #endregion

        #region 辅助方法

        private static string? GetConfigOption(KnotLinkCommandRequest request)
            => request.GetString("config_id");

        private static string? GetFolderOption(KnotLinkCommandRequest request)
            => request.GetString("folder");

        private static bool TryResolveConfig(KnotLinkCommandRequest request, out BackupConfig? config, out string error)
        {
            config = null;
            error = string.Empty;

            var configId = GetConfigOption(request);
            if (string.IsNullOrWhiteSpace(configId))
            {
                error = "ERROR:" + I18n.GetString("KnotLink_Error_MissingConfigId");
                return false;
            }

            config = FindConfigById(configId);
            if (config == null)
            {
                error = $"ERROR:Config not found: {configId}";
                return false;
            }

            return true;
        }

        private static bool TryResolveFolder(KnotLinkCommandRequest request, BackupConfig config, out ManagedFolder? folder, out string error)
        {
            folder = null;
            error = string.Empty;

            var folderArg = GetFolderOption(request);
            if (string.IsNullOrWhiteSpace(folderArg))
            {
                error = "ERROR:" + I18n.GetString("KnotLink_Error_MissingFolderName");
                return false;
            }

            folder = FindFolderByIndexOrName(config, folderArg);
            if (folder == null)
            {
                error = $"ERROR:Folder not found: {folderArg}";
                return false;
            }

            return true;
        }

        private static bool TryGetBoolOption(KnotLinkCommandRequest request, string key, bool defaultValue, out bool value, out string error)
        {
            error = string.Empty;
            value = defaultValue;

            if (!request.HasOption(key))
            {
                return true;
            }

            var parsed = request.GetBool(key);
            if (parsed == null)
            {
                error = $"ERROR:{I18n.Format("KnotLink_Error_InvalidBool", key, request.GetString(key) ?? string.Empty)}";
                return false;
            }

            value = parsed.Value;
            return true;
        }

        private static bool TryResolveRestoreMode(KnotLinkCommandRequest request, out BackupService.RestoreMode mode, out string error)
        {
            mode = BackupService.RestoreMode.Overwrite;
            error = string.Empty;

            var modeText = request.GetString("mode");
            if (string.IsNullOrWhiteSpace(modeText))
            {
                return true;
            }

            if (string.Equals(modeText, "clean", StringComparison.OrdinalIgnoreCase))
            {
                mode = BackupService.RestoreMode.Clean;
                return true;
            }

            if (string.Equals(modeText, "overwrite", StringComparison.OrdinalIgnoreCase))
            {
                mode = BackupService.RestoreMode.Overwrite;
                return true;
            }

            error = $"ERROR:{I18n.Format("KnotLink_Error_InvalidRestoreMode", modeText)}";
            return false;
        }

        private static BackupConfig CreateConfigWithOneShotFilters(
            BackupConfig source,
            IReadOnlyList<string> backupBlacklist,
            IReadOnlyList<string> backupWhitelist,
            IReadOnlyList<string> restoreWhitelist,
            string? backupScopeId = null,
            IReadOnlyDictionary<string, string>? backupScopeParameters = null)
        {
            var needsClone = (backupBlacklist?.Count ?? 0) > 0
                || (backupWhitelist?.Count ?? 0) > 0
                || (restoreWhitelist?.Count ?? 0) > 0
                || !string.IsNullOrWhiteSpace(backupScopeId)
                || (backupScopeParameters?.Count ?? 0) > 0;
            if (!needsClone)
            {
                return source;
            }

            var clone = BackupConfigCloneService.CloneForRuntimeMutation(
                source,
                I18n.GetString("KnotLink_Error_ConfigCloneFailed"),
                ensureBackupScope: true);

            if (backupBlacklist != null)
            {
                foreach (var rule in backupBlacklist.Where(rule => !string.IsNullOrWhiteSpace(rule)))
                {
                    clone.Filters.Blacklist.Add(rule.Trim());
                }
            }

            if (backupWhitelist != null && backupWhitelist.Count > 0)
            {
                clone.Filters.BackupFilterMode = BackupFilterMode.Whitelist;
                foreach (var rule in backupWhitelist.Where(rule => !string.IsNullOrWhiteSpace(rule)))
                {
                    AddDistinctRule(clone.Filters.BackupWhitelist, rule);
                }
            }

            if (restoreWhitelist != null)
            {
                foreach (var rule in restoreWhitelist.Where(rule => !string.IsNullOrWhiteSpace(rule)))
                {
                    AddDistinctRule(clone.Filters.RestoreWhitelist, rule);
                }
            }

            if (!string.IsNullOrWhiteSpace(backupScopeId))
            {
                clone.BackupScope.PluginScopeId = IsFullScopeAlias(backupScopeId)
                    ? string.Empty
                    : backupScopeId.Trim();
            }

            if (backupScopeParameters != null && backupScopeParameters.Count > 0)
            {
                clone.BackupScope.Parameters ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in backupScopeParameters)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key))
                    {
                        continue;
                    }

                    clone.BackupScope.Parameters[pair.Key] = pair.Value ?? string.Empty;
                }
            }

            return clone;
        }

        private static IReadOnlyDictionary<string, string> GetScopeParameters(KnotLinkCommandRequest request)
        {
            const string prefix = "scope_";
            return request.Options
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                               && pair.Key.Length > prefix.Length)
                .ToDictionary(
                    pair => pair.Key[prefix.Length..],
                    pair => pair.Value ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsFullScopeAlias(string value)
        {
            return string.Equals(value, "full", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "default", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<string> GetBackupWhitelistOptions(KnotLinkCommandRequest request)
        {
            return request.GetList("backup_whitelist");
        }

        private static bool IsPartialBackup(BackupConfig config, ManagedFolder folder, string backupFile)
        {
            return HistoryService.TryGetEntry(config.Id, folder.Path, backupFile)?.IsPartialBackup == true;
        }

        private static void AddDistinctRule(ObservableCollection<string> rules, string rule)
        {
            var trimmed = rule.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return;
            }

            if (rules.Any(existing => string.Equals(existing?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            rules.Add(trimmed);
        }

        private static ManagedFolder ResolveEquivalentFolder(BackupConfig effectiveConfig, ManagedFolder originalFolder)
        {
            if (effectiveConfig.SourceFolders.Count == 0)
            {
                return originalFolder;
            }

            return effectiveConfig.SourceFolders.FirstOrDefault(folder =>
                    string.Equals(folder.Path, originalFolder.Path, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(folder.DisplayName, originalFolder.DisplayName, StringComparison.OrdinalIgnoreCase))
                ?? originalFolder;
        }

        /// <summary>
        /// 根据 ID 查找配置
        /// </summary>
        private static BackupConfig? FindConfigById(string idOrName)
        {
            var configs = ConfigService.CurrentConfig?.BackupConfigs;
            if (configs == null) return null;

            // 先按 ID 查找
            var config = configs.FirstOrDefault(c =>
                string.Equals(c.Id, idOrName, StringComparison.OrdinalIgnoreCase));

            // 如果找不到，按名称查找
            if (config == null)
            {
                config = configs.FirstOrDefault(c =>
                    string.Equals(c.Name, idOrName, StringComparison.OrdinalIgnoreCase));
            }

            // 如果还找不到，尝试按索引查找
            if (config == null && int.TryParse(idOrName, out var index) && index >= 0 && index < configs.Count)
            {
                config = configs[index];
            }

            return config;
        }

        /// <summary>
        /// 根据索引或名称查找文件夹
        /// </summary>
        private static ManagedFolder? FindFolderByIndexOrName(BackupConfig config, string indexOrName)
        {
            // 先尝试按索引查找
            if (int.TryParse(indexOrName, out var index) && index >= 0 && index < config.SourceFolders.Count)
            {
                return config.SourceFolders[index];
            }

            // 按显示名称查找
            return config.SourceFolders.FirstOrDefault(f =>
                string.Equals(f.DisplayName, indexOrName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.Path, indexOrName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 获取应用版本
        /// </summary>
        private static string GetAppVersion()
        {
            try
            {
                var version = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"{version.Major}.{version.Minor}.{version.Build}";
            }
            catch
            {
                return "1.0.0";
            }
        }

        #endregion
    }
}

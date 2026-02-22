using FolderRewind.Models;
using FolderRewind.Services.KnotLink;
using FolderRewind.Services.Plugins;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
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
        private const string DefaultAppId = "0x00000020";      // 默认与 MineBackup 保持一致，以便兼容同一联动模组，但是之后最好改为独立 ID
        private const string DefaultOpenSocketId = "0x00000010"; // 命令响应器 Socket ID
        private const string DefaultSignalId = "0x00000020";     // 信号广播 ID

        #endregion

        #region 私有字段

        private static SignalSender? _signalSender;
        private static OpenSocketResponser? _commandResponser;
        private static readonly object _initLock = new();
        private static bool _isInitialized;
        private static bool _isEnabled;

        // 自动备份任务管理（对应 MineBackup 的 g_active_auto_backups）
        private static readonly ConcurrentDictionary<(string configId, string folderPath), CancellationTokenSource> _activeAutoBackups = new();

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
                    var host = string.IsNullOrWhiteSpace(settings.KnotLinkHost) ? "127.0.0.1" : settings.KnotLinkHost;
                    var appId = string.IsNullOrWhiteSpace(settings.KnotLinkAppId) ? DefaultAppId : settings.KnotLinkAppId;
                    var openSocketId = string.IsNullOrWhiteSpace(settings.KnotLinkOpenSocketId) ? DefaultOpenSocketId : settings.KnotLinkOpenSocketId;
                    var signalId = string.IsNullOrWhiteSpace(settings.KnotLinkSignalId) ? DefaultSignalId : settings.KnotLinkSignalId;

                    // 初始化信号发送器（用于广播事件）
                    InitializeSignalSender(appId, signalId, host);

                    // 初始化命令响应器（用于接收远程命令）
                    InitializeCommandResponser(appId, openSocketId, host);

                    _isInitialized = true;
                    LogService.Log(I18n.Format("KnotLink_InitSuccess", appId, host));
                    BroadcastEvent($"event=app_startup;version={GetAppVersion()}");
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
            if (_signalSender == null || !_isEnabled) return;

            try
            {
                _signalSender.EmitAsync(eventData).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("KnotLink_BroadcastFailed", ex.Message));
            }
        }

        /// <summary>
        /// 广播事件（可 await）。
        /// </summary>
        public static async Task BroadcastEventAsync(string eventData)
        {
            if (_signalSender == null || !_isEnabled) return;

            try
            {
                await _signalSender.EmitAsync(eventData).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("KnotLink_BroadcastFailed", ex.Message));
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
            if (string.IsNullOrWhiteSpace(commandStr))
            {
                return "ERROR:Empty command.";
            }

            var parts = commandStr.Trim().Split(' ', 2);
            var command = parts[0].ToUpperInvariant();
            var args = parts.Length > 1 ? parts[1] : string.Empty;

            try
            {
                return command switch
                {
                    "LIST_CONFIGS" => await HandleListConfigs(),
                    "LIST_FOLDERS" => await HandleListFolders(args),
                    "LIST_BACKUPS" => await HandleListBackups(args),
                    "GET_CONFIG" => await HandleGetConfig(args),
                    "SET_CONFIG" => await HandleSetConfig(args),
                    "BACKUP" => await HandleBackup(args),
                    "RESTORE" => await HandleRestore(args),
                    "BACKUP_ALL" => await HandleBackupAll(args),
                    "AUTO_BACKUP" => await HandleAutoBackup(args),
                    "STOP_AUTO_BACKUP" => await HandleStopAutoBackup(args),
                    "GET_STATUS" => await HandleGetStatus(),
                    "PING" => HandlePing(),
                    "SEND" => HandleSend(args),
                    _ => await HandleUnknownCommandViaPluginsAsync(command, args, commandStr)
                };
            }
            catch (Exception ex)
            {
                var errorMsg = $"ERROR:{ex.Message}";
                BroadcastEvent($"event=command_error;command={command};error={ex.Message}");
                return errorMsg;
            }
        }

        private static async Task<string> HandleUnknownCommandViaPluginsAsync(string command, string args, string rawCommand)
        {
            try
            {
                var (handled, response) = await PluginService.TryHandleKnotLinkCommandAsync(command, args, rawCommand).ConfigureAwait(false);
                if (handled)
                {
                    return string.IsNullOrWhiteSpace(response) ? "OK:" : response;
                }
            }
            catch
            {
                // ignore and fallback to unknown
            }

            return $"ERROR:Unknown command '{command}'.";
        }

        #region 命令处理器实现

        /// <summary>
        /// 列出所有备份配置
        /// 对应 LIST_CONFIGS 命令
        /// </summary>
        private static Task<string> HandleListConfigs()
        {
            var configs = ConfigService.CurrentConfig?.BackupConfigs;
            if (configs == null || configs.Count == 0)
            {
                BroadcastEvent("event=list_configs;data=OK:");
                return Task.FromResult("OK:");
            }

            var sb = new StringBuilder("OK:");
            foreach (var config in configs)
            {
                sb.Append($"{config.Id},{config.Name};");
            }

            // 移除最后的分号
            if (sb.Length > 3 && sb[sb.Length - 1] == ';')
            {
                sb.Length--;
            }

            var result = sb.ToString();
            BroadcastEvent($"event=list_configs;data={result}");
            return Task.FromResult(result);
        }

        /// <summary>
        /// 列出指定配置下的所有文件夹
        /// 对应 LIST_FOLDERS 命令（类似 MineBackup 的 LIST_WORLDS）
        /// </summary>
        private static Task<string> HandleListFolders(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                return Task.FromResult("ERROR:Missing config id. Usage: LIST_FOLDERS <config_id>");
            }

            var config = FindConfigById(args.Trim());
            if (config == null)
            {
                return Task.FromResult($"ERROR:Config not found: {args}");
            }

            var sb = new StringBuilder("OK:");
            foreach (var folder in config.SourceFolders)
            {
                sb.Append($"{folder.DisplayName};");
            }

            if (sb.Length > 3 && sb[sb.Length - 1] == ';')
            {
                sb.Length--;
            }

            var result = sb.ToString();
            BroadcastEvent($"event=list_folders;config={config.Id};data={result}");
            return Task.FromResult(result);
        }

        /// <summary>
        /// 列出指定文件夹的备份历史
        /// 对应 LIST_BACKUPS 命令
        /// </summary>
        private static Task<string> HandleListBackups(string args)
        {
            var argParts = args.Split(' ', 2);
            if (argParts.Length < 2)
            {
                return Task.FromResult("ERROR:Invalid arguments. Usage: LIST_BACKUPS <config_id> <folder_index|folder_name>");
            }

            var configId = argParts[0].Trim();
            var folderArg = argParts[1].Trim();

            var config = FindConfigById(configId);
            if (config == null)
            {
                return Task.FromResult($"ERROR:Config not found: {configId}");
            }

            var folder = FindFolderByIndexOrName(config, folderArg);
            if (folder == null)
            {
                return Task.FromResult($"ERROR:Folder not found: {folderArg}");
            }

            // 获取备份目录
            var backupDir = Path.Combine(config.DestinationPath, folder.DisplayName);
            var sb = new StringBuilder("OK:");

            if (Directory.Exists(backupDir))
            {
                var extensions = new[] { ".7z", ".zip" };
                var files = Directory.GetFiles(backupDir)
                    .Where(f => extensions.Contains(Path.GetExtension(f).ToLower()))
                    .Select(Path.GetFileName);

                foreach (var file in files)
                {
                    sb.Append($"{file};");
                }
            }

            if (sb.Length > 3 && sb[sb.Length - 1] == ';')
            {
                sb.Length--;
            }

            var result = sb.ToString();
            BroadcastEvent($"event=list_backups;config={config.Id};folder={folder.DisplayName};data={result}");
            return Task.FromResult(result);
        }

        /// <summary>
        /// 获取配置详情
        /// 对应 GET_CONFIG 命令
        /// </summary>
        private static Task<string> HandleGetConfig(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                return Task.FromResult("ERROR:Missing config id. Usage: GET_CONFIG <config_id>");
            }

            var config = FindConfigById(args.Trim());
            if (config == null)
            {
                return Task.FromResult($"ERROR:Config not found: {args}");
            }

            var result = $"OK:name={config.Name};backup_mode={config.Archive.Mode};format={config.Archive.Format};keep_count={config.Archive.KeepCount}";
            BroadcastEvent($"event=get_config;config={config.Id};{result.Substring(3)}");
            return Task.FromResult(result);
        }

        /// <summary>
        /// 设置配置属性
        /// 对应 SET_CONFIG 命令
        /// </summary>
        private static Task<string> HandleSetConfig(string args)
        {
            var argParts = args.Split(' ', 3);
            if (argParts.Length < 3)
            {
                return Task.FromResult("ERROR:Invalid arguments. Usage: SET_CONFIG <config_id> <key> <value>");
            }

            var configId = argParts[0].Trim();
            var key = argParts[1].Trim().ToLower();
            var value = argParts[2].Trim();

            var config = FindConfigById(configId);
            if (config == null)
            {
                return Task.FromResult($"ERROR:Config not found: {configId}");
            }

            // 根据 key 设置不同的属性
            switch (key)
            {
                case "backup_mode":
                    if (int.TryParse(value, out var mode) && Enum.IsDefined(typeof(BackupMode), mode))
                    {
                        config.Archive.Mode = (BackupMode)mode;
                    }
                    else
                    {
                        return Task.FromResult($"ERROR:Invalid backup_mode value: {value}");
                    }
                    break;

                case "keep_count":
                    if (int.TryParse(value, out var keepCount) && keepCount >= 0)
                    {
                        config.Archive.KeepCount = keepCount;
                    }
                    else
                    {
                        return Task.FromResult($"ERROR:Invalid keep_count value: {value}");
                    }
                    break;

                case "format":
                    if (value == "7z" || value == "zip")
                    {
                        config.Archive.Format = value;
                    }
                    else
                    {
                        return Task.FromResult($"ERROR:Invalid format value: {value}. Use '7z' or 'zip'.");
                    }
                    break;

                default:
                    return Task.FromResult($"ERROR:Unknown key '{key}'.");
            }

            ConfigService.Save();
            var result = $"OK:Set {key} to {value}";
            BroadcastEvent($"event=config_changed;config={configId};key={key};value={value}");
            return Task.FromResult(result);
        }

        /// <summary>
        /// 执行单个文件夹备份
        /// 对应 BACKUP 命令
        /// </summary>
        private static async Task<string> HandleBackup(string args)
        {
            var argParts = args.Split(' ', 3);
            if (argParts.Length < 2)
            {
                return "ERROR:Invalid arguments. Usage: BACKUP <config_id> <folder_index|folder_name> [comment]";
            }

            var configId = argParts[0].Trim();
            var folderArg = argParts[1].Trim();
            var comment = argParts.Length > 2 ? argParts[2].Trim() : string.Empty;

            var config = FindConfigById(configId);
            if (config == null)
            {
                return $"ERROR:Config not found: {configId}";
            }

            var folder = FindFolderByIndexOrName(config, folderArg);
            if (folder == null)
            {
                return $"ERROR:Folder not found: {folderArg}";
            }

            // 在后台线程执行备份
            _ = Task.Run(async () =>
            {
                try
                {
                    await BackupService.BackupFolderAsync(config, folder, comment);
                }
                catch (Exception ex)
                {
                    BroadcastEvent($"event=backup_failed;config={config.Id};world={folder.DisplayName};error={ex.Message}");
                }
            });

            return $"OK:Backup started for folder '{folder.DisplayName}'";
        }

        /// <summary>
        /// 执行还原操作
        /// 对应 RESTORE 命令
        /// </summary>
        private static async Task<string> HandleRestore(string args)
        {
            var argParts = args.Split(' ', 3);
            if (argParts.Length < 3)
            {
                return "ERROR:Invalid arguments. Usage: RESTORE <config_id> <folder_index|folder_name> <backup_file>";
            }

            var configId = argParts[0].Trim();
            var folderArg = argParts[1].Trim();
            var backupFile = argParts[2].Trim();

            var config = FindConfigById(configId);
            if (config == null)
            {
                return $"ERROR:Config not found: {configId}";
            }

            var folder = FindFolderByIndexOrName(config, folderArg);
            if (folder == null)
            {
                return $"ERROR:Folder not found: {folderArg}";
            }

            // 在后台线程执行还原
            _ = Task.Run(async () =>
            {
                try
                {
                    await BackupService.RestoreBackupAsync(config, folder, backupFile);
                }
                catch (Exception ex)
                {
                    BroadcastEvent($"event=restore_failed;config={config.Id};world={folder.DisplayName};error={ex.Message}");
                }
            });

            return $"OK:Restore started for folder '{folder.DisplayName}'";
        }

        /// <summary>
        /// 备份配置下的所有文件夹
        /// 对应 BACKUP_ALL 命令
        /// </summary>
        private static async Task<string> HandleBackupAll(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                return "ERROR:Missing config id. Usage: BACKUP_ALL <config_id> [comment]";
            }

            var argParts = args.Split(' ', 2);
            var configId = argParts[0].Trim();
            var comment = argParts.Length > 1 ? argParts[1].Trim() : string.Empty;

            var config = FindConfigById(configId);
            if (config == null)
            {
                return $"ERROR:Config not found: {configId}";
            }

            // 广播开始事件
            BroadcastEvent($"event=backup_all_started;config={config.Id}");

            // 在后台线程执行
            _ = Task.Run(async () =>
            {
                try
                {
                    await BackupService.BackupConfigAsync(config);
                    BroadcastEvent($"event=backup_all_completed;config={config.Id}");
                }
                catch (Exception ex)
                {
                    BroadcastEvent($"event=backup_all_failed;config={config.Id};error={ex.Message}");
                }
            });

            return $"OK:Backup all started for config '{config.Name}'";
        }

        /// <summary>
        /// 启动自动备份任务
        /// 对应 AUTO_BACKUP 命令
        /// </summary>
        private static Task<string> HandleAutoBackup(string args)
        {
            var argParts = args.Split(' ', 3);
            if (argParts.Length < 3)
            {
                return Task.FromResult("ERROR:Invalid arguments. Usage: AUTO_BACKUP <config_id> <folder_index|folder_name> <interval_minutes>");
            }

            var configId = argParts[0].Trim();
            var folderArg = argParts[1].Trim();
            if (!int.TryParse(argParts[2].Trim(), out var intervalMinutes) || intervalMinutes < 1)
            {
                return Task.FromResult("ERROR:Interval must be at least 1 minute.");
            }

            var config = FindConfigById(configId);
            if (config == null)
            {
                return Task.FromResult($"ERROR:Config not found: {configId}");
            }

            var folder = FindFolderByIndexOrName(config, folderArg);
            if (folder == null)
            {
                return Task.FromResult($"ERROR:Folder not found: {folderArg}");
            }

            var taskKey = (config.Id, folder.Path);

            // 检查是否已有任务在运行
            if (_activeAutoBackups.ContainsKey(taskKey))
            {
                return Task.FromResult("ERROR:An auto-backup task is already running for this folder.");
            }

            // 创建取消令牌
            var cts = new CancellationTokenSource();
            _activeAutoBackups[taskKey] = cts;

            // 启动自动备份线程
            _ = Task.Run(async () =>
            {
                LogService.Log(I18n.Format("KnotLink_AutoBackupStarted", folder.DisplayName, intervalMinutes));

                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), cts.Token);
                        if (cts.Token.IsCancellationRequested) break;

                        LogService.Log(I18n.Format("KnotLink_AutoBackupExecute", folder.DisplayName));
                        await BackupService.BackupFolderAsync(config, folder, "Auto backup via KnotLink");
                        BroadcastEvent($"event=auto_backup_executed;config={config.Id};folder={folder.DisplayName}");
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        LogService.Log(I18n.Format("KnotLink_AutoBackupFailed", ex.Message));
                        BroadcastEvent($"event=auto_backup_error;config={config.Id};folder={folder.DisplayName};error={ex.Message}");
                    }
                }

                _activeAutoBackups.TryRemove(taskKey, out _);
                LogService.Log(I18n.Format("KnotLink_AutoBackupStopped", folder.DisplayName));
            });

            BroadcastEvent($"event=auto_backup_started;config={config.Id};folder={folder.DisplayName};interval={intervalMinutes}");
            return Task.FromResult($"OK:Auto-backup started for folder '{folder.DisplayName}' with interval of {intervalMinutes} minutes.");
        }

        /// <summary>
        /// 停止自动备份任务
        /// 对应 STOP_AUTO_BACKUP 命令
        /// </summary>
        private static Task<string> HandleStopAutoBackup(string args)
        {
            var argParts = args.Split(' ', 2);
            if (argParts.Length < 2)
            {
                return Task.FromResult("ERROR:Invalid arguments. Usage: STOP_AUTO_BACKUP <config_id> <folder_index|folder_name>");
            }

            var configId = argParts[0].Trim();
            var folderArg = argParts[1].Trim();

            var config = FindConfigById(configId);
            if (config == null)
            {
                return Task.FromResult($"ERROR:Config not found: {configId}");
            }

            var folder = FindFolderByIndexOrName(config, folderArg);
            if (folder == null)
            {
                return Task.FromResult($"ERROR:Folder not found: {folderArg}");
            }

            var taskKey = (config.Id, folder.Path);

            if (!_activeAutoBackups.TryRemove(taskKey, out var cts))
            {
                return Task.FromResult("ERROR:No active auto-backup task found for this folder.");
            }

            cts.Cancel();
            BroadcastEvent($"event=auto_backup_stopped;config={config.Id};folder={folder.DisplayName}");
            return Task.FromResult($"OK:Auto-backup task for folder '{folder.DisplayName}' has been stopped.");
        }

        /// <summary>
        /// 获取服务状态
        /// 对应 GET_STATUS 命令
        /// </summary>
        private static Task<string> HandleGetStatus()
        {
            var sb = new StringBuilder("OK:");
            sb.Append($"enabled={_isEnabled};");
            sb.Append($"initialized={_isInitialized};");
            sb.Append($"active_auto_backups={_activeAutoBackups.Count};");
            sb.Append($"active_tasks={BackupService.ActiveTasks.Count}");

            var result = sb.ToString();
            BroadcastEvent($"event=status;{result.Substring(3)}");
            return Task.FromResult(result);
        }

        /// <summary>
        /// 心跳检测
        /// </summary>
        private static string HandlePing()
        {
            return "OK:PONG";
        }

        /// <summary>
        /// 发送自定义事件
        /// 对应 SEND 命令
        /// </summary>
        private static string HandleSend(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                return "ERROR:Missing event data. Usage: SEND <event_data>";
            }

            BroadcastEvent(args);
            return "OK:Event sent";
        }

        #endregion

        #endregion

        #region 辅助方法

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

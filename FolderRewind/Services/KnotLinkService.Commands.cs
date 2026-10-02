using FolderRewind.Models;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.Services.KnotLink;
using FolderRewind.Services.Plugins;
using FolderRewind.Services.Plugins.V3;
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
    public static partial class KnotLinkService
    {
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

                var optionError = KnotLinkCommandValidator.ValidateOptions(request);
                if (optionError is not null) return KnotLinkProtocolFormatter.FormatError(context, optionError);
                if (request.GetBool("current_save") == true)
                {
                    var target = await PluginV3CommandService.ResolveKnotLinkTargetAsync(command, request.Options).ConfigureAwait(false);
                    request = request.WithResolvedTarget(target);
                    context = new KnotLinkCommandContext(request);
                    if (!TryResolveConfig(request, out var resolvedConfig, out var targetError)
                        || !TryResolveFolder(request, resolvedConfig!, out _, out targetError))
                        return KnotLinkProtocolFormatter.FormatError(context, targetError);
                }
                if (KnotLinkCommandValidator.RequiresConversationMetadata(context.Command))
                    BroadcastCommandLifecycle(context, "command_accepted");

                if (request.ResolvedTarget is null && !string.Equals(command, "GET_CAPABILITIES", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(command, "PING", StringComparison.OrdinalIgnoreCase))
                {
                    var (v3Handled, v3Response) = await PluginV3CommandService.TryExecuteKnotLinkAsync(
                        command,
                        context.Request.Options).ConfigureAwait(false);
                    if (v3Handled)
                    {
                        return FormatCommandHandlerResponse(context, v3Response);
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

        private static async Task<string> HandleListBackups(KnotLinkCommandContext context)
        {
            var request = context.Request;
            if (!TryResolveConfig(request, out var config, out var error))
            {
                return error;
            }

            if (!TryResolveFolder(request, config!, out var folder, out error))
            {
                return error;
            }

            if (!Guid.TryParse(folder!.Id, out var sourceGuid) || sourceGuid == Guid.Empty)
                return "ERROR:Managed source has no stable identity.";
            var data = string.Join(';', await NativeHistoryCoreGateway.ListBackupFilesAsync(
                config!.Id,
                new SourceId(sourceGuid)).ConfigureAwait(false));

            BroadcastEvent(context, "list_backups", new Dictionary<string, string?>
            {
                ["config"] = config.Id,
                ["folder"] = folder.DisplayName,
                ["data"] = data
            });
            return "OK:" + data;
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
            var data = $"enabled={IsEnabled};initialized={IsInitialized};active_auto_backups={_activeAutoBackups.Count};active_tasks={BackupService.ActiveTasks.Count}";
            BroadcastEvent(context, "status", new Dictionary<string, string?>
            {
                ["enabled"] = IsEnabled.ToString(),
                ["initialized"] = IsInitialized.ToString(),
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

            if (!TryCreateBackupOperationConfig(request, config!, out var effectiveConfig, out error))
                return Task.FromResult("ERROR:" + error);
            var comment = request.GetStringOrDefault("comment");
            var effectiveFolder = ResolveEquivalentFolder(effectiveConfig, folder!);

            _ = Task.Run(async () =>
            {
                using var scope = PushCommandContext(context);
                try
                {
                    await BackupService.BackupFolderAsync(
                        effectiveConfig,
                        effectiveFolder,
                        BackupInvocationOptions.ForRemote().WithComment(comment));
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

        private static async Task<string> HandleRestore(KnotLinkCommandContext context)
        {
            var request = context.Request;
            if (!TryResolveConfig(request, out var config, out var error))
            {
                return error;
            }

            if (!TryResolveFolder(request, config!, out var folder, out error))
            {
                return error;
            }

            var backupFile = request.GetString("file");
            if (!TryResolveRestoreMode(request, out var mode, out error)) return error;
            var restoreWhitelist = request.GetList("restore_whitelist");
            string[] preservePaths;
            try { preservePaths = RestorePreservePaths.Normalize(request.GetList("restore_preserve_paths")); }
            catch (Exception ex) { return $"ERROR:invalid_preserve_path:{ex.Message}"; }
            try { PathRuleMatcher.ValidateRestoreRules(config!.Filters.RestoreWhitelist.Concat(restoreWhitelist)); }
            catch (Exception ex) { return $"ERROR:invalid_filter_rule:{ex.Message}"; }
            var options = new FolderRewind.Plugin.Abstractions.RestoreRequestOptions
            {
                PreservePlayerDataOverride = request.HasOption("preserve_player_data") ? request.GetBool("preserve_player_data") : null,
                Mode = mode.ToString().ToLowerInvariant(),
                RestoreWhitelist = restoreWhitelist.ToArray(),
                RestorePreservePaths = preservePaths
            };
            if (!Guid.TryParse(folder!.Id, out var sourceGuid) || sourceGuid == Guid.Empty)
                return "ERROR:invalid_source_identity";
            VersionId? versionId = null;
            var effectiveMode = mode;
            if (!string.IsNullOrWhiteSpace(backupFile))
            {
                var version = await NativeHistoryCoreGateway.FindVersionByFileAsync(
                    config!.Id, new SourceId(sourceGuid), backupFile).ConfigureAwait(false);
                if (version is null) return "ERROR:history_version_not_found";
                versionId = version.VersionId;
                if (version.CaptureScope == CaptureScope.PartialSource) effectiveMode = BackupService.RestoreMode.Overwrite;
            }

            _ = Task.Run(async () =>
            {
                using var scope = PushCommandContext(context);
                try
                {
                    BroadcastCommandLifecycle(context, "command_started");
                    BroadcastEvent(context, "restore_started", new Dictionary<string, string?>
                        { ["config"] = config!.Id, ["folder"] = folder!.DisplayName });
                    var restored = versionId.HasValue
                        ? await NativeHistoryApplicationService.RestoreVersionAsync(config!, folder!, versionId.Value,
                            mode, options: options).ConfigureAwait(false)
                        : await NativeHistoryApplicationService.QuickRestoreAsync(config!, folder!, options: options).ConfigureAwait(false);
                    if (!restored.Succeeded)
                        throw new InvalidOperationException(restored.Diagnostic);
                    BroadcastEvent(context, "restore_success", new Dictionary<string, string?>
                        { ["config"] = config!.Id, ["folder"] = folder!.DisplayName, ["result"] = restored.Status.ToString() });
                    BroadcastCommandLifecycle(context, "command_completed", new Dictionary<string, string?>
                        { ["result"] = restored.Status.ToString() });
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

            return
                $"OK:Restore started for folder '{folder!.DisplayName}';" +
                $"requested_mode={mode.ToString().ToLowerInvariant()};" +
                $"effective_mode={effectiveMode.ToString().ToLowerInvariant()}";
        }

        private static Task<string> HandleBackupAll(KnotLinkCommandContext context)
        {
            var request = context.Request;
            if (!TryResolveConfig(request, out var config, out var error))
            {
                return Task.FromResult(error);
            }

            var comment = request.GetStringOrDefault("comment");
            if (!TryCreateBackupOperationConfig(request, config!, out var effectiveConfig, out error))
                return Task.FromResult("ERROR:" + error);

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
                    var outcome = await BackupService.BackupConfigAsync(
                        effectiveConfig,
                        BackupInvocationOptions.ForRemote().WithComment(comment));

                    var result = outcome.ProtocolResult;
                    if (!outcome.IsSuccessful)
                    {
                        BroadcastEvent(context, "backup_all_failed", new Dictionary<string, string?>
                        {
                            ["config"] = config.Id,
                            ["error"] = result
                        });
                        BroadcastCommandLifecycle(context, "command_failed", new Dictionary<string, string?>
                        {
                            ["reason"] = result
                        });
                        return;
                    }
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

            if (!TryCreateBackupOperationConfig(request, config!, out var effectiveConfig, out error))
                return Task.FromResult("ERROR:" + error);
            // Freeze the one-shot options, not the user's mutable configuration instance.
            effectiveConfig = BackupConfigCloneService.CloneForRuntimeMutation(effectiveConfig, "Could not snapshot automatic backup options.");
            var effectiveFolder = ResolveEquivalentFolder(effectiveConfig, folder!);
            var comment = request.GetStringOrDefault("comment", "Auto backup via KnotLink");
            var expectedSignature = NativeHistoryConfigLease.Signature(config!);

            var taskKey = (config!.Id, folder!.Id);
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
                                var registered = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(value => value.Id == config!.Id);
                                if (registered is null || NativeHistoryConfigLease.Signature(registered) != expectedSignature)
                                {
                                    cts.Cancel();
                                    throw new InvalidOperationException("Automatic backup target configuration changed; restart the task.");
                                }
                                await BackupService.BackupFolderAsync(effectiveConfig, effectiveFolder,
                                    BackupInvocationOptions.ForAutomatic().WithComment(comment));
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
                    _activeAutoBackups.TryRemove(new KeyValuePair<(string configId, string folderId), CancellationTokenSource>(taskKey, cts));
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

            var taskKey = (config!.Id, folder!.Id);
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

        private static async Task<string> HandleMarkImportant(KnotLinkCommandContext context)
        {
            var request = context.Request;
            if (!TryGetBoolOption(request, "important", true, out var isImportant, out var error)) return error;

            var backupFile = request.GetString("file");
            if (!TryResolveConfig(request, out var config, out error)) return error;
            if (!TryResolveFolder(request, config!, out var folder, out error)) return error;
            if (string.IsNullOrWhiteSpace(backupFile)) return "ERROR:" + I18n.GetString("KnotLink_Error_MissingBackupFile");

            if (!Guid.TryParse(folder!.Id, out var sourceGuid) || sourceGuid == Guid.Empty)
                return "ERROR:Managed source has no stable identity.";
            bool success = await NativeHistoryCoreGateway.SetVersionPinByFileAsync(
                config!.Id, new SourceId(sourceGuid), backupFile!, isImportant).ConfigureAwait(false);
            if (!success) return $"ERROR:Backup entry not found: {backupFile}";

            var action = isImportant ? "marked as important" : "unmarked";
            BroadcastEvent(context, "mark_important", new Dictionary<string, string?>
            {
                ["config"] = config.Id,
                ["folder"] = folder.DisplayName,
                ["file"] = backupFile,
                ["important"] = isImportant.ToString()
            });
            BroadcastCommandLifecycle(context, "command_completed");
            return $"OK:Backup '{backupFile}' {action}";
        }
        #endregion

        #endregion
    }
}

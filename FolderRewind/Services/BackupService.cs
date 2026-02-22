using FolderRewind.Models;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static class BackupService
    {
        public static ObservableCollection<BackupTask> ActiveTasks { get; } = new();

        private static DispatcherQueue? UiQueue => App._window?.DispatcherQueue;

        private static Task RunOnUIAsync(Action action)
        {
            var queue = UiQueue;
            if (queue == null || queue.HasThreadAccess)
            {
                action();
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<object?>();

            if (!queue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.SetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }))
            {
                action();
                tcs.TrySetResult(null);
            }

            return tcs.Task;
        }

        /// <summary>
        /// 检查文件是否在黑名单中（参考 MineBackup 的 is_blacklisted 实现）
        /// </summary>
        /// <param name="fileToCheck">要检查的文件路径</param>
        /// <param name="backupSourceRoot">备份源根目录</param>
        /// <param name="originalSourceRoot">原始源目录（热备份时可能不同）</param>
        /// <param name="blacklist">黑名单规则列表</param>
        /// <param name="useRegex">是否启用正则表达式</param>
        /// <returns>如果文件被黑名单匹配则返回 true</returns>
        public static bool IsBlacklisted(
            string fileToCheck,
            string backupSourceRoot,
            string originalSourceRoot,
            IEnumerable<string>? blacklist,
            bool useRegex = false)
        {
            if (string.IsNullOrWhiteSpace(fileToCheck) || blacklist == null) return false;

            var rules = blacklist.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
            if (rules.Count == 0) return false;

            // 转为小写用于不区分大小写的匹配
            var filePathLower = fileToCheck.ToLowerInvariant();

            // 获取相对路径
            string relativePathLower = string.Empty;
            try
            {
                var relativePath = Path.GetRelativePath(backupSourceRoot, fileToCheck);
                if (!relativePath.StartsWith(".."))
                {
                    relativePathLower = relativePath.ToLowerInvariant();
                }
            }
            catch { }

            foreach (var ruleOrig in rules)
            {
                var rule = ruleOrig.Trim();
                var ruleLower = rule.ToLowerInvariant();

                // 检查是否为正则表达式规则
                if (ruleLower.StartsWith("regex:"))
                {
                    if (!useRegex) continue; // 如果未启用正则，跳过正则规则

                    try
                    {
                        var pattern = rule.Substring(6); // 使用原始大小写
                        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

                        // 正则同时匹配绝对路径和相对路径
                        if (regex.IsMatch(fileToCheck) ||
                            (!string.IsNullOrEmpty(relativePathLower) && regex.IsMatch(relativePathLower)))
                        {
                            return true;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // 无效的正则表达式，跳过
                        Log(I18n.Format("BackupService_Log_InvalidRegex", rule), LogLevel.Warning);
                    }
                }
                else
                {
                    // 普通字符串规则

                    // 1. 直接匹配文件名
                    var fileName = Path.GetFileName(fileToCheck);
                    if (!string.IsNullOrEmpty(fileName) &&
                        fileName.Equals(rule, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    // 2. 检查路径是否包含规则字符串
                    if (filePathLower.Contains(ruleLower))
                    {
                        return true;
                    }

                    // 3. 检查相对路径匹配
                    if (!string.IsNullOrEmpty(relativePathLower) && relativePathLower.Contains(ruleLower))
                    {
                        return true;
                    }

                    // 4. 支持通配符匹配 (*, ?)
                    if (rule.Contains('*') || rule.Contains('?'))
                    {
                        try
                        {
                            // 将通配符转换为正则表达式
                            var wildcardPattern = "^" + Regex.Escape(rule)
                                .Replace("\\*", ".*")
                                .Replace("\\?", ".") + "$";
                            var wildcardRegex = new Regex(wildcardPattern, RegexOptions.IgnoreCase);

                            // 匹配文件名
                            if (!string.IsNullOrEmpty(fileName) && wildcardRegex.IsMatch(fileName))
                            {
                                return true;
                            }

                            // 匹配相对路径
                            if (!string.IsNullOrEmpty(relativePathLower) && wildcardRegex.IsMatch(relativePathLower))
                            {
                                return true;
                            }
                        }
                        catch { }
                    }

                    // 5. 处理热备份时的路径映射（参考 MineBackup）
                    if (Path.IsPathRooted(rule))
                    {
                        try
                        {
                            // 检查规则是否在原始源路径下
                            var ruleFullPath = Path.GetFullPath(rule);
                            var originalFullPath = Path.GetFullPath(originalSourceRoot);

                            if (ruleFullPath.StartsWith(originalFullPath, StringComparison.OrdinalIgnoreCase))
                            {
                                // 计算规则相对于原始源的相对路径
                                var ruleRelative = Path.GetRelativePath(originalSourceRoot, ruleFullPath);

                                // 重映射到当前备份源
                                var remappedPath = Path.Combine(backupSourceRoot, ruleRelative);
                                var remappedPathLower = remappedPath.ToLowerInvariant();

                                // 检查文件是否在重映射的黑名单路径下
                                if (filePathLower.StartsWith(remappedPathLower, StringComparison.OrdinalIgnoreCase))
                                {
                                    return true;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 过滤文件列表，移除黑名单中的文件
        /// </summary>
        public static List<string> FilterBlacklist(
            IEnumerable<string> files,
            string backupSourceRoot,
            string originalSourceRoot,
            FilterSettings? filters)
        {
            if (filters?.Blacklist == null || filters.Blacklist.Count == 0)
            {
                return files.ToList();
            }

            return files.Where(f => !IsBlacklisted(
                f, backupSourceRoot, originalSourceRoot,
                filters.Blacklist, filters.UseRegex)).ToList();
        }

        /// <summary>
        /// 备份配置下的所有文件夹
        /// </summary>
        /// <returns>true 表示至少有一个文件夹产生了新的备份文件；false 表示所有文件夹均未检测到变更。</returns>
        public static async Task<bool> BackupConfigAsync(BackupConfig config)
        {
            if (config == null) return false;
            Log(I18n.Format("BackupService_Log_ConfigTaskBegin", config.Name), LogLevel.Info);

            bool anyChanges = false;
            foreach (var folder in config.SourceFolders)
            {
                var hadChanges = await BackupFolderAsync(config, folder);
                if (hadChanges) anyChanges = true;
            }

            Log(I18n.Format("BackupService_Log_TaskEnd"), LogLevel.Info);
            return anyChanges;
        }

        /// <summary>
        /// 备份单个文件夹
        /// </summary>
        /// <returns>true 表示产生了新的备份文件；false 表示未检测到变更或备份失败。</returns>
        public static async Task<bool> BackupFolderAsync(BackupConfig config, ManagedFolder folder, string comment = "")
        {
            if (config == null || folder == null) return false;

            int configIndex = GetConfigIndex(config);

            // 1. 创建任务对象并确保在 UI 线程添加到集合
            var task = new BackupTask
            {
                FolderName = folder.DisplayName,
                Status = I18n.Format("BackupService_Task_Preparing"),
                Progress = 0
            };

            await RunOnUIAsync(() => ActiveTasks.Insert(0, task));

            // 检查是否有插件希望完全接管备份流程
            var (shouldHandle, handlerPlugin) = Services.Plugins.PluginService.CheckPluginWantsToHandleBackup(config);
            if (shouldHandle && handlerPlugin != null)
            {
                await HandlePluginBackupAsync(config, folder, task, handlerPlugin, comment);
                return false;
            }

            // 允许插件在备份前创建快照并替换源路径（例如 Minecraft 热备份：先复制到 snapshot 再备份）。
            string sourcePath = folder.Path;
            try
            {
                var pluginOverride = Services.Plugins.PluginService.InvokeBeforeBackupFolder(config, folder);
                if (!string.IsNullOrWhiteSpace(pluginOverride))
                {
                    sourcePath = pluginOverride;

                    // 原本打算在插件里发送，但是实测太不稳定了，干脆在这里统一发送。
                    KnotLinkService.BroadcastEvent("event=pre_hot_backup;");
                }
            }
            catch
            {
                // 插件异常不会影响核心备份流程（具体异常会在 PluginService 内记录）
            }
            // 按照要求：备份路径 = 用户设置的目标路径 \ 文件夹名
            string backupSubDir = Path.Combine(config.DestinationPath, folder.DisplayName);
            string metadataDir = Path.Combine(config.DestinationPath, "_metadata", folder.DisplayName);

            if (!Directory.Exists(sourcePath))
            {
                Log(I18n.Format("BackupService_Log_SourceFolderMissing", sourcePath), LogLevel.Error);
                await RunOnUIAsync(() =>
                {
                    folder.StatusText = I18n.Format("BackupService_Folder_SourceNotFound");
                    task.Status = I18n.Format("BackupService_Task_Failed");
                    task.IsCompleted = true;
                });

                try
                {
                    KnotLinkService.BroadcastEvent($"event=backup_failed;config={configIndex};world={folder.DisplayName};error=command_failed");
                }
                catch
                {
                }
                return false;
            }

            if (string.IsNullOrEmpty(config.DestinationPath))
            {
                Log(I18n.Format("BackupService_Log_DestinationNotSet"), LogLevel.Error);
                await RunOnUIAsync(() =>
                {
                    folder.StatusText = I18n.Format("BackupService_Folder_TargetNotSet");
                    task.Status = I18n.Format("BackupService_Task_Failed");
                    task.IsCompleted = true;
                });

                try
                {
                    KnotLinkService.BroadcastEvent($"event=backup_failed;config={configIndex};world={folder.DisplayName};error=command_failed");
                }
                catch
                {
                }
                return false;
            }

            // 创建必要的目录
            if (!Directory.Exists(backupSubDir)) Directory.CreateDirectory(backupSubDir);
            if (!Directory.Exists(metadataDir)) Directory.CreateDirectory(metadataDir);

            Log(I18n.Format("BackupService_Log_ProcessingFolder", folder.DisplayName), LogLevel.Info);
            await RunOnUIAsync(() => folder.StatusText = I18n.Format("BackupService_Folder_BackupInProgress"));

            // 与 MineBackup 保持一致：备份开始事件
            try
            {
                KnotLinkService.BroadcastEvent($"event=backup_started;config={configIndex};world={folder.DisplayName}");
            }
            catch
            {
            }

            bool success = false;
            string generatedFileName = null;
            try
            {

                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.Format("BackupService_Task_Processing");
                    folder.StatusText = I18n.Format("BackupService_Folder_BackupRunning");
                });

                // 调用核心逻辑，传入 task 以便更新进度

                // 根据模式分发逻辑
                switch (config.Archive.Mode)
                {
                    case BackupMode.Incremental:
                        {
                            var res = await DoSmartBackupAsync(sourcePath, backupSubDir, metadataDir, folder.DisplayName, config, comment);
                            success = res.Success;
                            generatedFileName = res.FileName;
                            break;
                        }
                    case BackupMode.Overwrite:
                        {
                            var res = await DoOverwriteBackupAsync(sourcePath, backupSubDir, folder.DisplayName, config, comment);
                            success = res.Success;
                            generatedFileName = res.FileName;
                            break;
                        }
                    case BackupMode.Full:
                    default:
                        {
                            var res = await DoFullBackupAsync(sourcePath, backupSubDir, metadataDir, folder.DisplayName, config, comment);
                            success = res.Success;
                            generatedFileName = res.FileName;
                            break;
                        }
                }
            }
            catch (Exception ex)
            {
                Log(I18n.Format("BackupService_Log_Exception", ex.Message), LogLevel.Error);
                success = false;
            }

            if (success)
            {
                bool hasNewFile = !string.IsNullOrWhiteSpace(generatedFileName);

                await RunOnUIAsync(() =>
                {
                    task.Status = hasNewFile
                        ? I18n.Format("BackupService_Task_Completed")
                        : I18n.Format("BackupService_Task_NoChanges");
                    task.Progress = 100;
                    task.IsCompleted = true;

                    folder.StatusText = hasNewFile
                        ? I18n.Format("BackupService_Folder_BackupCompleted")
                        : I18n.Format("BackupService_Task_NoChanges");
                    if (hasNewFile)
                    {
                        folder.LastBackupTime = DateTime.Now.ToString("yyyy/MM/dd HH:mm");
                    }
                });

                if (hasNewFile)
                {
                    ConfigService.Save();

                    string typeStr = config.Archive.Mode.ToString();
                    HistoryService.AddEntry(config, folder, generatedFileName, typeStr, comment);

                    PruneOldArchives(backupSubDir, config.Archive.Format, config.Archive.KeepCount, config.Archive.Mode, config.Archive.SafeDeleteEnabled);

                    // 备份完成后检查文件大小，过小时发出警告
                    try
                    {
                        var archiveFile = Path.Combine(backupSubDir, generatedFileName);
                        if (File.Exists(archiveFile))
                        {
                            var fileSizeKB = new FileInfo(archiveFile).Length / 1024.0;
                            var thresholdKB = ConfigService.CurrentConfig?.GlobalSettings?.FileSizeWarningThresholdKB ?? 5;
                            if (thresholdKB > 0 && fileSizeKB < thresholdKB)
                            {
                                Log(I18n.Format("BackupService_Log_FileSizeTooSmall", folder.DisplayName, fileSizeKB.ToString("F1"), thresholdKB.ToString()), LogLevel.Warning);
                                NotificationService.ShowWarning(
                                    I18n.Format("BackupService_Warning_FileSizeTooSmall", folder.DisplayName, fileSizeKB.ToString("F1"), thresholdKB.ToString()));
                                KnotLinkService.BroadcastEvent($"event=backup_warning;type=file_too_small;config={configIndex};world={folder.DisplayName};size_kb={fileSizeKB:F1}");
                            }
                        }
                    }
                    catch
                    {
                    }

                    // 与 MineBackup 保持一致：备份成功事件
                    try
                    {
                        KnotLinkService.BroadcastEvent($"event=backup_success;config={configIndex};world={folder.DisplayName};file={generatedFileName}");
                    }
                    catch
                    {
                    }
                }

                Log(
                    hasNewFile
                        ? I18n.Format("BackupService_Log_BackupSucceeded", folder.DisplayName)
                        : I18n.Format("BackupService_Log_BackupSkippedNoChanges", folder.DisplayName),
                    LogLevel.Info);
            }
            else
            {
                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.Format("BackupService_Task_Failed");
                    task.IsCompleted = true;
                    folder.StatusText = I18n.Format("BackupService_Folder_BackupFailed");
                });
                Log(I18n.Format("BackupService_Log_BackupFailed", folder.DisplayName), LogLevel.Error);

                try
                {
                    KnotLinkService.BroadcastEvent($"event=backup_failed;config={configIndex};world={folder.DisplayName};error=command_failed");
                }
                catch
                {
                }

                // 发送失败通知
                NotificationService.NotifyBackupCompleted(folder.DisplayName, false, I18n.GetString("BackupService_Task_Failed"));
            }

            // 备份后回调（用于清理快照等）
            try
            {
                Services.Plugins.PluginService.InvokeAfterBackupFolder(config, folder, success, generatedFileName);
            }
            catch
            {
            }

            return success && !string.IsNullOrWhiteSpace(generatedFileName);
        }

        /// <summary>
        /// 由插件完全接管的备份流程
        /// </summary>
        private static async Task HandlePluginBackupAsync(
            BackupConfig config,
            ManagedFolder folder,
            BackupTask task,
            Services.Plugins.IFolderRewindPlugin plugin,
            string comment)
        {
            Log(I18n.Format("BackupService_Log_PluginTakeover", plugin.Manifest.Name, folder.DisplayName), LogLevel.Info);

            await RunOnUIAsync(() =>
            {
                task.Status = I18n.Format("BackupService_Task_PluginProcessing");
                folder.StatusText = I18n.Format("BackupService_Folder_PluginBackingUp");
            });

            try
            {
                var result = await Services.Plugins.PluginService.InvokePluginBackupAsync(
                    plugin, config, folder, comment,
                    async (progress, status) =>
                    {
                        await RunOnUIAsync(() =>
                        {
                            task.Progress = progress;
                            task.Status = status;
                        });
                    });

                if (result.Success)
                {
                    bool hasNewFile = !string.IsNullOrWhiteSpace(result.GeneratedFileName);

                    await RunOnUIAsync(() =>
                    {
                        task.Status = hasNewFile
                            ? I18n.Format("BackupService_Task_Completed")
                            : I18n.Format("BackupService_Task_NoChanges");
                        task.Progress = 100;
                        task.IsCompleted = true;

                        folder.StatusText = hasNewFile
                            ? I18n.Format("BackupService_Folder_BackupCompleted")
                            : I18n.Format("BackupService_Task_NoChanges");
                        if (hasNewFile)
                        {
                            folder.LastBackupTime = DateTime.Now.ToString("yyyy/MM/dd HH:mm");
                        }
                    });

                    if (hasNewFile)
                    {
                        ConfigService.Save();
                        HistoryService.AddEntry(config, folder, result.GeneratedFileName!, "Plugin", comment);
                    }

                    Log(
                        hasNewFile
                            ? I18n.Format("BackupService_Log_PluginBackupSucceeded", folder.DisplayName)
                            : I18n.Format("BackupService_Log_PluginBackupSkippedNoChanges", folder.DisplayName),
                        LogLevel.Info);
                }
                else
                {
                    await RunOnUIAsync(() =>
                    {
                        task.Status = I18n.Format("BackupService_Task_Failed");
                        task.IsCompleted = true;
                        folder.StatusText = I18n.Format("BackupService_Folder_BackupFailed");
                    });

                    Log(I18n.Format("BackupService_Log_PluginBackupFailed", folder.DisplayName, result.Message ?? string.Empty), LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.Format("BackupService_Task_Exception");
                    task.IsCompleted = true;
                    folder.StatusText = I18n.Format("BackupService_Folder_PluginException");
                });

                Log(I18n.Format("BackupService_Log_PluginException", folder.DisplayName, ex.Message), LogLevel.Error);
            }
        }


        private static string GenerateFileName(string baseName, string format, string prefix, string comment)
        {
            string timeStr = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string safeComment = SanitizeFileName(comment);

            // 格式: [Full][2025-01-01_12-00-00]WorldName [Comment].7z
            string commentPart = string.IsNullOrEmpty(safeComment) ? "" : $" [{safeComment}]";
            return $"[{prefix}][{timeStr}]{baseName}{commentPart}.{format}";
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var invalid = Path.GetInvalidFileNameChars();
            // 额外过滤掉中括号，以免破坏解析逻辑
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                if (!invalid.Contains(c) && c != '[' && c != ']')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static void PruneOldArchives(string destDir, string format, int keepCount, BackupMode mode, bool safeDeleteEnabled = true)
        {
            if (keepCount <= 0) return;
            if (mode == BackupMode.Incremental && !safeDeleteEnabled) return; // 不启用安全删除时，增量模式跳过自动清理以保护链
            try
            {
                var di = new DirectoryInfo(destDir);
                if (!di.Exists) return;

                var files = di.GetFiles($"*.{format}")
                              .OrderByDescending(f => f.LastWriteTimeUtc)
                              .ToList();

                if (files.Count <= keepCount) return;

                // 检查历史记录中标记为重要的文件，避免删除
                var importantFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var folderName = di.Name;
                    var allConfigs = ConfigService.CurrentConfig?.BackupConfigs;
                    if (allConfigs != null)
                    {
                        foreach (var config in allConfigs)
                        {
                            var historyEntries = HistoryService.GetEntriesForFolder(config.Id, folderName);
                            if (historyEntries != null)
                            {
                                foreach (var entry in historyEntries)
                                {
                                    if (entry.IsImportant)
                                    {
                                        importantFiles.Add(entry.FileName);
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // 历史记录读取失败不影响清理流程
                }

                // 收集可删除的文件（排除标记为重要的）
                var deletableFiles = files.Where(f => !importantFiles.Contains(f.Name)).ToList();

                // 计算需要删除的数量
                int totalToKeep = keepCount;
                int importantCount = files.Count - deletableFiles.Count;
                // 如果重要文件已占满配额，则不删除
                if (importantCount >= totalToKeep)
                {
                    Log(I18n.Format("BackupService_Log_PruneSkipAllImportant"), LogLevel.Info);
                    return;
                }

                int toDeleteCount = files.Count - totalToKeep;
                // 从最旧的可删除文件开始删除
                var filesToDelete = deletableFiles
                    .OrderBy(f => f.LastWriteTimeUtc) // 最旧的排前面
                    .Take(toDeleteCount)
                    .ToList();

                foreach (var file in filesToDelete)
                {
                    try
                    {
                        if (safeDeleteEnabled && file.Name.Contains("[Smart]", StringComparison.OrdinalIgnoreCase))
                        {
                            // 安全删除：增量备份需要合并到下一个备份再删除
                            SafeDeleteArchive(file, di, format);
                        }
                        else
                        {
                            file.Delete();
                            Log(I18n.Format("BackupService_Log_PrunedOldBackup", file.Name), LogLevel.Info);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(I18n.Format("BackupService_Log_PruneDeleteFailed", file.Name, ex.Message), LogLevel.Warning);
                    }
                }
            }
            catch
            {

            }
        }

        /// <summary>
        /// 安全删除备份文件（参考 MineBackup DoSafeDeleteBackup 逻辑）。
        /// 当删除的是增量链中的一个节点时，先将其内容合并到下一个备份中，
        /// 如果被删除的是 Full 备份，则将下一个 Smart 备份升级为 Full。
        /// 这样可以保证增量链不断裂，任何备份仍然可以正确还原。
        /// </summary>
        private static void SafeDeleteArchive(FileInfo fileToDelete, DirectoryInfo backupDir, string format)
        {
            Log(I18n.Format("BackupService_Log_SafeDeleteStart", fileToDelete.Name), LogLevel.Info);

            // 找到时间上紧邻的下一个备份文件
            var allFiles = backupDir.GetFiles($"*.{format}")
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            FileInfo nextFile = null;
            for (int i = 0; i < allFiles.Count; i++)
            {
                if (string.Equals(allFiles[i].FullName, fileToDelete.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < allFiles.Count)
                    {
                        nextFile = allFiles[i + 1];
                    }
                    break;
                }
            }

            // 如果没有下一个文件（链尾），或下一个是 Full 备份，直接删除即可
            if (nextFile == null || nextFile.Name.Contains("[Full]", StringComparison.OrdinalIgnoreCase))
            {
                Log(I18n.Format("BackupService_Log_SafeDeleteEndOfChain"), LogLevel.Info);
                fileToDelete.Delete();
                Log(I18n.Format("BackupService_Log_PrunedOldBackup", fileToDelete.Name), LogLevel.Info);
                return;
            }

            // 需要将 fileToDelete 的内容合并到 nextFile 中
            string sevenZipExe = ResolveSevenZipExecutable();
            if (string.IsNullOrEmpty(sevenZipExe))
            {
                Log(I18n.Format("BackupService_Log_SafeDeleteNo7z"), LogLevel.Warning);
                // 无法安全删除，回退为直接删除
                fileToDelete.Delete();
                return;
            }

            // 创建临时目录用于解压
            string tempDir = Path.Combine(Path.GetTempPath(), "FolderRewind_SafeDelete_" + Guid.NewGuid().ToString("N")[..8]);

            try
            {
                Directory.CreateDirectory(tempDir);

                // 步骤1: 解压被删除文件的内容到临时目录
                Log(I18n.Format("BackupService_Log_SafeDeleteStep1"), LogLevel.Info);
                string extractArgs = $"x \"{fileToDelete.FullName}\" -o\"{tempDir}\" -y";
                var extractResult = RunSevenZipProcessSync(sevenZipExe, extractArgs);
                if (!extractResult)
                {
                    Log(I18n.Format("BackupService_Log_SafeDeleteExtractFailed"), LogLevel.Error);
                    return; // 解压失败则不删除，保护数据安全
                }

                // 步骤2: 将解压的内容合并到下一个备份文件中
                // 记录原始修改时间（保持时间排序不变）
                Log(I18n.Format("BackupService_Log_SafeDeleteStep2"), LogLevel.Info);
                var originalModTime = nextFile.LastWriteTimeUtc;
                string mergeArgs = $"a \"{nextFile.FullName}\" .\\*";
                var mergeResult = RunSevenZipProcessSync(sevenZipExe, mergeArgs, tempDir);
                if (!mergeResult)
                {
                    // 合并失败时恢复时间戳
                    try { File.SetLastWriteTimeUtc(nextFile.FullName, originalModTime); } catch { }
                    Log(I18n.Format("BackupService_Log_SafeDeleteMergeFailed"), LogLevel.Error);
                    return; // 合并失败则不删除
                }
                // 恢复原始修改时间以维持排序
                try { File.SetLastWriteTimeUtc(nextFile.FullName, originalModTime); } catch { }

                // 步骤3: 如果被删除的是 Full 备份，将下一个 Smart 备份重命名为 Full
                if (fileToDelete.Name.Contains("[Full]", StringComparison.OrdinalIgnoreCase)
                    && nextFile.Name.Contains("[Smart]", StringComparison.OrdinalIgnoreCase))
                {
                    Log(I18n.Format("BackupService_Log_SafeDeletePromote"), LogLevel.Info);
                    string newName = nextFile.Name.Replace("[Smart]", "[Full]");
                    string newPath = Path.Combine(backupDir.FullName, newName);
                    try
                    {
                        File.Move(nextFile.FullName, newPath);
                        // 更新历史记录中的文件名和类型
                        HistoryService.RenameEntry(nextFile.Name, newName, "Full");
                        Log(I18n.Format("BackupService_Log_SafeDeleteRenamed", newName), LogLevel.Info);
                    }
                    catch (Exception ex)
                    {
                        Log(I18n.Format("BackupService_Log_SafeDeleteRenameFailed", ex.Message), LogLevel.Warning);
                    }
                }

                // 步骤4: 删除原始文件
                Log(I18n.Format("BackupService_Log_SafeDeleteStep4"), LogLevel.Info);
                fileToDelete.Delete();
                Log(I18n.Format("BackupService_Log_SafeDeleteSuccess", fileToDelete.Name), LogLevel.Info);
            }
            catch (Exception ex)
            {
                Log(I18n.Format("BackupService_Log_SafeDeleteFatalError", ex.Message), LogLevel.Error);
            }
            finally
            {
                // 清理临时目录
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch { }
            }
        }

        /// <summary>
        /// 同步方式运行 7z 进程（用于安全删除等非异步场景）
        /// </summary>
        private static bool RunSevenZipProcessSync(string sevenZipExe, string arguments, string workingDirectory = null)
        {
            try
            {
                var pInfo = new ProcessStartInfo
                {
                    FileName = sevenZipExe,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                if (!string.IsNullOrWhiteSpace(workingDirectory))
                    pInfo.WorkingDirectory = workingDirectory;

                using var p = Process.Start(pInfo);
                p?.WaitForExit(120_000); // 最长等待 2 分钟
                return p?.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Log(I18n.Format("BackupService_Log_SystemError", ex.Message), LogLevel.Error);
                return false;
            }
        }

        // --- 模式 1: 全量备份 ---
        // 返回 (Success, FileName)
        private static async Task<(bool Success, string FileName)> DoFullBackupAsync(string source, string destDir, string metaDir, string baseName, BackupConfig config, string comment = "")
        {
            if (config.Archive.SkipIfUnchanged && !string.IsNullOrEmpty(metaDir))
            {
                string metadataPath = Path.Combine(metaDir, "metadata.json");
                if (File.Exists(metadataPath))
                {
                    try
                    {
                        var oldMeta = JsonSerializer.Deserialize(File.ReadAllText(metadataPath), AppJsonContext.Default.BackupMetadata);
                        if (oldMeta != null)
                        {
                            // 校验元数据引用的备份文件是否仍然存在
                            // 如果用户删除了最近的备份文件，应强制执行完整备份
                            bool referencedBackupExists = true;
                            if (!string.IsNullOrEmpty(oldMeta.LastBackupFileName))
                            {
                                string referencedBackupPath = Path.Combine(destDir, oldMeta.LastBackupFileName);
                                if (!File.Exists(referencedBackupPath))
                                {
                                    referencedBackupExists = false;
                                    Log(I18n.Format("BackupService_Log_ReferencedBackupMissing", oldMeta.LastBackupFileName), LogLevel.Warning);
                                }
                            }

                            if (referencedBackupExists)
                            {
                                var currentStates = ScanDirectory(source, config.Filters);
                                bool hasChanges = false;

                                // 比较文件数量是否一致
                                if (currentStates.Count != oldMeta.FileStates.Count)
                                {
                                    hasChanges = true;
                                }
                                else
                                {
                                    // 逐文件比较大小和修改时间
                                    foreach (var kvp in currentStates)
                                    {
                                        if (!oldMeta.FileStates.TryGetValue(kvp.Key, out var oldState) ||
                                            kvp.Value.Size != oldState.Size ||
                                            kvp.Value.LastWriteTimeUtc != oldState.LastWriteTimeUtc)
                                        {
                                            hasChanges = true;
                                            break;
                                        }
                                    }
                                }

                                if (!hasChanges)
                                {
                                    Log(I18n.Format("BackupService_Log_NoChangesDetected"), LogLevel.Info);
                                    return (true, null); // 无变更，跳过备份
                                }
                            }
                            // 如果 referencedBackupExists == false，跳过比较，直接进入全量备份
                        }
                    }
                    catch
                    {
                        // 元数据读取失败时继续执行全量备份
                        Log(I18n.Format("BackupService_Log_MetadataCorruptedFallbackFull"), LogLevel.Warning);
                    }
                }
            }

            string fileName = GenerateFileName(baseName, config.Archive.Format, "Full", comment);
            string destFile = Path.Combine(destDir, fileName);

            // 1. 直接压缩（带黑名单过滤 + 自定义文件类型排除）
            var fileTypeExclusions = config.Archive.FileTypeHandlingEnabled ? (IReadOnlyList<FileTypeRule>)config.Archive.FileTypeRules : null;
            bool result = await Run7zCommandAsync("a", source, destFile, config.Archive, null, config.Filters, fileTypeExclusions);

            // 2. 自定义文件类型追加压缩（不同压缩等级）
            if (result && config.Archive.FileTypeHandlingEnabled)
            {
                bool ruleResult = await RunFileTypeRulePassesAsync(source, destFile, config.Archive, null, config.Filters);
                if (!ruleResult)
                {
                    Log(I18n.Format("BackupService_Log_FileTypeRulePassFailed"), LogLevel.Warning);
                    // 规则追加失败不影响主备份结果，仅记录警告
                }
            }

            // 3. 如果成功，生成新的元数据（为后续可能的增量备份做基准）
            if (result)
            {
                await UpdateMetadataAsync(source, metaDir, fileName, fileName, null, config.Filters); // 基准是自己
                return (true, fileName);
            }
            return (false, null);
        }

        // --- 模式 2: 智能增量备份 ---
        // 返回 (Success, FileName)
        private static async Task<(bool Success, string FileName)> DoSmartBackupAsync(string source, string destDir, string metaDir, string baseName, BackupConfig config, string comment = "")
        {
            string metadataPath = Path.Combine(metaDir, "metadata.json");
            BackupMetadata oldMeta = null;

            // 1. 读取旧元数据
            if (File.Exists(metadataPath))
            {
                try { oldMeta = JsonSerializer.Deserialize(File.ReadAllText(metadataPath), AppJsonContext.Default.BackupMetadata); }
                catch { Log(I18n.Format("BackupService_Log_MetadataCorruptedFallbackFull"), LogLevel.Warning); }
            }

            // 如果没有元数据，强制全量
            if (oldMeta == null)
            {
                Log(I18n.Format("BackupService_Log_NoBaselineMetadataFallbackFull"), LogLevel.Info);
                return await DoFullBackupAsync(source, destDir, metaDir, baseName, config, comment);
            }

            // 校验元数据引用的备份文件是否仍然存在
            // 如果用户删除了最近的备份文件，增量链已断裂，应强制全量备份
            if (!string.IsNullOrEmpty(oldMeta.LastBackupFileName))
            {
                string referencedBackupPath = Path.Combine(destDir, oldMeta.LastBackupFileName);
                if (!File.Exists(referencedBackupPath))
                {
                    Log(I18n.Format("BackupService_Log_ReferencedBackupMissing", oldMeta.LastBackupFileName), LogLevel.Warning);
                    return await DoFullBackupAsync(source, destDir, metaDir, baseName, config, comment);
                }
            }
            if (!string.IsNullOrEmpty(oldMeta.BasedOnFullBackup) && oldMeta.BasedOnFullBackup != oldMeta.LastBackupFileName)
            {
                string baseBackupPath = Path.Combine(destDir, oldMeta.BasedOnFullBackup);
                if (!File.Exists(baseBackupPath))
                {
                    Log(I18n.Format("BackupService_Log_ReferencedBackupMissing", oldMeta.BasedOnFullBackup), LogLevel.Warning);
                    return await DoFullBackupAsync(source, destDir, metaDir, baseName, config, comment);
                }
            }

            // 2. 智能备份链长度检查（参考 MineBackup maxSmartBackupsPerFull 逻辑）
            // 当连续的增量备份数量达到上限时，强制执行全量备份以截断链条
            int maxChain = config.Archive.MaxSmartBackupsPerFull;
            if (maxChain > 0)
            {
                bool forceFullDueToChainLimit = false;
                try
                {
                    var dirInfo = new DirectoryInfo(destDir);
                    if (dirInfo.Exists)
                    {
                        // 获取所有备份文件，按时间降序排列
                        var allBackups = dirInfo.GetFiles($"*.{config.Archive.Format}")
                            .OrderByDescending(f => f.LastWriteTimeUtc)
                            .ToList();

                        // 从最新备份往回计数，统计最近一次 Full 备份之后的 Smart 备份数量
                        int smartCount = 0;
                        bool fullFound = false;
                        foreach (var bkFile in allBackups)
                        {
                            if (bkFile.Name.Contains("[Full]", StringComparison.OrdinalIgnoreCase))
                            {
                                fullFound = true;
                                break;
                            }
                            if (bkFile.Name.Contains("[Smart]", StringComparison.OrdinalIgnoreCase))
                            {
                                smartCount++;
                            }
                        }

                        // 只有找到了 Full 基准且 Smart 数量已达上限时才强制全量
                        if (fullFound && smartCount >= maxChain)
                        {
                            forceFullDueToChainLimit = true;
                            Log(I18n.Format("BackupService_Log_SmartChainLimitReached", maxChain), LogLevel.Info);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log(I18n.Format("BackupService_Log_SmartChainCheckFailed", ex.Message), LogLevel.Warning);
                }

                if (forceFullDueToChainLimit)
                {
                    return await DoFullBackupAsync(source, destDir, metaDir, baseName, config, comment);
                }
            }

            // 2. 扫描并对比文件（带黑名单过滤）
            Log(I18n.Format("BackupService_Log_AnalyzingDiff"), LogLevel.Info);
            var currentStates = ScanDirectory(source, config.Filters);
            var changedFiles = new List<string>();

            foreach (var kvp in currentStates)
            {
                string relPath = kvp.Key;
                FileState curState = kvp.Value;

                if (oldMeta.FileStates.TryGetValue(relPath, out var oldState))
                {
                    // 对比 Size 和 Time (快速) 或 Hash (精确)
                    // 为了性能，先比 Size 和 Time，如果一致则认为没变
                    // 如果你需要绝对精确，可以强制算 Hash
                    if (curState.Size != oldState.Size || curState.LastWriteTimeUtc != oldState.LastWriteTimeUtc)
                    {
                        changedFiles.Add(relPath);
                    }
                    else
                    {
                        // 如果想模仿 MineBackup 严格模式，这里可以加 Hash 对比
                        // changedFiles.Add(relPath); 
                    }
                }
                else
                {
                    // 新文件
                    changedFiles.Add(relPath);
                }
            }

            if (changedFiles.Count == 0)
            {
                Log(I18n.Format("BackupService_Log_NoChangesDetected"), LogLevel.Info);
                return (true, null);
            }

            Log(I18n.Format("BackupService_Log_ChangesDetected", changedFiles.Count), LogLevel.Info);

            // 3. 生成文件列表文件
            // 当启用自定义文件类型处理时，需要将变更文件列表拆分：
            // - 主列表：不匹配任何 FileTypeRule 的文件（使用主压缩等级）
            // - 规则匹配文件：稍后用独立压缩等级追加
            bool hasFileTypeRules = config.Archive.FileTypeHandlingEnabled
                && config.Archive.FileTypeRules != null
                && config.Archive.FileTypeRules.Count > 0;

            List<string> mainFiles = changedFiles;
            if (hasFileTypeRules)
            {
                mainFiles = changedFiles.Where(f =>
                    !config.Archive.FileTypeRules.Any(rule =>
                        !string.IsNullOrWhiteSpace(rule.Pattern) && MatchWildcard(f, rule.Pattern.Trim())))
                    .ToList();
            }

            string listFile = Path.GetTempFileName();
            File.WriteAllLines(listFile, mainFiles);

            string fileName = GenerateFileName(baseName, config.Archive.Format, "Smart", comment);
            string destFile = Path.Combine(destDir, fileName);

            // 4. 执行压缩 (使用 @listfile)
            // 注意：7z 需要工作目录在 source 下，才能正确识别相对路径列表
            var fileTypeExclusions = hasFileTypeRules ? (IReadOnlyList<FileTypeRule>)config.Archive.FileTypeRules : null;
            bool result = await Run7zCommandAsync("a", source, destFile, config.Archive, listFile, config.Filters, fileTypeExclusions);

            // 4.5 自定义文件类型追加压缩（增量模式下传递变更文件列表用于筛选）
            if (result && hasFileTypeRules)
            {
                bool ruleResult = await RunFileTypeRulePassesAsync(source, destFile, config.Archive, changedFiles, config.Filters);
                if (!ruleResult)
                {
                    Log(I18n.Format("BackupService_Log_FileTypeRulePassFailed"), LogLevel.Warning);
                }
            }

            // 5. 更新元数据
            if (result)
            {
                File.Delete(listFile);
                // 更新元数据：基准文件保持不变（指向最初的Full），LastBackup指向自己
                await UpdateMetadataAsync(source, metaDir, fileName, oldMeta.BasedOnFullBackup, currentStates, config.Filters);
                return (true, fileName);
            }
            else
            {
                try { File.Delete(listFile); } catch { }
                return (false, null);
            }
        }

        // --- 模式 3: 覆写备份 ---
        // 返回 (Success, FileName)
        private static async Task<(bool Success, string FileName)> DoOverwriteBackupAsync(string source, string destDir, string baseName, BackupConfig config, string comment = "")
        {
            // 1. 寻找最近的备份文件
            var dirInfo = new DirectoryInfo(destDir);
            var files = dirInfo.GetFiles($"*.{config.Archive.Format}")
                               .OrderByDescending(f => f.LastWriteTime)
                               .ToList();

            if (files.Count == 0)
            {
                Log(I18n.Format("BackupService_Log_NoExistingBackupFallbackFull"), LogLevel.Info);
                return await DoFullBackupAsync(source, destDir, "", baseName, config, comment); // 覆写模式不需要元数据
            }

            FileInfo targetFile = files[0];
            Log(I18n.Format("BackupService_Log_OverwriteUpdating", targetFile.Name), LogLevel.Info);

            // 2. 执行 update 命令 (u)（带黑名单过滤 + 自定义文件类型排除）
            // 7z u <archive_name> <file_names>
            // u 指令会更新已存在的文件并添加新文件
            var fileTypeExclusions = config.Archive.FileTypeHandlingEnabled ? (IReadOnlyList<FileTypeRule>)config.Archive.FileTypeRules : null;
            bool result = await Run7zCommandAsync("u", source, targetFile.FullName, config.Archive, null, config.Filters, fileTypeExclusions);

            // 2.5 自定义文件类型追加压缩
            if (result && config.Archive.FileTypeHandlingEnabled)
            {
                bool ruleResult = await RunFileTypeRulePassesAsync(source, targetFile.FullName, config.Archive, null, config.Filters);
                if (!ruleResult)
                {
                    Log(I18n.Format("BackupService_Log_FileTypeRulePassFailed"), LogLevel.Warning);
                }
            }

            string resultingFileName = null;

            if (result)
            {
                // 3. 重命名文件以更新时间戳 (参考 MineBackup 逻辑)
                // 假设文件名格式包含 [YYYY-MM-DD...]，我们要替换它
                string oldName = targetFile.Name;
                string newTimeStr = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

                // 简单的正则或字符串替换：查找第二个方括号内时间并替换
                string newName = oldName;
                int firstBracket = oldName.IndexOf('[');
                int secondBracket = oldName.IndexOf(']', firstBracket + 1);
                int thirdBracket = oldName.IndexOf('[', secondBracket + 1);
                int fourthBracket = thirdBracket >= 0 ? oldName.IndexOf(']', thirdBracket + 1) : -1;

                // 尝试找到时间段（第二个方括号里的内容）
                int timeStart = -1;
                int timeEnd = -1;
                // 通mon pattern: [Type][Time]Name.ext  -> time is between second '[' and following ']'
                // 找第二个 '['
                int nth = 0;
                for (int i = 0; i < oldName.Length; i++)
                {
                    if (oldName[i] == '[') nth++;
                    if (nth == 2) { timeStart = i + 1; break; }
                }
                if (timeStart != -1)
                {
                    for (int i = timeStart; i < oldName.Length; i++)
                    {
                        if (oldName[i] == ']') { timeEnd = i; break; }
                    }
                }

                if (timeStart != -1 && timeEnd != -1)
                {
                    newName = oldName.Substring(0, timeStart) + newTimeStr + oldName.Substring(timeEnd);
                }
                else
                {
                    // 如果格式不对，就重新构造名字，保留类型前缀与后缀
                    string extension = Path.GetExtension(oldName);
                    // 去掉已存在的方括号信息尽量简化构造
                    string simpleBase = baseName;
                    newName = GenerateFileName(simpleBase, config.Archive.Format, "Overwrite", comment);
                }

                resultingFileName = newName;

                if (newName != oldName)
                {
                    string newPath = Path.Combine(destDir, newName);
                    try
                    {
                        File.Move(targetFile.FullName, newPath);
                        Log(I18n.Format("BackupService_Log_RenamedTo", newName), LogLevel.Info);
                    }
                    catch { /* 忽略重命名错误 */ resultingFileName = targetFile.Name; }
                }
                else
                {
                    resultingFileName = oldName;
                }
            }

            return (result, resultingFileName ?? targetFile.Name);
        }


        public enum RestoreMode
        {
            Clean = 0,      // 清空目标后还原 (最安全)
            Overwrite = 1   // 直接覆盖 (保留未被覆盖的文件)
        }

        public static async Task RestoreBackupAsync(BackupConfig config, ManagedFolder folder, HistoryItem historyItem, RestoreMode mode)
        {
            int configIndex = GetConfigIndex(config);
            string backupFilePath = Path.Combine(config.DestinationPath, folder.DisplayName, historyItem.FileName);
            string targetDir = folder.Path; // 还原回源目录

            if (!File.Exists(backupFilePath))
            {
                Log(I18n.Format("BackupService_Log_BackupFileNotFound", backupFilePath), LogLevel.Error);

                try
                {
                    KnotLinkService.BroadcastEvent("event=restore_finished;status=failure;reason=no_backup_found");
                }
                catch
                {
                }
                return;
            }

            if (config.Archive.BackupBeforeRestore)
            {
                Log(I18n.Format("BackupService_Log_BackupBeforeRestore", folder.DisplayName), LogLevel.Info);
                try
                {
                    await BackupFolderAsync(config, folder, "BeforeRestore");
                    Log(I18n.Format("BackupService_Log_BackupBeforeRestoreCompleted"), LogLevel.Info);
                }
                catch (Exception ex)
                {
                    Log(I18n.Format("BackupService_Log_BackupBeforeRestoreFailed", ex.Message), LogLevel.Warning);
                    // 即使备份失败也继续还原，仅记录警告
                }
            }

            string sevenZipExe = ResolveSevenZipExecutable();
            if (string.IsNullOrEmpty(sevenZipExe)) return;

            var backupDir = new DirectoryInfo(Path.GetDirectoryName(backupFilePath)!);
            var targetFile = new FileInfo(backupFilePath);
            var chain = BuildRestoreChain(backupDir, targetFile, historyItem.BackupType);
            if (chain.Count == 0)
            {
                Log(I18n.Format("BackupService_Log_RestoreChainNotFound"), LogLevel.Error);
                return;
            }

            Log(I18n.Format("BackupService_Log_RestoreBegin", folder.DisplayName), LogLevel.Info);
            Log(I18n.Format("BackupService_Log_RestoreTargetBackup", historyItem.FileName), LogLevel.Info);
            Log(I18n.Format("BackupService_Log_RestoreTargetPath", targetDir), LogLevel.Info);

            try
            {
                KnotLinkService.BroadcastEvent($"event=restore_started;config={configIndex};world={folder.DisplayName}");
            }
            catch
            {
            }

            // 先确保目标目录存在，再执行清空操作
            if (!Directory.Exists(targetDir))
            {
                try
                {
                    Directory.CreateDirectory(targetDir);
                }
                catch (Exception ex)
                {
                    Log(I18n.Format("BackupService_Log_RestoreCreateTargetDirFailed", ex.Message), LogLevel.Error);
                    try
                    {
                        KnotLinkService.BroadcastEvent("event=restore_finished;status=failure;reason=create_dir_failed");
                    }
                    catch { }
                    return;
                }
            }

            // 1. Clean 模式先清空目标
            if (mode == RestoreMode.Clean)
            {
                Log(I18n.Format("BackupService_Log_RestoreCleaningTarget"), LogLevel.Info);

                // 收集还原白名单
                var restoreWhitelist = config.Filters?.RestoreWhitelist;
                bool hasWhitelist = restoreWhitelist != null && restoreWhitelist.Count > 0;

                try
                {
                    DirectoryInfo di = new DirectoryInfo(targetDir);

                    foreach (var entry in di.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
                    {
                        // 检查是否在还原白名单中
                        if (hasWhitelist && IsInRestoreWhitelist(entry.FullName, targetDir, restoreWhitelist))
                        {
                            Log(I18n.Format("BackupService_Log_RestoreWhitelistSkip", entry.Name), LogLevel.Info);
                            continue;
                        }

                        try
                        {
                            if (entry is DirectoryInfo dirEntry)
                            {
                                // 递归删除目录
                                dirEntry.Delete(true);
                            }
                            else if (entry is FileInfo fileEntry)
                            {
                                if ((fileEntry.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                                {
                                    fileEntry.Attributes &= ~FileAttributes.ReadOnly;
                                }
                                fileEntry.Delete();
                            }
                        }
                        catch (Exception entryEx)
                        {
                            Log(I18n.Format("BackupService_Log_RestoreDeleteFileFailed", entry.Name, entryEx.Message), LogLevel.Warning);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log(I18n.Format("BackupService_Log_RestoreCleanFailedContinueOverwrite", ex.Message), LogLevel.Warning);
                }
            }

            // 2. 按链顺序依次解压（Full + Smart）
            foreach (var file in chain)
            {
                Log(I18n.Format("BackupService_Log_RestoreApplyingArchive", file.Name), LogLevel.Info);
                bool ok = await RunSevenZipProcessAsync(sevenZipExe, $"x \"{file.FullName}\" -o\"{targetDir}\" -y", file.DirectoryName);
                if (!ok)
                {
                    Log(I18n.Format("BackupService_Log_RestoreExtractFailed"), LogLevel.Error);

                    try
                    {
                        KnotLinkService.BroadcastEvent("event=restore_finished;status=failure;reason=command_failed");
                    }
                    catch
                    {
                    }

                    // 发送恢复失败通知
                    NotificationService.NotifyRestoreCompleted(folder.DisplayName, false, I18n.GetString("BackupService_Log_RestoreExtractFailed"));
                    return;
                }
            }

            Log(I18n.Format("BackupService_Log_RestoreCompleted"), LogLevel.Info);

            try
            {
                KnotLinkService.BroadcastEvent($"event=restore_success;config={configIndex};world={folder.DisplayName};backup={historyItem.FileName}");
            }
            catch
            {
            }
        }

        /// <summary>
        /// 检查文件/文件夹是否在还原白名单中（参考 MineBackup is_blacklisted 思路，复用名称匹配）
        /// 支持精确文件名匹配和路径包含匹配
        /// </summary>
        private static bool IsInRestoreWhitelist(string entryPath, string rootDir, IEnumerable<string> whitelist)
        {
            if (whitelist == null) return false;

            var entryName = Path.GetFileName(entryPath);
            var entryPathLower = entryPath.ToLowerInvariant();

            string relativePathLower = string.Empty;
            try
            {
                var relativePath = Path.GetRelativePath(rootDir, entryPath);
                if (!relativePath.StartsWith(".."))
                {
                    relativePathLower = relativePath.ToLowerInvariant();
                }
            }
            catch { }

            foreach (var ruleOrig in whitelist)
            {
                if (string.IsNullOrWhiteSpace(ruleOrig)) continue;
                var rule = ruleOrig.Trim();

                // 精确文件名匹配
                if (!string.IsNullOrEmpty(entryName) &&
                    entryName.Equals(rule, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // 路径包含匹配
                var ruleLower = rule.ToLowerInvariant();
                if (entryPathLower.Contains(ruleLower))
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(relativePathLower) && relativePathLower.Contains(ruleLower))
                {
                    return true;
                }

                // 通配符匹配
                if (rule.Contains('*') || rule.Contains('?'))
                {
                    try
                    {
                        var wildcardPattern = "^" + Regex.Escape(rule)
                            .Replace("\\*", ".*")
                            .Replace("\\?", ".") + "$";
                        var wildcardRegex = new Regex(wildcardPattern, RegexOptions.IgnoreCase);

                        if (!string.IsNullOrEmpty(entryName) && wildcardRegex.IsMatch(entryName))
                        {
                            return true;
                        }
                    }
                    catch { }
                }
            }

            return false;
        }

        private static int GetConfigIndex(BackupConfig config)
        {
            try
            {
                var configs = ConfigService.CurrentConfig?.BackupConfigs;
                if (configs == null) return -1;

                for (int i = 0; i < configs.Count; i++)
                {
                    if (configs[i]?.Id == config.Id)
                    {
                        return i;
                    }
                }
            }
            catch
            {
            }

            return -1;
        }

        /// <summary>
        /// 通过备份文件名还原（供 KnotLink 远程调用使用）
        /// </summary>
        public static async Task RestoreBackupAsync(BackupConfig config, ManagedFolder folder, string backupFileName)
        {
            // 构造一个临时的 HistoryItem
            string backupType = "Full";
            if (backupFileName.Contains("[Smart]", StringComparison.OrdinalIgnoreCase))
            {
                backupType = "Incremental";
            }
            else if (backupFileName.Contains("[Overwrite]", StringComparison.OrdinalIgnoreCase))
            {
                backupType = "Overwrite";
            }

            var historyItem = new HistoryItem
            {
                FileName = backupFileName,
                BackupType = backupType
            };

            await RestoreBackupAsync(config, folder, historyItem, RestoreMode.Overwrite);
        }


        // --- 辅助：元数据处理 ---
        private static Dictionary<string, FileState> ScanDirectory(string path, FilterSettings? filters = null, string? originalSourcePath = null)
        {
            // 预估容量以减少字典扩容开销
            var result = new Dictionary<string, FileState>(1024, StringComparer.OrdinalIgnoreCase);
            var dirInfo = new DirectoryInfo(path);

            // 使用 EnumerationOptions 跳过无法访问的文件，避免异常导致的性能损失
            var enumOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System // 跳过系统文件
            };

            var originalRoot = originalSourcePath ?? path;

            // 获取所有文件，使用相对路径作为 Key，采用流式枚举避免一次性加载大目录列表。
            foreach (var file in dirInfo.EnumerateFiles("*", enumOptions))
            {
                // 检查黑名单
                if (filters?.Blacklist != null && filters.Blacklist.Count > 0)
                {
                    if (IsBlacklisted(file.FullName, path, originalRoot, filters.Blacklist, filters.UseRegex))
                    {
                        continue;
                    }
                }

                string relPath = Path.GetRelativePath(path, file.FullName);
                result[relPath] = new FileState
                {
                    Size = file.Length,
                    LastWriteTimeUtc = file.LastWriteTimeUtc,
                    // 只有在真正需要的时候才算 Hash，因为很慢。
                    // 这里暂且留空或仅在严格模式计算。MineBackup 默认也是优先比对 Time/Size
                    Hash = ""
                };
            }
            return result;
        }

        private static async Task UpdateMetadataAsync(string sourceDir, string metaDir, string currentBackupFile, string baseBackupFile, Dictionary<string, FileState>? states = null, FilterSettings? filters = null)
        {
            if (states == null) states = ScanDirectory(sourceDir, filters);

            var meta = new BackupMetadata
            {
                LastBackupTime = DateTime.Now,
                LastBackupFileName = currentBackupFile,
                BasedOnFullBackup = baseBackupFile,
                FileStates = states
            };

            string json = JsonSerializer.Serialize(meta, AppJsonContext.Default.BackupMetadata);
            await File.WriteAllTextAsync(Path.Combine(metaDir, "metadata.json"), json);
        }

        // --- 核心：7z 进程调用 ---
        private static async Task<bool> Run7zCommandAsync(string commandMode, string sourceDir, string archivePath, ArchiveSettings settings, string? listFile = null, FilterSettings? filters = null, IReadOnlyList<FileTypeRule>? fileTypeExclusions = null)
        {
            string sevenZipExe = ResolveSevenZipExecutable();
            if (string.IsNullOrEmpty(sevenZipExe)) return false;

            // 构建参数
            // -mx: 压缩等级
            // -ssw: 即使打开也压缩
            // -m0: 算法
            var sb = new StringBuilder();
            sb.Append($"{commandMode} -t{settings.Format} \"{archivePath}\"");

            if (listFile != null)
            {
                // 使用文件列表
                sb.Append($" @\"{listFile}\"");
            }
            else
            {
                // 直接指定源目录 (加通配符以包含内容而非目录本身，视需求而定)
                sb.Append($" \"{sourceDir}\\*\"");
            }

            sb.Append($" -mx={settings.CompressionLevel} -m0={settings.Method} -ssw");

            if (settings.CpuThreads > 0)
            {
                sb.Append($" -mmt{settings.CpuThreads}");
            }
            else
            {
                sb.Append(" -mmt"); // 默认自动线程数
            }

            if (!string.IsNullOrWhiteSpace(settings.Password))
            {
                sb.Append($" -p\"{settings.Password}\" -mhe=on");
            }
            sb.Append(" -bsp1"); // 开启进度输出到 stderr/stdout

            // 自定义文件类型处理：关闭固实压缩，排除待特殊处理的文件模式
            if (fileTypeExclusions != null && fileTypeExclusions.Count > 0)
            {
                sb.Append(" -ms=off");
                foreach (var rule in fileTypeExclusions.Where(r => !string.IsNullOrWhiteSpace(r.Pattern)))
                {
                    sb.Append($" -xr!\"{rule.Pattern.Trim()}\"");
                }
            }

            // 添加黑名单排除规则
            if (filters?.Blacklist != null && filters.Blacklist.Count > 0)
            {
                foreach (var rule in filters.Blacklist.Where(r => !string.IsNullOrWhiteSpace(r)))
                {
                    var trimmedRule = rule.Trim();

                    // 跳过正则表达式规则（7z 不直接支持）
                    if (trimmedRule.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // 7z 排除语法: -xr!pattern
                    // -x 排除, r 递归, ! 后跟模式
                    if (trimmedRule.Contains('*') || trimmedRule.Contains('?'))
                    {
                        // 通配符规则
                        sb.Append($" -xr!\"{trimmedRule}\"");
                    }
                    else if (Path.IsPathRooted(trimmedRule))
                    {
                        // 绝对路径规则 - 转换为相对路径
                        try
                        {
                            var relative = Path.GetRelativePath(sourceDir, trimmedRule);
                            if (!relative.StartsWith(".."))
                            {
                                sb.Append($" -xr!\"{relative}\"");
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        // 普通名称/相对路径规则
                        sb.Append($" -xr!\"{trimmedRule}\"");
                    }
                }
            }

            string args = sb.ToString();
            string safeArgs = string.IsNullOrWhiteSpace(settings.Password) ? args : args.Replace(settings.Password, "***");

            return await RunSevenZipProcessAsync(sevenZipExe, args, sourceDir, safeArgs);
        }

        /// <summary>
        /// 自定义文件类型处理：主压缩完成后，对匹配各规则的文件执行追加压缩（不同压缩等级）。
        /// 按压缩等级分组，每组生成一次 7z 追加命令，减少进程调用次数。
        /// </summary>
        /// <param name="sourceDir">源文件目录</param>
        /// <param name="archivePath">已创建的压缩包路径</param>
        /// <param name="settings">归档设置</param>
        /// <param name="changedFileList">增量备份时的变更文件列表（相对路径），为 null 表示全量</param>
        /// <param name="filters">黑名单过滤设置</param>
        /// <returns>所有追加操作是否全部成功</returns>
        private static async Task<bool> RunFileTypeRulePassesAsync(
            string sourceDir,
            string archivePath,
            ArchiveSettings settings,
            IReadOnlyList<string>? changedFileList = null,
            FilterSettings? filters = null)
        {
            if (!settings.FileTypeHandlingEnabled || settings.FileTypeRules == null || settings.FileTypeRules.Count == 0)
                return true;

            string sevenZipExe = ResolveSevenZipExecutable();
            if (string.IsNullOrEmpty(sevenZipExe)) return false;

            var activeRules = settings.FileTypeRules
                .Where(r => !string.IsNullOrWhiteSpace(r.Pattern))
                .ToList();
            if (activeRules.Count == 0) return true;

            // 按压缩等级分组（相同等级的模式合并处理）
            var levelGroups = activeRules
                .GroupBy(r => r.CompressionLevel)
                .ToList();

            bool allSuccess = true;
            var tempFiles = new List<string>();

            try
            {
                foreach (var group in levelGroups)
                {
                    int level = group.Key;
                    var patterns = group.Select(r => r.Pattern.Trim()).ToList();

                    Log(I18n.Format("BackupService_Log_FileTypeRulePass", level, string.Join(", ", patterns)), LogLevel.Info);

                    if (changedFileList != null)
                    {
                        // 增量模式：从变更文件列表中筛选匹配的文件
                        var matchedFiles = new List<string>();
                        foreach (var relPath in changedFileList)
                        {
                            foreach (var pattern in patterns)
                            {
                                if (MatchWildcard(relPath, pattern))
                                {
                                    matchedFiles.Add(relPath);
                                    break;
                                }
                            }
                        }

                        if (matchedFiles.Count == 0) continue;

                        string tmpList = Path.GetTempFileName();
                        tempFiles.Add(tmpList);
                        File.WriteAllLines(tmpList, matchedFiles);

                        var sb = new StringBuilder();
                        sb.Append($"a -t{settings.Format} \"{archivePath}\" @\"{tmpList}\"");
                        sb.Append($" -mx={level} -m0={settings.Method} -ms=off -ssw");
                        if (settings.CpuThreads > 0) sb.Append($" -mmt{settings.CpuThreads}"); else sb.Append(" -mmt");
                        if (!string.IsNullOrWhiteSpace(settings.Password)) sb.Append($" -p\"{settings.Password}\" -mhe=on");
                        sb.Append(" -bsp1");

                        string args = sb.ToString();
                        string safeArgs = string.IsNullOrWhiteSpace(settings.Password) ? args : args.Replace(settings.Password, "***");

                        bool ok = await RunSevenZipProcessAsync(sevenZipExe, args, sourceDir, safeArgs);
                        if (!ok) allSuccess = false;
                    }
                    else
                    {
                        // 全量/覆写模式：使用 -ir! 包含匹配模式
                        var sb = new StringBuilder();
                        sb.Append($"a -t{settings.Format} \"{archivePath}\"");
                        foreach (var pattern in patterns)
                        {
                            sb.Append($" -ir!\"{pattern}\"");
                        }
                        sb.Append($" -mx={level} -m0={settings.Method} -ms=off -ssw");
                        if (settings.CpuThreads > 0) sb.Append($" -mmt{settings.CpuThreads}"); else sb.Append(" -mmt");
                        if (!string.IsNullOrWhiteSpace(settings.Password)) sb.Append($" -p\"{settings.Password}\" -mhe=on");
                        sb.Append(" -bsp1");

                        // 添加黑名单排除（同主压缩一致）
                        if (filters?.Blacklist != null)
                        {
                            foreach (var rule in filters.Blacklist.Where(r => !string.IsNullOrWhiteSpace(r)))
                            {
                                var trimmedRule = rule.Trim();
                                if (trimmedRule.StartsWith("regex:", StringComparison.OrdinalIgnoreCase)) continue;
                                sb.Append($" -xr!\"{trimmedRule}\"");
                            }
                        }

                        string args = sb.ToString();
                        string safeArgs = string.IsNullOrWhiteSpace(settings.Password) ? args : args.Replace(settings.Password, "***");

                        bool ok = await RunSevenZipProcessAsync(sevenZipExe, args, sourceDir, safeArgs);
                        if (!ok) allSuccess = false;
                    }
                }
            }
            finally
            {
                foreach (var tmp in tempFiles) { try { File.Delete(tmp); } catch { } }
            }

            return allSuccess;
        }

        /// <summary>
        /// 简单通配符匹配（支持 * 和 ?），不区分大小写。
        /// 用于在增量文件列表中筛选匹配 FileTypeRule 模式的文件。
        /// </summary>
        private static bool MatchWildcard(string filePath, string pattern)
        {
            try
            {
                // 仅拿文件名部分做匹配（如 *.mp4 应匹配 sub/dir/video.mp4）
                string fileName = Path.GetFileName(filePath);
                string wildcardPattern = "^" + Regex.Escape(pattern)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".") + "$";
                return Regex.IsMatch(fileName, wildcardPattern, RegexOptions.IgnoreCase)
                    || Regex.IsMatch(filePath, wildcardPattern, RegexOptions.IgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static List<FileInfo> BuildRestoreChain(DirectoryInfo backupDir, FileInfo targetFile, string backupType)
        {
            var chain = new List<FileInfo>();
            if (!backupDir.Exists) return chain;

            bool isIncremental =
                (!string.IsNullOrWhiteSpace(backupType) &&
                    (backupType.Equals("Incremental", StringComparison.OrdinalIgnoreCase) ||
                     backupType.Equals("Smart", StringComparison.OrdinalIgnoreCase))) ||
                targetFile.Name.Contains("[Smart]", StringComparison.OrdinalIgnoreCase);

            if (!isIncremental)
            {
                chain.Add(targetFile);
                return chain;
            }

            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive
            };

            // 查找最近的全量备份基准
            var baseFull = backupDir
                .EnumerateFiles("*", enumOptions)
                .Where(f => f.Name.Contains("[Full]", StringComparison.OrdinalIgnoreCase) && f.LastWriteTime <= targetFile.LastWriteTime)
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();

            if (baseFull == null)
            {
                Log(I18n.Format("BackupService_Log_NoBaseFullFoundTryIncrementOnly"), LogLevel.Warning);
                chain.Add(targetFile);
                return chain;
            }

            chain.Add(baseFull);

            var increments = backupDir
                .EnumerateFiles("*", enumOptions)
                .Where(f => f.Name.Contains("[Smart]", StringComparison.OrdinalIgnoreCase)
                            && f.LastWriteTime >= baseFull.LastWriteTime
                            && f.LastWriteTime <= targetFile.LastWriteTime)
                .OrderBy(f => f.LastWriteTime)
                .ThenBy(f => f.Name); // 二级排序确保稳定性

            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            added.Add(baseFull.FullName);

            foreach (var inc in increments)
            {
                if (added.Add(inc.FullName))
                {
                    chain.Add(inc);
                }
            }

            if (added.Add(targetFile.FullName))
            {
                chain.Add(targetFile);
            }

            Log(I18n.Format("BackupService_Log_RestoreChainBuilt", chain.Count), LogLevel.Debug);

            return chain;
        }

        private static string ResolveSevenZipExecutable()
        {
            var candidates = new List<string>();
            var configPath = ConfigService.CurrentConfig.GlobalSettings?.SevenZipPath;

            void AddCandidate(string path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                try { candidates.Add(Path.GetFullPath(path)); }
                catch { candidates.Add(path); }
            }

            // 1) 用户配置
            AddCandidate(configPath);
            if (!string.IsNullOrWhiteSpace(configPath) && !Path.IsPathRooted(configPath))
            {
                AddCandidate(Path.Combine(AppContext.BaseDirectory, configPath));
            }

            // 2) 应用目录和常见安装目录
            string[] exeNames = { "7z.exe", "7zz.exe", "7za.exe" };
            foreach (var exe in exeNames)
            {
                AddCandidate(Path.Combine(AppContext.BaseDirectory, exe));

                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!string.IsNullOrWhiteSpace(pf)) AddCandidate(Path.Combine(pf, "7-Zip", exe));

                var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (!string.IsNullOrWhiteSpace(pf86)) AddCandidate(Path.Combine(pf86, "7-Zip", exe));
            }

            // 3) PATH
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathEnv))
            {
                foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = dir.Trim();
                    foreach (var exe in exeNames)
                    {
                        AddCandidate(Path.Combine(trimmed, exe));
                    }
                }
            }

            foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(path)) return path;
            }

            Log(I18n.Format("BackupService_Log_SevenZipNotFound"), LogLevel.Error);
            return null;
        }

        private static async Task<bool> RunSevenZipProcessAsync(string sevenZipExe, string arguments, string workingDirectory = null, string logArguments = null)
        {
            var pInfo = new ProcessStartInfo
            {
                FileName = sevenZipExe,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                pInfo.WorkingDirectory = workingDirectory;
            }

            Log($"[CMD] {Path.GetFileName(sevenZipExe)} {(logArguments ?? arguments)}", LogLevel.Debug);

            try
            {
                using var p = new Process { StartInfo = pInfo };
                p.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log($"[7z] {e.Data}"); };
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log($"[7z Err] {e.Data}", LogLevel.Error); };

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                await p.WaitForExitAsync();

                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Log(I18n.Format("BackupService_Log_SystemError", ex.Message), LogLevel.Error);
                return false;
            }
        }

        private static void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine(message);
            LogService.Log(message, InferLevel(message));
        }

        private static void Log(string message, LogLevel level)
        {
            System.Diagnostics.Debug.WriteLine(message);
            LogService.Log(message, level);
        }

        private static LogLevel InferLevel(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return LogLevel.Info;

            var lower = message.ToLowerInvariant();

            if (lower.Contains("[7z err]") || lower.Contains("[错误]") || lower.Contains("[失败]") || lower.Contains("[异常]") || lower.Contains("严重错误") || lower.Contains("[系统错误]"))
                return LogLevel.Error;

            if (lower.Contains("[警告]") || lower.Contains("[warning]"))
                return LogLevel.Warning;

            if (lower.Contains("[debug]") || lower.Contains("[调试]") || lower.Contains("[cmd]"))
                return LogLevel.Debug;

            return LogLevel.Info;
        }
    }
}
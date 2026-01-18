using FolderRewind.Models;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
        public static async Task BackupConfigAsync(BackupConfig config)
        {
            if (config == null) return;
            Log(I18n.Format("BackupService_Log_ConfigTaskBegin", config.Name), LogLevel.Info);

            foreach (var folder in config.SourceFolders)
            {
                await BackupFolderAsync(config, folder);
            }

            Log(I18n.Format("BackupService_Log_TaskEnd"), LogLevel.Info);
        }

        /// <summary>
        /// 备份单个文件夹
        /// </summary>
        public static async Task BackupFolderAsync(BackupConfig config, ManagedFolder folder, string comment = "")
        {
            if (config == null || folder == null) return;

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
                return;
            }

            // 允许插件在备份前创建快照并替换源路径（例如 Minecraft 热备份：先复制到 snapshot 再备份）。
            string sourcePath = folder.Path;
            try
            {
                var pluginOverride = Services.Plugins.PluginService.InvokeBeforeBackupFolder(config, folder);
                if (!string.IsNullOrWhiteSpace(pluginOverride))
                {
                    sourcePath = pluginOverride;

                    // 与 MineBackup 保持一致
                    try
                    {
                        KnotLinkService.BroadcastEvent("event=pre_hot_backup;");
                    }
                    catch
                    {
                    }
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
                return;
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
                return;
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

                    PruneOldArchives(backupSubDir, config.Archive.Format, config.Archive.KeepCount, config.Archive.Mode);

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
            }

            // 备份后回调（用于清理快照等）
            try
            {
                Services.Plugins.PluginService.InvokeAfterBackupFolder(config, folder, success, generatedFileName);
            }
            catch
            {
            }
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

        private static void PruneOldArchives(string destDir, string format, int keepCount, BackupMode mode)
        {
            if (keepCount <= 0) return;
            if (mode == BackupMode.Incremental) return; // 保留增量链，避免破坏恢复
            try
            {
                var di = new DirectoryInfo(destDir);
                if (!di.Exists) return;

                var files = di.GetFiles($"*.{format}")
                              .OrderByDescending(f => f.LastWriteTimeUtc)
                              .ToList();

                if (files.Count <= keepCount) return;

                foreach (var file in files.Skip(keepCount))
                {
                    try { file.Delete(); }
                    catch { /* ignore single delete failure */ }
                }
            }
            catch
            {
                
            }
        }

        // --- 模式 1: 全量备份 ---
        // 返回 (Success, FileName)
        private static async Task<(bool Success, string FileName)> DoFullBackupAsync(string source, string destDir, string metaDir, string baseName, BackupConfig config, string comment = "")
        {
            string fileName = GenerateFileName(baseName, config.Archive.Format, "Full", comment);
            string destFile = Path.Combine(destDir, fileName);

            // 1. 直接压缩（带黑名单过滤）
            bool result = await Run7zCommandAsync("a", source, destFile, config.Archive, null, config.Filters);

            // 2. 如果成功，生成新的元数据（为后续可能的增量备份做基准）
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
            string listFile = Path.GetTempFileName();
            File.WriteAllLines(listFile, changedFiles);

            string fileName = GenerateFileName(baseName, config.Archive.Format, "Smart", comment);
            string destFile = Path.Combine(destDir, fileName);

            // 4. 执行压缩 (使用 @listfile)
            // 注意：7z 需要工作目录在 source 下，才能正确识别相对路径列表
            bool result = await Run7zCommandAsync("a", source, destFile, config.Archive, listFile, config.Filters);

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

            // 2. 执行 update 命令 (u)（带黑名单过滤）
            // 7z u <archive_name> <file_names>
            // u 指令会更新已存在的文件并添加新文件
            bool result = await Run7zCommandAsync("u", source, targetFile.FullName, config.Archive, null, config.Filters);

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
                int idx = 0;
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
                    string prefix = "[Overwrite]";
                    string nameWithoutExt = Path.GetFileNameWithoutExtension(oldName);
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
                try
                {
                    DirectoryInfo di = new DirectoryInfo(targetDir);
                    foreach (FileInfo file in di.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            // 移除只读属性
                            if ((file.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                            {
                                file.Attributes &= ~FileAttributes.ReadOnly;
                            }
                            file.Delete();
                        }
                        catch (Exception fileEx)
                        {
                            Log(I18n.Format("BackupService_Log_RestoreDeleteFileFailed", file.Name, fileEx.Message), LogLevel.Warning);
                        }
                    }
                    // 从最深层开始删除目录
                    foreach (DirectoryInfo dir in di.EnumerateDirectories("*", SearchOption.AllDirectories).OrderByDescending(d => d.FullName.Length))
                    {
                        try
                        {
                            if ((dir.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                            {
                                dir.Attributes &= ~FileAttributes.ReadOnly;
                            }
                            dir.Delete(false);
                        }
                        catch
                        {

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
        private static async Task<bool> Run7zCommandAsync(string commandMode, string sourceDir, string archivePath, ArchiveSettings settings, string? listFile = null, FilterSettings? filters = null)
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
            if (!string.IsNullOrWhiteSpace(settings.Password))
            {
                sb.Append($" -p\"{settings.Password}\" -mhe=on");
            }
            sb.Append(" -bsp1"); // 开启进度输出到 stderr/stdout

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
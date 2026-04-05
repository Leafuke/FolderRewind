using FolderRewind.Models;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static class BackupService
    {
        public static ObservableCollection<BackupTask> ActiveTasks { get; } = new();

        // 还原阶段会用内部标记目录记录“仅删除”动作，完成后必须清理避免污染用户目录。
        private const string InternalRestoreMarkerDirectoryName = "__FolderRewind_Internal";
        private const string InternalRestoreMarkerFileName = "__DeletedOnly.marker";
        private const string MissingEncryptionPasswordMessage = "Encrypted backup password is missing for this configuration.";

        // 变更集是增量备份/删除标记/元数据写入的统一输入。
        private sealed class BackupChangeSet
        {
            public List<string> AddedFiles { get; } = new();
            public List<string> ModifiedFiles { get; } = new();
            public List<string> DeletedFiles { get; } = new();

            public bool HasChanges => AddedFiles.Count > 0
                || ModifiedFiles.Count > 0
                || DeletedFiles.Count > 0;
        }

        private sealed class SmartRestoreArchiveGroup
        {
            public required FileInfo Archive { get; init; }
            public required List<string> Files { get; init; }
        }

        private sealed class SmartRestorePlan
        {
            public required List<FileInfo> Chain { get; init; }
            public required List<SmartRestoreArchiveGroup> ArchiveGroups { get; init; }
        }

        private enum RestoreChainBuildStatus
        {
            Success = 0,
            MissingBaseFull = 1,
            NotFound = 2
        }

        public sealed class DeleteBackupResult
        {
            public bool Success { get; init; }
            public bool ArchiveDeleted { get; init; }
            public bool HistoryUpdated { get; init; }
            public string Message { get; init; } = string.Empty;
        }

        private sealed class DeleteArchiveExecutionResult
        {
            public bool Success { get; set; }
            public bool ArchiveDeleted { get; set; }
            public bool HistoryUpdated { get; set; }
            public string DeletedFileName { get; set; } = string.Empty;
            public string? RenamedFromFileName { get; set; }
            public string? RenamedToFileName { get; set; }
            public string? RenamedToBackupType { get; set; }
            public string Message { get; set; } = string.Empty;
        }

        private sealed class PruneArchivesResult
        {
            public bool Success { get; set; } = true;
            public int DeletedCount { get; set; }
            public string Message { get; set; } = string.Empty;
        }

        private static readonly object SevenZipResolutionLock = new();
        private static string? _cachedSevenZipExecutable;
        private static string? _cachedSevenZipConfigPath;
        private static string? _cachedSevenZipPathEnvironment;

        private static Task RunOnUIAsync(Action action)
        {
            return UiDispatcherService.RunOnUiAsync(action);
        }

        private static Task<T> RunOnUIAsync<T>(Func<Task<T>> action)
        {
            return UiDispatcherService.RunOnUiAsync(action);
        }

        private static bool TryResolveStorageFolderName(string? rawFolderName, string? fallbackPath, out string storageFolderName)
            => BackupStoragePathService.TryResolveStorageFolderName(rawFolderName, fallbackPath, out storageFolderName);

        private static bool TryBuildPathWithinRoot(string rootPath, string childName, out string fullPath)
            => BackupStoragePathService.TryBuildPathWithinRoot(rootPath, childName, out fullPath);

        private static bool IsPathInsideRoot(string candidatePath, string rootPath)
            => BackupStoragePathService.IsPathInsideRoot(candidatePath, rootPath);

        private static bool TryResolveBackupStoragePaths(
            string destinationRoot,
            string folderDisplayName,
            string? fallbackPath,
            out string storageFolderName,
            out string backupSubDir,
            out string metadataDir)
            => BackupStoragePathService.TryResolveBackupStoragePaths(
                destinationRoot,
                folderDisplayName,
                fallbackPath,
                out storageFolderName,
                out backupSubDir,
                out metadataDir);

        private static string NormalizePathForRuleMatching(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var normalized = path.Trim()
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .Replace(Path.DirectorySeparatorChar, '/');

            while (normalized.Contains("//", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
            }

            return normalized.Trim('/');
        }

        private static bool PathContainsRuleAtBoundary(string path, string normalizedRule)
        {
            var normalizedPath = NormalizePathForRuleMatching(path);
            if (string.IsNullOrEmpty(normalizedPath) || string.IsNullOrEmpty(normalizedRule))
            {
                return false;
            }

            int searchStart = 0;
            while (searchStart < normalizedPath.Length)
            {
                int matchIndex = normalizedPath.IndexOf(normalizedRule, searchStart, StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0)
                {
                    return false;
                }

                bool startBoundary = matchIndex == 0 || normalizedPath[matchIndex - 1] == '/';
                int matchEnd = matchIndex + normalizedRule.Length;
                bool endBoundary = matchEnd == normalizedPath.Length || normalizedPath[matchEnd] == '/';

                if (startBoundary && endBoundary)
                {
                    return true;
                }

                searchStart = matchIndex + 1;
            }

            return false;
        }

        private static bool MatchesPathBoundary(string fullPath, string? relativePath, string rule)
        {
            var normalizedRule = NormalizePathForRuleMatching(rule);
            if (string.IsNullOrEmpty(normalizedRule))
            {
                return false;
            }

            if (PathContainsRuleAtBoundary(fullPath, normalizedRule))
            {
                return true;
            }

            return !string.IsNullOrEmpty(relativePath)
                && PathContainsRuleAtBoundary(relativePath, normalizedRule);
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
                if (!relativePath.StartsWith("..", StringComparison.Ordinal))
                {
                    relativePathLower = relativePath.ToLowerInvariant();
                }
            }
            catch (Exception ex)
            {
                Log($"[Filter][Debug] Failed to build relative path for blacklist matching: {ex.Message}", LogLevel.Debug);
            }

            // 缓存编译好的正则表达式
            var regexCache = new Dictionary<string, Regex>();

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
                        if (!regexCache.TryGetValue(pattern, out var regex))
                        {
                            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                            regexCache[pattern] = regex;
                        }

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

                    // 2. 路径边界匹配（避免子串误伤，例如 voxy 误匹配 VoxyFab）
                    if (MatchesPathBoundary(fileToCheck, relativePathLower, rule))
                    {
                        return true;
                    }

                    // 3. 支持通配符匹配 (*, ?)
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
                        catch (Exception ex)
                        {
                            Log($"[Filter][Debug] Invalid wildcard blacklist rule '{rule}': {ex.Message}", LogLevel.Debug);
                        }
                    }

                    // 4. 处理热备份时的路径映射（参考 MineBackup）
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
                        catch (Exception ex)
                        {
                            Log($"[Filter][Debug] Failed to remap rooted blacklist rule '{rule}': {ex.Message}", LogLevel.Debug);
                        }
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
        public static async Task<bool> BackupFolderAsync(BackupConfig config, ManagedFolder folder, string? comment = "", bool forceFullBackup = false)
        {
            if (config == null || folder == null) return false;
            comment ??= string.Empty;

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
                return await HandlePluginBackupAsync(config, folder, task, handlerPlugin, comment);
            }

            // 允许插件在备份前创建快照并替换源路径（例如 Minecraft 热备份：先复制到 snapshot 再备份）。
            string sourcePath = folder.Path;
            try
            {
                var pluginOverride = Services.Plugins.PluginService.InvokeBeforeBackupFolder(config, folder);
                if (!string.IsNullOrWhiteSpace(pluginOverride))
                {
                    sourcePath = pluginOverride;
                }
            }
            catch
            {
                // 插件异常不会影响核心备份流程（具体异常会在 PluginService 内记录）
            }
            if (!Directory.Exists(sourcePath))
            {
                Log(I18n.Format("BackupService_Log_SourceFolderMissing", sourcePath), LogLevel.Error);
                await RunOnUIAsync(() =>
                {
                    folder.StatusText = I18n.Format("BackupService_Folder_SourceNotFound");
                    task.Status = I18n.Format("BackupService_Task_Failed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = I18n.Format("BackupService_Folder_SourceNotFound");
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
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = I18n.Format("BackupService_Folder_TargetNotSet");
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

            if (!TryResolveBackupStoragePaths(
                config.DestinationPath,
                folder.DisplayName,
                folder.Path,
                out var storageFolderName,
                out var backupSubDir,
                out var metadataDir))
            {
                string invalidFolderNameMessage = I18n.GetString("BackupService_Log_InvalidStorageFolderName");
                Log(invalidFolderNameMessage, LogLevel.Error);
                await RunOnUIAsync(() =>
                {
                    folder.StatusText = I18n.Format("BackupService_Task_Failed");
                    task.Status = I18n.Format("BackupService_Task_Failed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = invalidFolderNameMessage;
                });

                try
                {
                    KnotLinkService.BroadcastEvent($"event=backup_failed;config={configIndex};world={folder.DisplayName};error=invalid_folder_name");
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
            string? generatedFileName = null;
            try
            {

                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.Format("BackupService_Task_Processing");
                    folder.StatusText = I18n.Format("BackupService_Folder_BackupRunning");
                });

                // 调用核心逻辑，传入 task 以便更新进度

                // FORCE_FULL 指令可绕过当前配置模式，直接执行一次全量备份。
                if (forceFullBackup)
                {
                    var res = await DoFullBackupAsync(sourcePath, backupSubDir, metadataDir, folder.DisplayName, config, comment, task);
                    success = res.Success;
                    generatedFileName = res.FileName;
                }
                else
                {
                    // 根据模式分发逻辑
                    switch (config.Archive.Mode)
                    {
                        case BackupMode.Incremental:
                            {
                                var res = await DoSmartBackupAsync(sourcePath, backupSubDir, metadataDir, folder.DisplayName, config, comment, task);
                                success = res.Success;
                                generatedFileName = res.FileName;
                                break;
                            }
                        case BackupMode.Overwrite:
                            {
                                var res = await DoOverwriteBackupAsync(sourcePath, backupSubDir, metadataDir, folder.DisplayName, config, comment, task);
                                success = res.Success;
                                generatedFileName = res.FileName;
                                break;
                            }
                        case BackupMode.Full:
                        default:
                            {
                                var res = await DoFullBackupAsync(sourcePath, backupSubDir, metadataDir, folder.DisplayName, config, comment, task);
                                success = res.Success;
                                generatedFileName = res.FileName;
                                break;
                            }
                    }
                }
            }
            catch (Exception ex)
            {
                Log(I18n.Format("BackupService_Log_Exception", ex.Message), LogLevel.Error);
                success = false;
                await RunOnUIAsync(() => { if (string.IsNullOrEmpty(task.ErrorMessage)) task.ErrorMessage = ex.Message; });
            }

            if (success)
            {
                var completedFileName = string.IsNullOrWhiteSpace(generatedFileName) ? null : generatedFileName;
                bool hasNewFile = completedFileName != null;

                if (completedFileName != null)
                {
                    ConfigService.Save();

                    // 增量模式下，根据实际生成的文件名区分 Full 和 Smart
                    string typeStr;
                    if (forceFullBackup)
                    {
                        typeStr = "Full";
                    }
                    else if (config.Archive.Mode == BackupMode.Incremental)
                    {
                        typeStr = completedFileName.StartsWith("[Full]", StringComparison.OrdinalIgnoreCase) ? "Full" : "Smart";
                    }
                    else
                    {
                        typeStr = config.Archive.Mode.ToString();
                    }
                    HistoryService.AddEntry(config, folder, completedFileName, typeStr, comment, storageFolderName);

                    var pruneResult = await Task.Run(() => PruneOldArchives(
                        backupSubDir,
                        config.Archive.Format,
                        config.Archive.KeepCount,
                        config.Archive.Mode,
                        config.Archive.SafeDeleteEnabled,
                        config,
                        folder.DisplayName)).ConfigureAwait(false);

                    if (!pruneResult.Success)
                    {
                        string pruneWarning = I18n.Format("BackupService_Warning_PostBackupCleanupFailed", folder.DisplayName, pruneResult.Message);
                        Log(I18n.Format("BackupService_Log_PostBackupCleanupFailed", folder.DisplayName, pruneResult.Message), LogLevel.Warning);
                        NotificationService.ShowWarning(pruneWarning);
                    }

                    // 备份完成后检查文件大小，过小时发出警告
                    try
                    {
                        var archiveFile = Path.Combine(backupSubDir, completedFileName);
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
                        KnotLinkService.BroadcastEvent($"event=backup_success;config={configIndex};world={folder.DisplayName};file={completedFileName}");
                    }
                    catch
                    {
                    }

                    CloudSyncService.QueueUploadAfterBackup(config, folder, completedFileName, comment);
                }

                await RunOnUIAsync(() =>
                {
                    task.Status = hasNewFile
                        ? I18n.Format("BackupService_Task_Completed")
                        : I18n.Format("BackupService_Task_NoChanges");
                    task.Progress = 100;
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = true;

                    folder.StatusText = hasNewFile
                        ? I18n.Format("BackupService_Folder_BackupCompleted")
                        : I18n.Format("BackupService_Task_NoChanges");
                    if (hasNewFile)
                    {
                        folder.LastBackupTime = DateTime.Now.ToString("yyyy/MM/dd HH:mm");
                    }
                });

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
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
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

            // 注意：这里返回“是否真的产出新归档”，会影响自动化里的无变更计数策略。
            return success && !string.IsNullOrWhiteSpace(generatedFileName);
        }

        /// <summary>
        /// 由插件完全接管的备份流程
        /// </summary>
        private static async Task<bool> HandlePluginBackupAsync(
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
                        task.IsIndeterminate = false;
                        task.IsSuccess = true;

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
                        if (!TryResolveStorageFolderName(folder.DisplayName, folder.Path, out var storageFolderName))
                        {
                            throw new InvalidOperationException(I18n.GetString("BackupService_Log_InvalidStorageFolderName"));
                        }

                        HistoryService.AddEntry(config, folder, result.GeneratedFileName!, "Plugin", comment, storageFolderName);
                        CloudSyncService.QueueUploadAfterBackup(config, folder, result.GeneratedFileName, comment);
                    }

                    Log(
                        hasNewFile
                            ? I18n.Format("BackupService_Log_PluginBackupSucceeded", folder.DisplayName)
                            : I18n.Format("BackupService_Log_PluginBackupSkippedNoChanges", folder.DisplayName),
                        LogLevel.Info);

                    return hasNewFile;
                }
                else
                {
                    await RunOnUIAsync(() =>
                    {
                        task.Status = I18n.Format("BackupService_Task_Failed");
                        task.IsCompleted = true;
                        task.IsIndeterminate = false;
                        task.IsSuccess = false;
                        task.ErrorMessage = result.Message ?? string.Empty;
                        folder.StatusText = I18n.Format("BackupService_Folder_BackupFailed");
                    });

                    Log(I18n.Format("BackupService_Log_PluginBackupFailed", folder.DisplayName, result.Message ?? string.Empty), LogLevel.Error);
                    return false;
                }
            }
            catch (Exception ex)
            {
                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.Format("BackupService_Task_Exception");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = ex.Message;
                    folder.StatusText = I18n.Format("BackupService_Folder_PluginException");
                });

                Log(I18n.Format("BackupService_Log_PluginException", folder.DisplayName, ex.Message), LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// 由插件完全接管的还原流程
        /// </summary>
        private static async Task HandlePluginRestoreAsync(
            BackupConfig config,
            ManagedFolder folder,
            HistoryItem historyItem,
            BackupTask task,
            Services.Plugins.IFolderRewindPlugin plugin,
            int configIndex)
        {
            Log(I18n.Format("BackupService_Log_PluginRestoreTakeover", plugin.Manifest.Name, folder.DisplayName), LogLevel.Info);

            await RunOnUIAsync(() =>
            {
                task.Status = I18n.Format("BackupService_Task_PluginProcessing");
                task.Progress = 0;
                task.IsIndeterminate = true;
            });

            bool restoreStarted = false;

            try
            {
                try
                {
                    KnotLinkService.BroadcastEvent($"event=restore_started;config={configIndex};world={folder.DisplayName}");
                    restoreStarted = true;
                }
                catch
                {
                }

                var result = await Services.Plugins.PluginService.InvokePluginRestoreAsync(
                    plugin,
                    config,
                    folder,
                    historyItem.FileName,
                    async (progress, status) =>
                    {
                        await RunOnUIAsync(() =>
                        {
                            task.Progress = progress;
                            task.Status = string.IsNullOrWhiteSpace(status)
                                ? I18n.Format("BackupService_Task_PluginProcessing")
                                : status;
                            task.IsIndeterminate = false;
                        });
                    });

                if (result.Success)
                {
                    await RunOnUIAsync(() =>
                    {
                        task.Status = I18n.GetString("BackupService_Task_RestoreCompleted");
                        task.Progress = 100;
                        task.IsCompleted = true;
                        task.IsIndeterminate = false;
                        task.IsSuccess = true;
                        task.ErrorMessage = string.Empty;
                    });

                    Log(I18n.Format("BackupService_Log_PluginRestoreSucceeded", folder.DisplayName), LogLevel.Info);
                    NotificationService.NotifyRestoreCompleted(folder.DisplayName, true, I18n.GetString("BackupService_Task_RestoreCompleted"));

                    try
                    {
                        KnotLinkService.BroadcastEvent($"event=restore_success;config={configIndex};world={folder.DisplayName};backup={historyItem.FileName}");
                    }
                    catch
                    {
                    }

                    return;
                }

                string failureMessage = string.IsNullOrWhiteSpace(result.Message)
                    ? I18n.GetString("BackupService_Task_RestoreFailed")
                    : result.Message!;

                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.GetString("BackupService_Task_RestoreFailed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = failureMessage;
                });

                Log(I18n.Format("BackupService_Log_PluginRestoreFailed", folder.DisplayName, failureMessage), LogLevel.Error);

                if (restoreStarted)
                {
                    try
                    {
                        KnotLinkService.BroadcastEvent("event=restore_finished;status=failure;reason=plugin_restore_failed");
                    }
                    catch
                    {
                    }
                }

                NotificationService.NotifyRestoreCompleted(folder.DisplayName, false, failureMessage);
            }
            catch (Exception ex)
            {
                await RunOnUIAsync(() =>
                {
                    task.Status = I18n.GetString("BackupService_Task_RestoreFailed");
                    task.IsCompleted = true;
                    task.IsIndeterminate = false;
                    task.IsSuccess = false;
                    task.ErrorMessage = ex.Message;
                });

                Log(I18n.Format("BackupService_Log_PluginException", folder.DisplayName, ex.Message), LogLevel.Error);

                if (restoreStarted)
                {
                    try
                    {
                        KnotLinkService.BroadcastEvent("event=restore_finished;status=failure;reason=plugin_restore_exception");
                    }
                    catch
                    {
                    }
                }

                NotificationService.NotifyRestoreCompleted(folder.DisplayName, false, ex.Message);
            }
        }

        public static async Task<DeleteBackupResult> DeleteBackupAsync(BackupConfig config, ManagedFolder folder, HistoryItem historyItem, bool deleteArchive)
        {
            if (config == null || folder == null || historyItem == null)
            {
                return new DeleteBackupResult
                {
                    Success = false,
                    Message = "Invalid delete request."
                };
            }

            if (!deleteArchive)
            {
                HistoryService.RemoveEntry(historyItem);
                return new DeleteBackupResult
                {
                    Success = true,
                    ArchiveDeleted = false,
                    HistoryUpdated = true
                };
            }

            return await Task.Run(() =>
            {
                string? targetFilePath = HistoryService.GetBackupFilePath(config, folder, historyItem);
                if (string.IsNullOrWhiteSpace(targetFilePath))
                {
                    return new DeleteBackupResult
                    {
                        Success = false,
                        Message = "Invalid backup path in history record."
                    };
                }

                var targetFile = new FileInfo(targetFilePath);
                var backupDir = targetFile.Directory;
                if (backupDir == null)
                {
                    return new DeleteBackupResult
                    {
                        Success = false,
                        Message = "Invalid backup directory."
                    };
                }

                string backupFolderName = backupDir.Name;
                string format = targetFile.Extension.TrimStart('.');
                if (string.IsNullOrWhiteSpace(format))
                {
                    format = config.Archive.Format;
                }

                var deleteResult = DeleteBackupArchiveInternal(
                    targetFile,
                    backupDir,
                    format,
                    config,
                    backupFolderName,
                    config.Archive.SafeDeleteEnabled);

                return new DeleteBackupResult
                {
                    Success = deleteResult.Success,
                    ArchiveDeleted = deleteResult.ArchiveDeleted,
                    HistoryUpdated = deleteResult.HistoryUpdated,
                    Message = deleteResult.Message
                };
            });
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

        private static PruneArchivesResult PruneOldArchives(string destDir, string format, int keepCount, BackupMode mode, bool safeDeleteEnabled = true, BackupConfig? config = null, string? folderName = null)
        {
            var result = new PruneArchivesResult();
            if (keepCount <= 0) return result;
            if (mode == BackupMode.Incremental && !safeDeleteEnabled) return result; // 不启用安全删除时，增量模式跳过自动清理以保护链

            try
            {
                var di = new DirectoryInfo(destDir);
                if (!di.Exists) return result;

                string resolvedFolderName = string.IsNullOrWhiteSpace(folderName) ? di.Name : folderName;
                var importantFiles = GetImportantBackupFiles(config, resolvedFolderName);

                CleanupArchiveTempArtifacts(di, format);

                int deleteGuard = 0;
                while (true)
                {
                    var files = di.GetFiles($"*.{format}")
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .ToList();

                    if (files.Count <= keepCount) break;

                    var deletableFiles = files
                        .Where(f => !importantFiles.Contains(f.Name))
                        .OrderBy(f => f.LastWriteTimeUtc)
                        .ToList();

                    if (deletableFiles.Count <= keepCount) break;

                    var oldestFile = deletableFiles.FirstOrDefault();
                    if (oldestFile == null) break;

                    var deleteResult = DeleteBackupArchiveInternal(oldestFile, di, format, config, resolvedFolderName, safeDeleteEnabled);
                    if (!deleteResult.Success)
                    {
                        result.Success = false;
                        result.Message = string.IsNullOrWhiteSpace(deleteResult.Message)
                            ? oldestFile.Name
                            : deleteResult.Message;
                        Log(I18n.Format("BackupService_Log_PruneDeleteFailed", oldestFile.Name, deleteResult.Message), LogLevel.Warning);
                        break;
                    }

                    result.DeletedCount++;
                    importantFiles.Remove(oldestFile.Name);
                    deleteGuard++;
                    if (deleteGuard > Math.Max(files.Count * 2, keepCount + 8))
                    {
                        result.Success = false;
                        result.Message = "Delete guard triggered.";
                        break;
                    }
                }

                CleanupArchiveTempArtifacts(di, format);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = ex.Message;
            }

            return result;
        }

        private static HashSet<string> GetImportantBackupFiles(BackupConfig? config, string folderName)
        {
            var importantFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return importantFiles;
            }

            try
            {
                if (config != null)
                {
                    foreach (var entry in HistoryService.GetEntriesForFolder(config.Id, folderName))
                    {
                        if (entry.IsImportant && !string.IsNullOrWhiteSpace(entry.FileName))
                        {
                            importantFiles.Add(entry.FileName);
                        }
                    }

                    return importantFiles;
                }

                var allConfigs = ConfigService.CurrentConfig?.BackupConfigs;
                if (allConfigs == null)
                {
                    return importantFiles;
                }

                foreach (var cfg in allConfigs)
                {
                    foreach (var entry in HistoryService.GetEntriesForFolder(cfg.Id, folderName))
                    {
                        if (entry.IsImportant && !string.IsNullOrWhiteSpace(entry.FileName))
                        {
                            importantFiles.Add(entry.FileName);
                        }
                    }
                }
            }
            catch
            {
            }

            return importantFiles;
        }

        private static DeleteArchiveExecutionResult DeleteBackupArchiveInternal(
            FileInfo fileToDelete,
            DirectoryInfo backupDir,
            string format,
            BackupConfig? config = null,
            string? folderName = null,
            bool safeDeleteEnabled = true)
        {
            var result = new DeleteArchiveExecutionResult
            {
                DeletedFileName = fileToDelete.Name
            };

            string resolvedFolderName = string.IsNullOrWhiteSpace(folderName) ? backupDir.Name : folderName;
            string normalizedFormat = string.IsNullOrWhiteSpace(format)
                ? fileToDelete.Extension.TrimStart('.')
                : format.TrimStart('.');

            try
            {
                if (backupDir.Exists)
                {
                    CleanupArchiveTempArtifacts(backupDir, normalizedFormat);

                    if (fileToDelete.Exists && safeDeleteEnabled && TryGetSafeDeleteSuccessor(fileToDelete, backupDir, normalizedFormat, config, resolvedFolderName, out var nextFile))
                    {
                        result.Success = TrySafeDeleteArchive(fileToDelete, nextFile!, backupDir, normalizedFormat, config, resolvedFolderName, result);
                    }
                    else
                    {
                        if (fileToDelete.Exists)
                        {
                            try
                            {
                                if ((fileToDelete.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                                {
                                    fileToDelete.Attributes &= ~FileAttributes.ReadOnly;
                                }
                            }
                            catch
                            {
                            }

                            fileToDelete.Delete();
                            result.ArchiveDeleted = true;
                            Log(I18n.Format("BackupService_Log_PrunedOldBackup", fileToDelete.Name), LogLevel.Info);
                        }
                        else
                        {
                            Log(I18n.Format("BackupService_Log_BackupFileNotFound", fileToDelete.FullName), LogLevel.Warning);
                        }

                        result.Success = true;
                    }

                    CleanupArchiveTempArtifacts(backupDir, normalizedFormat);
                }
                else
                {
                    result.Success = true;
                }

                if (result.Success && config != null)
                {
                    if (!string.IsNullOrWhiteSpace(result.RenamedFromFileName)
                        && !string.IsNullOrWhiteSpace(result.RenamedToFileName))
                    {
                        HistoryService.RenameEntriesForFile(
                            config.Id,
                            resolvedFolderName,
                            result.RenamedFromFileName,
                            result.RenamedToFileName,
                            result.RenamedToBackupType);
                    }

                    int removedCount = HistoryService.RemoveEntriesForFile(config.Id, resolvedFolderName, result.DeletedFileName);
                    result.HistoryUpdated = removedCount > 0 || !string.IsNullOrWhiteSpace(result.RenamedFromFileName);

                    SynchronizeMetadataAfterArchiveDeletion(
                        config,
                        resolvedFolderName,
                        result.DeletedFileName,
                        result.RenamedFromFileName,
                        result.RenamedToFileName,
                        result.RenamedToBackupType);
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = ex.Message;
                Log(I18n.Format("BackupService_Log_PruneDeleteFailed", fileToDelete.Name, ex.Message), LogLevel.Warning);
            }

            return result;
        }

        private static bool TryGetSafeDeleteSuccessor(
            FileInfo fileToDelete,
            DirectoryInfo backupDir,
            string format,
            BackupConfig? config,
            string? folderName,
            out FileInfo? nextFile)
        {
            nextFile = null;
            if (!fileToDelete.Exists || !backupDir.Exists)
            {
                return false;
            }

            bool currentIsChainArchive = IsFullBackupFile(fileToDelete, config, folderName)
                || IsIncrementalBackupFile(fileToDelete, config, folderName);
            if (!currentIsChainArchive)
            {
                return false;
            }

            var allFiles = backupDir.GetFiles($"*.{format}")
                .OrderBy(f => f.LastWriteTimeUtc)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int index = allFiles.FindIndex(f => string.Equals(f.FullName, fileToDelete.FullName, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= allFiles.Count)
            {
                return false;
            }

            var candidate = allFiles[index + 1];
            if (!IsIncrementalBackupFile(candidate, config, folderName))
            {
                return false;
            }

            nextFile = candidate;
            return true;
        }

        /// <summary>
        /// 安全删除备份文件：将当前节点与它的后继 Smart 节点重建成一个新的后继归档，避免直接在原归档旁生成 7z 的 .tmp 临时文件。
        /// </summary>
        private static bool TrySafeDeleteArchive(
            FileInfo fileToDelete,
            FileInfo nextFile,
            DirectoryInfo backupDir,
            string format,
            BackupConfig? config,
            string? folderName,
            DeleteArchiveExecutionResult result)
        {
            Log(I18n.Format("BackupService_Log_SafeDeleteStart", fileToDelete.Name), LogLevel.Info);

            string? sevenZipExe = ResolveSevenZipExecutable();
            if (string.IsNullOrEmpty(sevenZipExe))
            {
                result.Message = I18n.GetString("BackupService_Log_SafeDeleteNo7z");
                Log(result.Message, LogLevel.Warning);
                return false;
            }

            string tempDir = Path.Combine(backupDir.FullName, "__FolderRewind_SafeDelete_" + Guid.NewGuid().ToString("N"));
            string mergeDir = Path.Combine(tempDir, "merged");
            string stagedNextPath = Path.Combine(tempDir, nextFile.Name + ".original");
            string? safeDeletePassword = config != null ? ResolvePassword(config) : null;
            if (config?.IsEncrypted == true && string.IsNullOrWhiteSpace(safeDeletePassword))
            {
                result.Message = MissingEncryptionPasswordMessage;
                Log(result.Message, LogLevel.Error);
                return false;
            }
            var archiveSettings = CreateArchiveSettingsForSafeDelete(config?.Archive, format);

            try
            {
                Directory.CreateDirectory(mergeDir);

                Log(I18n.Format("BackupService_Log_SafeDeleteStep1"), LogLevel.Info);
                if (!ExtractArchiveToDirectorySync(sevenZipExe, fileToDelete.FullName, mergeDir, safeDeletePassword, archiveSettings.RunCompressionAtLowPriority))
                {
                    result.Message = I18n.GetString("BackupService_Log_SafeDeleteExtractFailed");
                    Log(result.Message, LogLevel.Error);
                    return false;
                }

                if (!ExtractArchiveToDirectorySync(sevenZipExe, nextFile.FullName, mergeDir, safeDeletePassword, archiveSettings.RunCompressionAtLowPriority))
                {
                    result.Message = I18n.GetString("BackupService_Log_SafeDeleteExtractFailed");
                    Log(result.Message, LogLevel.Error);
                    return false;
                }

                Log(I18n.Format("BackupService_Log_SafeDeleteStep2"), LogLevel.Info);
                string rebuiltArchivePath = Path.Combine(tempDir, "merged." + archiveSettings.Format);
                if (!CreateArchiveFromDirectorySync(sevenZipExe, mergeDir, rebuiltArchivePath, archiveSettings, safeDeletePassword))
                {
                    result.Message = I18n.GetString("BackupService_Log_SafeDeleteMergeFailed");
                    Log(result.Message, LogLevel.Error);
                    return false;
                }

                bool promoteToFull = IsFullBackupFile(fileToDelete, config, folderName)
                    && IsIncrementalBackupFile(nextFile, config, folderName);
                string finalFileName = promoteToFull && nextFile.Name.Contains("[Smart]", StringComparison.OrdinalIgnoreCase)
                    ? nextFile.Name.Replace("[Smart]", "[Full]", StringComparison.OrdinalIgnoreCase)
                    : nextFile.Name;
                string finalPath = Path.Combine(backupDir.FullName, finalFileName);
                DateTime originalModTime = nextFile.LastWriteTimeUtc;

                File.Move(nextFile.FullName, stagedNextPath);
                try
                {
                    if (File.Exists(finalPath))
                    {
                        File.Delete(finalPath);
                    }

                    File.Move(rebuiltArchivePath, finalPath);
                    File.SetLastWriteTimeUtc(finalPath, originalModTime);

                    try
                    {
                        if ((fileToDelete.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            fileToDelete.Attributes &= ~FileAttributes.ReadOnly;
                        }
                    }
                    catch
                    {
                    }

                    fileToDelete.Delete();
                    result.ArchiveDeleted = true;

                    if (File.Exists(stagedNextPath))
                    {
                        File.Delete(stagedNextPath);
                    }
                }
                catch (Exception replaceEx)
                {
                    try
                    {
                        if (File.Exists(finalPath))
                        {
                            File.Delete(finalPath);
                        }
                    }
                    catch
                    {
                    }

                    try
                    {
                        if (File.Exists(stagedNextPath))
                        {
                            File.Move(stagedNextPath, nextFile.FullName);
                        }
                    }
                    catch
                    {
                    }

                    result.Message = replaceEx.Message;
                    Log(I18n.Format("BackupService_Log_SafeDeleteFatalError", replaceEx.Message), LogLevel.Error);
                    return false;
                }

                if (!string.Equals(finalFileName, nextFile.Name, StringComparison.OrdinalIgnoreCase))
                {
                    result.RenamedFromFileName = nextFile.Name;
                    result.RenamedToFileName = finalFileName;
                    result.RenamedToBackupType = "Full";
                    Log(I18n.Format("BackupService_Log_SafeDeleteRenamed", finalFileName), LogLevel.Info);
                }

                Log(I18n.Format("BackupService_Log_SafeDeleteSuccess", fileToDelete.Name), LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                result.Message = ex.Message;
                Log(I18n.Format("BackupService_Log_SafeDeleteFatalError", ex.Message), LogLevel.Error);
                return false;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        ClearReadonlyAttributes(tempDir);
                        Directory.Delete(tempDir, true);
                    }
                }
                catch
                {
                }
            }
        }

        private static bool ExtractArchiveToDirectorySync(string sevenZipExe, string archivePath, string targetDir, string? password, bool runAtLowPriority = false)
        {
            string extractArgs = $"x \"{archivePath}\" -o\"{targetDir}\" -y -aoa";
            if (!string.IsNullOrWhiteSpace(password))
            {
                extractArgs += $" -p\"{password}\"";
            }
            return RunSevenZipProcessSync(sevenZipExe, extractArgs, runAtLowPriority: runAtLowPriority);
        }

        private static bool CreateArchiveFromDirectorySync(string sevenZipExe, string sourceDir, string archivePath, ArchiveSettings settings, string? password)
        {
            var sb = new StringBuilder();
            sb.Append($"a -t{settings.Format} \"{archivePath}\" .\\*");
            sb.Append($" -mx={settings.CompressionLevel} -m0={settings.Method} -ssw");

            int cpuThreads = NormalizeCpuThreadCount(settings.CpuThreads);
            if (cpuThreads > 0)
            {
                sb.Append($" -mmt{cpuThreads}");
            }
            else
            {
                sb.Append(" -mmt");
            }

            if (!string.IsNullOrWhiteSpace(password))
            {
                sb.Append($" -p\"{password}\" -mhe=on");
            }

            sb.Append(" -bsp1");
            return RunSevenZipProcessSync(sevenZipExe, sb.ToString(), sourceDir, settings.RunCompressionAtLowPriority);
        }

        private static ArchiveSettings CreateArchiveSettingsForSafeDelete(ArchiveSettings? sourceSettings, string format)
        {
            return new ArchiveSettings
            {
                Format = string.IsNullOrWhiteSpace(format) ? (sourceSettings?.Format ?? "7z") : format,
                CompressionLevel = sourceSettings?.CompressionLevel ?? 5,
                Method = string.IsNullOrWhiteSpace(sourceSettings?.Method) ? "LZMA2" : sourceSettings.Method,
                CpuThreads = sourceSettings?.CpuThreads ?? 0,
                RunCompressionAtLowPriority = sourceSettings?.RunCompressionAtLowPriority ?? false
            };
        }

        private static void CleanupArchiveTempArtifacts(DirectoryInfo backupDir, string format)
        {
            if (!backupDir.Exists) return;

            try
            {
                foreach (var file in backupDir.GetFiles())
                {
                    if (!IsArchiveTempArtifact(file, format))
                    {
                        continue;
                    }

                    try
                    {
                        if ((file.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            file.Attributes &= ~FileAttributes.ReadOnly;
                        }
                        file.Delete();
                    }
                    catch
                    {
                    }
                }

                foreach (var dir in backupDir.GetDirectories("__FolderRewind_SafeDelete_*"))
                {
                    try
                    {
                        ClearReadonlyAttributes(dir.FullName);
                        dir.Delete(true);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static bool IsArchiveTempArtifact(FileInfo file, string format)
        {
            if (file == null || string.IsNullOrWhiteSpace(format))
            {
                return false;
            }

            string pattern = $@"\.{Regex.Escape(format)}\.tmp\d*$";
            return Regex.IsMatch(file.Name, pattern, RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// 同步方式运行 7z 进程（用于安全删除等非异步场景）
        /// </summary>
        private static bool RunSevenZipProcessSync(string sevenZipExe, string arguments, string? workingDirectory = null, bool runAtLowPriority = false)
        {
            try
            {
                arguments = EnsureSswArgument(arguments);

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

                // 修复进程未退出读取 ExitCode 抛出系统错误的神秘问题。
                using var p = new Process { StartInfo = pInfo };
                string? lastErrorLine = null;

                p.OutputDataReceived += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    Log($"[7z] {e.Data}");
                };
                p.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    lastErrorLine = e.Data;
                    Log($"[7z Err] {e.Data}", LogLevel.Error);
                };

                if (!p.Start())
                {
                    Log(I18n.Format("BackupService_Log_SystemError", I18n.GetString("BackupService_Log_SevenZipStartFailed")), LogLevel.Error);
                    return false;
                }

                ApplyLowPriorityIfRequested(p, runAtLowPriority);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                const int timeoutMs =300_000; // 最长等待 5 分钟
                bool exited = p.WaitForExit(timeoutMs);
                if (!exited)
                {
                    Log(I18n.Format("BackupService_Log_SystemError", I18n.Format("BackupService_Log_SevenZipTimeout", timeoutMs / 1000)), LogLevel.Error);
                    try
                    {
                        p.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }
                    return false;
                }

                p.WaitForExit();
                if (p.ExitCode != 0 && !string.IsNullOrWhiteSpace(lastErrorLine))
                {
                    Log($"[7z Exit={p.ExitCode}] {lastErrorLine}", LogLevel.Error);
                }

                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Log(I18n.Format("BackupService_Log_SystemError", ex.Message), LogLevel.Error);
                return false;
            }
        }

        private static async Task<BackupMetadataStoreService.BackupMetadataLoadResult> LoadBackupMetadataAsync(
            string metaDir,
            IEnumerable<string>? archiveFileNames = null)
        {
            if (string.IsNullOrWhiteSpace(metaDir))
            {
                return new BackupMetadataStoreService.BackupMetadataLoadResult();
            }

            return await BackupMetadataStoreService.LoadAsync(metaDir, archiveFileNames).ConfigureAwait(false);
        }

        private static BackupMetadata? LoadBackupMetadata(string metadataPath)
        {
            if (string.IsNullOrWhiteSpace(metadataPath))
            {
                return null;
            }

            string? metaDir = Path.GetDirectoryName(metadataPath);
            if (string.IsNullOrWhiteSpace(metaDir))
            {
                return null;
            }

            var loadResult = BackupMetadataStoreService.LoadAsync(metaDir).GetAwaiter().GetResult();
            return ConvertToAggregateMetadata(loadResult);
        }

        private static BackupMetadata? ConvertToAggregateMetadata(BackupMetadataStoreService.BackupMetadataLoadResult loadResult)
        {
            if (loadResult.State == null)
            {
                return null;
            }

            var metadata = new BackupMetadata
            {
                Version = loadResult.State.Version,
                LastBackupTime = loadResult.State.LastBackupTime,
                LastBackupFileName = loadResult.State.LastBackupFileName,
                BasedOnFullBackup = loadResult.State.BasedOnFullBackup,
                FileStates = new Dictionary<string, FileState>(loadResult.State.FileStates, StringComparer.OrdinalIgnoreCase),
                BackupRecords = loadResult.Records.Values
                    .Select(record => new BackupChangeRecord
                    {
                        ArchiveFileName = record.ArchiveFileName,
                        BackupType = record.BackupType,
                        BasedOnFullBackup = record.BasedOnFullBackup,
                        PreviousBackupFileName = record.PreviousBackupFileName,
                        CreatedAtUtc = record.CreatedAtUtc,
                        AddedFiles = record.AddedFiles.ToList(),
                        ModifiedFiles = record.ModifiedFiles.ToList(),
                        DeletedFiles = record.DeletedFiles.ToList(),
                        FullFileList = record.FullFileList.ToList()
                    })
                    .OrderBy(r => r.CreatedAtUtc)
                    .ThenBy(r => r.ArchiveFileName, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };

            return NormalizeBackupMetadata(metadata);
        }

        private static BackupMetadata NormalizeBackupMetadata(BackupMetadata? meta)
        {
            meta ??= new BackupMetadata();
            meta.FileStates = meta.FileStates != null
                ? new Dictionary<string, FileState>(meta.FileStates, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
            meta.BackupRecords ??= new List<BackupChangeRecord>();

            foreach (var record in meta.BackupRecords)
            {
                record.ArchiveFileName ??= string.Empty;
                record.BackupType ??= string.Empty;
                record.BasedOnFullBackup ??= string.Empty;
                record.PreviousBackupFileName ??= string.Empty;
                record.AddedFiles ??= new List<string>();
                record.ModifiedFiles ??= new List<string>();
                record.DeletedFiles ??= new List<string>();
                record.FullFileList ??= new List<string>();
            }

            return meta;
        }

        private static BackupChangeSet CompareFileStates(
            IReadOnlyDictionary<string, FileState> currentStates,
            IReadOnlyDictionary<string, FileState>? previousStates)
        {
            var result = new BackupChangeSet();
            previousStates ??= new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in currentStates)
            {
                if (!previousStates.TryGetValue(kvp.Key, out var oldState))
                {
                    result.AddedFiles.Add(kvp.Key);
                    continue;
                }

                if (kvp.Value.Size != oldState.Size
                    || kvp.Value.LastWriteTimeUtc != oldState.LastWriteTimeUtc)
                {
                    result.ModifiedFiles.Add(kvp.Key);
                }
            }

            foreach (var kvp in previousStates)
            {
                if (!currentStates.ContainsKey(kvp.Key))
                {
                    result.DeletedFiles.Add(kvp.Key);
                }
            }

            result.AddedFiles.Sort(StringComparer.OrdinalIgnoreCase);
            result.ModifiedFiles.Sort(StringComparer.OrdinalIgnoreCase);
            result.DeletedFiles.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        // --- 模式 1: 全量备份 ---
        // 返回 (Success, FileName)
        private static async Task<(bool Success, string? FileName)> DoFullBackupAsync(string source, string destDir, string metaDir, string baseName, BackupConfig config, string comment = "", BackupTask? taskToUpdate = null)
        {
            BackupMetadata? oldMeta = null;
            if (!string.IsNullOrEmpty(metaDir))
            {
                var metadataLoadResult = await LoadBackupMetadataAsync(metaDir).ConfigureAwait(false);
                oldMeta = ConvertToAggregateMetadata(metadataLoadResult);
                if (oldMeta == null && metadataLoadResult.StateLoadFailed)
                {
                    Log(I18n.Format("BackupService_Log_MetadataCorruptedFallbackFull"), LogLevel.Warning);
                }
            }

            var currentStates = ScanDirectory(source, config.Filters);
            var changeSet = CompareFileStates(currentStates, oldMeta?.FileStates);

            if (config.Archive.SkipIfUnchanged && !string.IsNullOrEmpty(metaDir) && oldMeta != null)
            {
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

                if (referencedBackupExists && !changeSet.HasChanges)
                {
                    Log(I18n.Format("BackupService_Log_NoChangesDetected"), LogLevel.Info);
                    return (true, null);
                }
            }

            string fileName = GenerateFileName(baseName, config.Archive.Format, "Full", comment);
            string destFile = Path.Combine(destDir, fileName);

            // 获取加密密码
            if (!TryResolveRequiredPassword(config, out var password, taskToUpdate))
            {
                return (false, null);
            }

            // 1. 直接压缩（带黑名单过滤 + 自定义文件类型排除）
            var fileTypeExclusions = config.Archive.FileTypeHandlingEnabled ? (IReadOnlyList<FileTypeRule>)config.Archive.FileTypeRules : null;
            bool result = await Run7zCommandAsync("a", source, destFile, config.Archive, password, null, config.Filters, fileTypeExclusions, taskToUpdate);

            // 2. 自定义文件类型追加压缩（不同压缩等级）
            if (result && config.Archive.FileTypeHandlingEnabled)
            {
                bool ruleResult = await RunFileTypeRulePassesAsync(source, destFile, config.Archive, null, config.Filters, password);
                if (!ruleResult)
                {
                    Log(I18n.Format("BackupService_Log_FileTypeRulePassFailed"), LogLevel.Warning);
                    // 规则追加失败不影响主备份结果，仅记录警告
                }
            }

            // 3. 如果成功，生成新的元数据（为后续可能的增量备份做基准）
            if (result)
            {
                bool metadataSaved = await UpdateMetadataAsync(source, metaDir, fileName, fileName, "Full", oldMeta, currentStates, changeSet, config.Filters);
                if (!metadataSaved)
                {
                    return (false, null);
                }

                return (true, fileName);
            }
            return (false, null);
        }

        // --- 模式 2: 智能增量备份 ---
        // 返回 (Success, FileName)
        private static async Task<(bool Success, string? FileName)> DoSmartBackupAsync(string source, string destDir, string metaDir, string baseName, BackupConfig config, string comment = "", BackupTask? taskToUpdate = null)
        {
            var metadataLoadResult = await LoadBackupMetadataAsync(metaDir).ConfigureAwait(false);
            BackupMetadata? oldMeta = ConvertToAggregateMetadata(metadataLoadResult);

            if (oldMeta == null && metadataLoadResult.StateLoadFailed)
            {
                Log(I18n.Format("BackupService_Log_MetadataCorruptedFallbackFull"), LogLevel.Warning);
            }

            // 如果没有元数据，强制全量
            if (oldMeta == null)
            {
                Log(I18n.Format("BackupService_Log_NoBaselineMetadataFallbackFull"), LogLevel.Info);
                return await DoFullBackupAsync(source, destDir, metaDir, baseName, config, comment, taskToUpdate);
            }

            // 校验元数据引用的备份文件是否仍然存在
            // 如果用户删除了最近的备份文件，增量链已断裂，应强制全量备份
            if (!string.IsNullOrEmpty(oldMeta.LastBackupFileName))
            {
                string referencedBackupPath = Path.Combine(destDir, oldMeta.LastBackupFileName);
                if (!File.Exists(referencedBackupPath))
                {
                    Log(I18n.Format("BackupService_Log_ReferencedBackupMissing", oldMeta.LastBackupFileName), LogLevel.Warning);
                    return await DoFullBackupAsync(source, destDir, metaDir, baseName, config, comment, taskToUpdate);
                }
            }
            if (!string.IsNullOrEmpty(oldMeta.BasedOnFullBackup) && oldMeta.BasedOnFullBackup != oldMeta.LastBackupFileName)
            {
                string baseBackupPath = Path.Combine(destDir, oldMeta.BasedOnFullBackup);
                if (!File.Exists(baseBackupPath))
                {
                    Log(I18n.Format("BackupService_Log_ReferencedBackupMissing", oldMeta.BasedOnFullBackup), LogLevel.Warning);
                    return await DoFullBackupAsync(source, destDir, metaDir, baseName, config, comment, taskToUpdate);
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
                            if (IsFullBackupFile(bkFile, config, baseName))
                            {
                                fullFound = true;
                                break;
                            }
                            if (IsIncrementalBackupFile(bkFile, config, baseName))
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
                    return await DoFullBackupAsync(source, destDir, metaDir, baseName, config, comment, taskToUpdate);
                }
            }

            // 2. 扫描并对比文件（带黑名单过滤）
            Log(I18n.Format("BackupService_Log_AnalyzingDiff"), LogLevel.Info);
            var currentStates = ScanDirectory(source, config.Filters);
            var changeSet = CompareFileStates(currentStates, oldMeta.FileStates);

            if (!changeSet.HasChanges)
            {
                Log(I18n.Format("BackupService_Log_NoChangesDetected"), LogLevel.Info);
                return (true, null);
            }

            var contentChangedFiles = changeSet.AddedFiles
                .Concat(changeSet.ModifiedFiles)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Log(I18n.Format("BackupService_Log_ChangesDetected", contentChangedFiles.Count + changeSet.DeletedFiles.Count), LogLevel.Info);

            // 3. 生成文件列表文件
            // 当启用自定义文件类型处理时，需要将变更文件列表拆分：
            // - 主列表：不匹配任何 FileTypeRule 的文件（使用主压缩等级）
            // - 规则匹配文件：稍后用独立压缩等级追加
            var fileTypeRules = config.Archive.FileTypeRules;
            bool hasFileTypeRules = config.Archive.FileTypeHandlingEnabled
                && fileTypeRules != null
                && fileTypeRules.Count > 0;

            List<string> mainFiles = contentChangedFiles;
            if (hasFileTypeRules && fileTypeRules != null)
            {
                mainFiles = contentChangedFiles.Where(f =>
                    !fileTypeRules.Any(rule =>
                        !string.IsNullOrWhiteSpace(rule.Pattern) && MatchWildcard(f, rule.Pattern.Trim())))
                    .ToList();
            }

            string? listFile = null;
            if (mainFiles.Count > 0)
            {
                listFile = Path.GetTempFileName();
                File.WriteAllLines(listFile, mainFiles);
            }

            string fileName = GenerateFileName(baseName, config.Archive.Format, "Smart", comment);
            string destFile = Path.Combine(destDir, fileName);

            // 4. 执行压缩 (使用 @listfile)
            // 注意：7z 需要工作目录在 source 下，才能正确识别相对路径列表
            var fileTypeExclusions = hasFileTypeRules && fileTypeRules != null ? (IReadOnlyList<FileTypeRule>)fileTypeRules : null;
            if (!TryResolveRequiredPassword(config, out var password, taskToUpdate))
            {
                return (false, null);
            }
            bool deletionOnlyChange = contentChangedFiles.Count == 0 && changeSet.DeletedFiles.Count > 0;
            bool result;

            if (deletionOnlyChange)
            {
                result = await CreateDeletionOnlyArchiveAsync(destFile, config.Archive, password, taskToUpdate);
            }
            else if (!string.IsNullOrWhiteSpace(listFile))
            {
                result = await Run7zCommandAsync("a", source, destFile, config.Archive, password, listFile, config.Filters, fileTypeExclusions, taskToUpdate);
            }
            else
            {
                // 所有变更文件都被自定义规则接管，主压缩阶段跳过，后续规则追加负责创建归档。
                result = true;
            }

            // 4.5 自定义文件类型追加压缩（增量模式下传递变更文件列表用于筛选）
            if (result && hasFileTypeRules && contentChangedFiles.Count > 0)
            {
                bool ruleResult = await RunFileTypeRulePassesAsync(source, destFile, config.Archive, contentChangedFiles, config.Filters, password);
                if (!ruleResult)
                {
                    if (string.IsNullOrWhiteSpace(listFile))
                    {
                        result = false;
                    }
                    else
                    {
                        Log(I18n.Format("BackupService_Log_FileTypeRulePassFailed"), LogLevel.Warning);
                    }
                }
            }

            // 5. 更新元数据
            if (result)
            {
                try { if (!string.IsNullOrWhiteSpace(listFile)) File.Delete(listFile); } catch { }

                if (!File.Exists(destFile))
                {
                    return (false, null);
                }

                // 更新元数据：基准文件保持不变（指向最初的Full），LastBackup指向自己
                bool metadataSaved = await UpdateMetadataAsync(source, metaDir, fileName, oldMeta.BasedOnFullBackup, "Smart", oldMeta, currentStates, changeSet, config.Filters);
                if (!metadataSaved)
                {
                    return (false, null);
                }

                return (true, fileName);
            }
            else
            {
                try { if (!string.IsNullOrWhiteSpace(listFile)) File.Delete(listFile); } catch { }
                return (false, null);
            }
        }

        // --- 模式 3: 覆写备份 ---
        // 返回 (Success, FileName)
        private static async Task<(bool Success, string? FileName)> DoOverwriteBackupAsync(string source, string destDir, string metaDir, string baseName, BackupConfig config, string comment = "", BackupTask? taskToUpdate = null)
        {
            BackupMetadata? oldMeta = null;
            if (!string.IsNullOrEmpty(metaDir))
            {
                var metadataLoadResult = await LoadBackupMetadataAsync(metaDir).ConfigureAwait(false);
                oldMeta = ConvertToAggregateMetadata(metadataLoadResult);
                if (oldMeta == null && metadataLoadResult.StateLoadFailed)
                {
                    Log(I18n.Format("BackupService_Log_MetadataCorruptedFallbackFull"), LogLevel.Warning);
                }
            }

            var currentStates = ScanDirectory(source, config.Filters);
            var changeSet = CompareFileStates(currentStates, oldMeta?.FileStates);

            // 1. 寻找最近的备份文件
            var dirInfo = new DirectoryInfo(destDir);
            var files = dirInfo.GetFiles($"*.{config.Archive.Format}")
                               .OrderByDescending(f => f.LastWriteTime)
                               .ToList();

            if (files.Count == 0)
            {
                Log(I18n.Format("BackupService_Log_NoExistingBackupFallbackFull"), LogLevel.Info);
                return await DoFullBackupAsync(source, destDir, metaDir, baseName, config, comment, taskToUpdate);
            }

            FileInfo targetFile = files[0];
            Log(I18n.Format("BackupService_Log_OverwriteUpdating", targetFile.Name), LogLevel.Info);

            // 2. 执行 update 命令 (u)（带黑名单过滤 + 自定义文件类型排除）
            // 7z u <archive_name> <file_names>
            // u 指令会更新已存在的文件并添加新文件
            var fileTypeExclusions = config.Archive.FileTypeHandlingEnabled ? (IReadOnlyList<FileTypeRule>)config.Archive.FileTypeRules : null;
            if (!TryResolveRequiredPassword(config, out var password, taskToUpdate))
            {
                return (false, null);
            }
            bool result = await Run7zCommandAsync("u", source, targetFile.FullName, config.Archive, password, null, config.Filters, fileTypeExclusions, taskToUpdate);

            // 2.5 自定义文件类型追加压缩
            if (result && config.Archive.FileTypeHandlingEnabled)
            {
                bool ruleResult = await RunFileTypeRulePassesAsync(source, targetFile.FullName, config.Archive, null, config.Filters, password);
                if (!ruleResult)
                {
                    Log(I18n.Format("BackupService_Log_FileTypeRulePassFailed"), LogLevel.Warning);
                }
            }

            string? resultingFileName = null;

            if (result)
            {
                // 3. 重命名文件以更新时间戳 (参考 MineBackup 逻辑)
                // 假设文件名格式包含 [YYYY-MM-DD...]，我们要替换它
                string oldName = targetFile.Name;
                string newTimeStr = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

                // 使用正则表达式精确匹配时间戳部分
                string newName = oldName;
                var timeRegex = new Regex(@"\[\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}\]");
                var match = timeRegex.Match(oldName);

                if (match.Success)
                {
                    newName = oldName.Substring(0, match.Index) + $"[{newTimeStr}]" + oldName.Substring(match.Index + match.Length);
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

                if (!string.IsNullOrWhiteSpace(resultingFileName))
                {
                    bool metadataSaved = await UpdateMetadataAsync(
                        source,
                        metaDir,
                        resultingFileName,
                        resultingFileName,
                        "Overwrite",
                        oldMeta,
                        currentStates,
                        changeSet,
                        config.Filters);
                    if (!metadataSaved)
                    {
                        return (false, null);
                    }
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
            string? backupFilePath = HistoryService.GetBackupFilePath(config, folder, historyItem);
            string resolvedFolderName = string.IsNullOrWhiteSpace(historyItem.FolderName)
                ? folder.DisplayName
                : historyItem.FolderName;
            string targetDir = folder.Path;
            var archiveSettings = config.Archive;
            bool safeRestoreEnabled = archiveSettings?.SafeRestoreEnabled ?? true;
            bool verifyArchiveBeforeRestore = archiveSettings?.VerifyArchiveBeforeRestore ?? true;
            // 历史数据可能是旧格式，这里同时看 BackupType 和文件名前缀做兼容判断。
            bool targetIsIncremental = IsIncrementalBackupType(historyItem.BackupType)
                || InferBackupTypeFromFileName(historyItem.FileName).Equals("Smart", StringComparison.OrdinalIgnoreCase);

            string? safeRestoreTempDir = null;
            bool safeRestoreWorkspacePrepared = false;
            bool useCompatibilityReverseRestore = false;
            bool restoreFailed = false;
            bool restoreStarted = false;
            bool effectiveCleanRestore = mode == RestoreMode.Clean;
            SmartRestorePlan? smartRestorePlan = null;
            List<FileInfo> restoreChain = new();

            var restoreTask = new BackupTask
            {
                FolderName = folder.DisplayName,
                Status = I18n.Format("BackupService_Task_Restoring"),
                IconGlyph = "\uE777",
                Progress = 0,
                IsIndeterminate = true
            };
            await RunOnUIAsync(() => ActiveTasks.Insert(0, restoreTask));

            async Task FailAsync(string message, string reason)
            {
                await RunOnUIAsync(() =>
                {
                    restoreTask.Status = I18n.Format("BackupService_Task_RestoreFailed");
                    restoreTask.IsCompleted = true;
                    restoreTask.IsIndeterminate = false;
                    restoreTask.IsSuccess = false;
                    restoreTask.ErrorMessage = message;
                });

                if (restoreStarted)
                {
                    try
                    {
                        KnotLinkService.BroadcastEvent($"event=restore_finished;status=failure;reason={reason}");
                    }
                    catch
                    {
                    }
                }

                NotificationService.NotifyRestoreCompleted(folder.DisplayName, false, message);
            }

            if (string.IsNullOrWhiteSpace(backupFilePath))
            {
                string message = "Invalid backup path in history record.";
                Log(message, LogLevel.Error);
                await FailAsync(message, "invalid_backup_path");
                return;
            }

            string resolvedBackupFilePath = backupFilePath;

            if (archiveSettings?.BackupBeforeRestore == true)
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
                }
            }

            var (shouldHandleRestore, handlerPlugin) = Services.Plugins.PluginService.CheckPluginWantsToHandleRestore(config);
            if (shouldHandleRestore && handlerPlugin != null)
            {
                await HandlePluginRestoreAsync(config, folder, historyItem, restoreTask, handlerPlugin, configIndex);
                return;
            }

            if (!File.Exists(resolvedBackupFilePath))
            {
                string message = I18n.Format("BackupService_Log_BackupFileNotFound", resolvedBackupFilePath);
                Log(message, LogLevel.Error);
                await FailAsync(message, "no_backup_found");
                return;
            }

            string? sevenZipExe = ResolveSevenZipExecutable();
            if (string.IsNullOrEmpty(sevenZipExe))
            {
                string message = I18n.Format("BackupService_Log_SevenZipNotFound");
                await FailAsync(message, "seven_zip_not_found");
                return;
            }

            var backupDir = new DirectoryInfo(Path.GetDirectoryName(resolvedBackupFilePath)!);
            var targetFile = new FileInfo(resolvedBackupFilePath);
            var chainResult = BuildRestoreChainWithStatus(backupDir, targetFile, historyItem.BackupType, config, resolvedFolderName);

            if (targetIsIncremental && chainResult.Status == RestoreChainBuildStatus.MissingBaseFull)
            {
                // 老用户历史里可能缺少基准 Full，这里给一次人工确认后回退到兼容链路。
                bool proceed = await ConfirmMissingBaseFullFallbackAsync(folder.DisplayName, historyItem.FileName);
                if (!proceed)
                {
                    await RunOnUIAsync(() =>
                    {
                        restoreTask.Status = I18n.GetString("Common_Canceled");
                        restoreTask.IsCompleted = true;
                        restoreTask.IsIndeterminate = false;
                        restoreTask.IsSuccess = false;
                        restoreTask.ErrorMessage = I18n.GetString("Common_Canceled");
                    });
                    return;
                }

                restoreChain = BuildReverseCompatibilityChain(backupDir, targetFile, config, resolvedFolderName);
                useCompatibilityReverseRestore = true;
                // 兼容链路无法精确执行 Clean 语义，这里强制退化到覆盖式还原。
                effectiveCleanRestore = false;

                if (restoreChain.Count == 0)
                {
                    string message = I18n.Format("BackupService_Log_RestoreChainNotFound");
                    Log(message, LogLevel.Error);
                    await FailAsync(message, "reverse_chain_not_found");
                    return;
                }
            }
            else
            {
                restoreChain = chainResult.Chain;
                if (restoreChain.Count == 0)
                {
                    string message = I18n.Format("BackupService_Log_RestoreChainNotFound");
                    Log(message, LogLevel.Error);
                    await FailAsync(message, "restore_chain_not_found");
                    return;
                }
            }

            if (effectiveCleanRestore && targetIsIncremental && !useCompatibilityReverseRestore)
            {
                if (!TryResolveBackupStoragePaths(
                    config.DestinationPath,
                    resolvedFolderName,
                    folder.Path,
                    out _,
                    out _,
                    out var metadataDir))
                {
                    string message = I18n.GetString("BackupService_Log_InvalidRestoreStorageFolderName");
                    Log(message, LogLevel.Error);
                    await FailAsync(message, "invalid_folder_name");
                    return;
                }

                var metadataLoadResult = await LoadBackupMetadataAsync(metadataDir, restoreChain.Select(file => file.Name)).ConfigureAwait(false);
                var metadata = ConvertToAggregateMetadata(metadataLoadResult);

                // 元数据完整时启用精确 Smart Clean，可避免“全链解压+覆盖”带来的额外写入。
                if (metadata != null
                    && !metadataLoadResult.RecordLoadFailed
                    && !metadataLoadResult.HasMissingRequestedRecords
                    && TryBuildSmartRestorePlan(restoreChain, metadata, out var plan))
                {
                    smartRestorePlan = plan;
                    Log($"[Restore] Exact Smart Clean restore enabled for {historyItem.FileName}", LogLevel.Info);
                }
                else
                {
                    Log($"[Restore] Exact Smart Clean restore unavailable for {historyItem.FileName}, falling back to compatibility chain extraction.", LogLevel.Warning);
                }
            }

            Log(I18n.Format("BackupService_Log_RestoreBegin", folder.DisplayName), LogLevel.Info);
            Log(I18n.Format("BackupService_Log_RestoreTargetBackup", historyItem.FileName), LogLevel.Info);
            Log(I18n.Format("BackupService_Log_RestoreTargetPath", targetDir), LogLevel.Info);

            if (!TryResolveRequiredPassword(config, out var restorePassword, restoreTask))
            {
                string message = string.IsNullOrWhiteSpace(restoreTask.ErrorMessage)
                    ? MissingEncryptionPasswordMessage
                    : restoreTask.ErrorMessage!;
                await FailAsync(message, "encryption_password_missing");
                return;
            }
            // 智能还原方案与普通还原方案都共用这一段完整性校验入口。
            var archivesToVerify = smartRestorePlan?.Chain ?? restoreChain;

            if (verifyArchiveBeforeRestore)
            {
                Log(I18n.Format("BackupService_Log_RestoreIntegrityCheckBegin", archivesToVerify.Count), LogLevel.Info);
                bool verifyPassed = await ValidateRestoreChainAsync(archivesToVerify, sevenZipExe, restorePassword, restoreTask);
                if (!verifyPassed)
                {
                    string message = I18n.Format("BackupService_Log_RestoreIntegrityCheckFailedStop");
                    Log(message, LogLevel.Error);
                    await FailAsync(message, "archive_integrity_check_failed");
                    return;
                }

                Log(I18n.Format("BackupService_Log_RestoreIntegrityCheckPassed"), LogLevel.Info);
            }

            List<(string PluginId, Services.Plugins.IFolderRewindPlugin Plugin, object? State)>? pluginRestoreStates = null;
            try
            {
                pluginRestoreStates = Services.Plugins.PluginService.InvokeBeforeRestoreFolder(config, folder, historyItem.FileName);
            }
            catch
            {
            }

            try
            {
                KnotLinkService.BroadcastEvent($"event=restore_started;config={configIndex};world={folder.DisplayName}");
                restoreStarted = true;
            }
            catch
            {
            }

            if (effectiveCleanRestore && safeRestoreEnabled)
            {
                // 先把目标目录挪到临时快照，再在空目录还原；失败时可以整体回滚。
                if (!TryPrepareSafeRestoreWorkspace(targetDir, out safeRestoreTempDir, out var prepareError))
                {
                    string message = I18n.Format("BackupService_Log_RestoreSnapshotPrepareFailed", prepareError ?? "Unknown error");
                    Log(message, LogLevel.Error);
                    try
                    {
                        Services.Plugins.PluginService.InvokeAfterRestoreFolder(config, folder, false, historyItem.FileName, pluginRestoreStates);
                    }
                    catch
                    {
                    }
                    await FailAsync(message, "snapshot_prepare_failed");
                    return;
                }

                safeRestoreWorkspacePrepared = !string.IsNullOrWhiteSpace(safeRestoreTempDir);
                if (safeRestoreWorkspacePrepared)
                {
                    Log(I18n.Format("BackupService_Log_RestoreSnapshotPrepared", safeRestoreTempDir ?? string.Empty), LogLevel.Info);
                }
            }
            else if (!Directory.Exists(targetDir))
            {
                try
                {
                    Directory.CreateDirectory(targetDir);
                }
                catch (Exception ex)
                {
                    string message = I18n.Format("BackupService_Log_RestoreCreateTargetDirFailed", ex.Message);
                    Log(message, LogLevel.Error);
                    try
                    {
                        Services.Plugins.PluginService.InvokeAfterRestoreFolder(config, folder, false, historyItem.FileName, pluginRestoreStates);
                    }
                    catch
                    {
                    }
                    await FailAsync(message, "create_dir_failed");
                    return;
                }
            }

            if (effectiveCleanRestore && !safeRestoreWorkspacePrepared)
            {
                // 未启用安全快照时，只能原地清理后再还原（会保留白名单路径）。
                Log(I18n.Format("BackupService_Log_RestoreCleaningTarget"), LogLevel.Info);
                var restoreWhitelist = config.Filters?.RestoreWhitelist;
                bool hasWhitelist = restoreWhitelist != null && restoreWhitelist.Count > 0;

                try
                {
                    DirectoryInfo di = new DirectoryInfo(targetDir);
                    foreach (var entry in di.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
                    {
                        if (hasWhitelist && restoreWhitelist != null && IsInRestoreWhitelist(entry.FullName, targetDir, restoreWhitelist))
                        {
                            Log(I18n.Format("BackupService_Log_RestoreWhitelistSkip", entry.Name), LogLevel.Info);
                            continue;
                        }

                        try
                        {
                            if (entry is DirectoryInfo dirEntry)
                            {
                                ClearReadonlyAttributes(dirEntry.FullName);
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

            bool restoreSucceeded;
            if (smartRestorePlan != null && effectiveCleanRestore)
            {
                restoreSucceeded = await ApplySmartRestorePlanAsync(smartRestorePlan, targetDir, sevenZipExe, restorePassword, restoreTask);
            }
            else
            {
                restoreSucceeded = await ApplyRestoreChainAsync(restoreChain, targetDir, sevenZipExe, restorePassword, restoreTask);
            }

            if (!restoreSucceeded)
            {
                restoreFailed = true;
                string message = string.IsNullOrWhiteSpace(restoreTask.ErrorMessage)
                    ? I18n.GetString("BackupService_Log_RestoreExtractFailed")
                    : restoreTask.ErrorMessage!;
                Log(message, LogLevel.Error);
            }
            else
            {
                CleanupInternalRestoreMarkers(targetDir);

                if (safeRestoreWorkspacePrepared && !string.IsNullOrWhiteSpace(safeRestoreTempDir))
                {
                    // 还原成功后再提交快照工作区，最后一步才真正删除旧目录。
                    if (!TryCommitSafeRestoreWorkspace(targetDir, safeRestoreTempDir, config.Filters?.RestoreWhitelist, out var commitError))
                    {
                        restoreFailed = true;
                        string message = I18n.Format("BackupService_Log_RestoreCommitFailed", commitError ?? "Unknown error");
                        Log(message, LogLevel.Error);
                        await RunOnUIAsync(() => restoreTask.ErrorMessage = message);
                    }
                }
            }

            if (restoreFailed)
            {
                if (safeRestoreWorkspacePrepared && !string.IsNullOrWhiteSpace(safeRestoreTempDir))
                {
                    // 只要失败就优先尝试整体回滚，尽量回到还原前状态。
                    Log(I18n.Format("BackupService_Log_RestoreRollbackBegin", safeRestoreTempDir), LogLevel.Warning);
                    if (TryRollbackSafeRestoreWorkspace(targetDir, safeRestoreTempDir, out var rollbackError))
                    {
                        Log(I18n.Format("BackupService_Log_RestoreRollbackSuccess"), LogLevel.Info);
                    }
                    else
                    {
                        string message = I18n.Format("BackupService_Log_RestoreRollbackFailed", rollbackError ?? "Unknown error");
                        Log(message, LogLevel.Error);
                        await RunOnUIAsync(() =>
                        {
                            if (string.IsNullOrWhiteSpace(restoreTask.ErrorMessage))
                            {
                                restoreTask.ErrorMessage = message;
                            }
                        });
                    }
                }

                try
                {
                    Services.Plugins.PluginService.InvokeAfterRestoreFolder(config, folder, false, historyItem.FileName, pluginRestoreStates);
                }
                catch
                {
                }

                string failureMessage = string.IsNullOrWhiteSpace(restoreTask.ErrorMessage)
                    ? I18n.GetString("BackupService_Log_RestoreExtractFailed")
                    : restoreTask.ErrorMessage!;
                await FailAsync(failureMessage, "command_failed");
                return;
            }

            try
            {
                Services.Plugins.PluginService.InvokeAfterRestoreFolder(config, folder, true, historyItem.FileName, pluginRestoreStates);
            }
            catch
            {
            }

            await RunOnUIAsync(() =>
            {
                restoreTask.Status = I18n.Format("BackupService_Task_RestoreCompleted");
                restoreTask.Progress = 100;
                restoreTask.IsCompleted = true;
                restoreTask.IsIndeterminate = false;
                restoreTask.IsSuccess = true;
            });

            Log(I18n.Format("BackupService_Log_RestoreCompleted"), LogLevel.Info);
            NotificationService.NotifyRestoreCompleted(folder.DisplayName, true, I18n.GetString("BackupService_Task_RestoreCompleted"));

            try
            {
                KnotLinkService.BroadcastEvent($"event=restore_success;config={configIndex};world={folder.DisplayName};backup={historyItem.FileName}");
            }
            catch
            {
            }
        }

        private static async Task<bool> ValidateRestoreChainAsync(List<FileInfo> chain, string sevenZipExe, string? password, BackupTask? restoreTask)
        {
            if (chain == null || chain.Count == 0) return false;

            // 逐包做完整性检测，提前挡住损坏归档，避免真正解压时把目标目录弄成半成品。
            for (int i = 0; i < chain.Count; i++)
            {
                var file = chain[i];
                if (restoreTask != null)
                {
                    int fileIndex = i;
                    await RunOnUIAsync(() => restoreTask.Status = I18n.Format("BackupService_Task_VerifyingRestore_N", fileIndex + 1, chain.Count));
                }

                string testArgs = $"t \"{file.FullName}\" -bsp1";
                if (!string.IsNullOrWhiteSpace(password))
                {
                    testArgs = $"t \"{file.FullName}\" -bsp1 -p\"{password}\"";
                }

                string safeTestArgs = string.IsNullOrWhiteSpace(password) ? testArgs : testArgs.Replace(password, "***");
                bool ok = await RunSevenZipProcessAsync(sevenZipExe, testArgs, file.DirectoryName, safeTestArgs);
                if (!ok)
                {
                    Log(I18n.Format("BackupService_Log_RestoreIntegrityArchiveCheckFailed", file.Name), LogLevel.Error);
                    return false;
                }
            }

            if (restoreTask != null)
            {
                await RunOnUIAsync(() => restoreTask.Status = I18n.Format("BackupService_Task_Restoring"));
            }

            return true;
        }

        private static bool TryBuildSmartRestorePlan(IReadOnlyList<FileInfo> chain, BackupMetadata metadata, out SmartRestorePlan? plan)
        {
            plan = null;
            if (chain == null || chain.Count == 0)
            {
                return false;
            }

            // 通过“最终文件 -> 最近归档拥有者”的映射，生成最小提取集合。
            var normalized = NormalizeBackupMetadata(metadata);
            var recordMap = normalized.BackupRecords
                .Where(r => !string.IsNullOrWhiteSpace(r.ArchiveFileName))
                .GroupBy(r => r.ArchiveFileName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(r => r.CreatedAtUtc).First(),
                    StringComparer.OrdinalIgnoreCase);

            if (!recordMap.TryGetValue(chain[0].Name, out var baseRecord)
                || baseRecord.FullFileList == null
                || baseRecord.FullFileList.Count == 0)
            {
                return false;
            }

            var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in baseRecord.FullFileList.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                owners[file] = chain[0].Name;
            }

            for (int i = 1; i < chain.Count; i++)
            {
                if (!recordMap.TryGetValue(chain[i].Name, out var record))
                {
                    return false;
                }

                foreach (var deleted in record.DeletedFiles.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    owners.Remove(deleted);
                }

                foreach (var added in record.AddedFiles.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    owners[added] = record.ArchiveFileName;
                }

                foreach (var modified in record.ModifiedFiles.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    owners[modified] = record.ArchiveFileName;
                }

                if (record.FullFileList == null || record.FullFileList.Count == 0)
                {
                    return false;
                }

                var expected = new HashSet<string>(record.FullFileList.Where(f => !string.IsNullOrWhiteSpace(f)), StringComparer.OrdinalIgnoreCase);
                if (owners.Count != expected.Count || owners.Keys.Any(path => !expected.Contains(path)))
                {
                    return false;
                }
            }

            var archiveLookup = chain.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            var archiveIndex = chain
                .Select((file, index) => new { file.Name, Index = index })
                .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

            var groups = owners
                .GroupBy(kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)
                .Where(g => archiveLookup.ContainsKey(g.Key))
                .Select(g => new SmartRestoreArchiveGroup
                {
                    Archive = archiveLookup[g.Key],
                    Files = g.Select(x => x.Key).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
                })
                .OrderBy(g => archiveIndex[g.Archive.Name])
                .ToList();

            plan = new SmartRestorePlan
            {
                Chain = chain.ToList(),
                ArchiveGroups = groups
            };
            return true;
        }

        private static async Task<bool> ApplyRestoreChainAsync(
            IReadOnlyList<FileInfo> chain,
            string targetDir,
            string sevenZipExe,
            string? password,
            BackupTask? restoreTask)
        {
            if (chain == null || chain.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < chain.Count; i++)
            {
                var file = chain[i];
                double segmentBase = (double)i / chain.Count * 100;
                double segmentRange = 100.0 / chain.Count;

                if (restoreTask != null && chain.Count > 1)
                {
                    int fileIndex = i;
                    await RunOnUIAsync(() => restoreTask.Status = I18n.Format("BackupService_Task_Restoring_N", fileIndex + 1, chain.Count));
                }

                Log(I18n.Format("BackupService_Log_RestoreApplyingArchive", file.Name), LogLevel.Info);
                string extractArgs = BuildRestoreExtractArguments(file.FullName, targetDir, password);
                string safeExtractArgs = string.IsNullOrWhiteSpace(password) ? extractArgs : extractArgs.Replace(password, "***");
                bool ok = await RunSevenZipProcessAsync(
                    sevenZipExe,
                    extractArgs,
                    file.DirectoryName,
                    safeExtractArgs,
                    restoreTask,
                    progressBase: segmentBase,
                    progressRange: segmentRange);

                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        private static async Task<bool> ApplySmartRestorePlanAsync(
            SmartRestorePlan plan,
            string targetDir,
            string sevenZipExe,
            string? password,
            BackupTask? restoreTask)
        {
            var groups = plan.ArchiveGroups.Where(g => g.Files.Count > 0).ToList();
            if (groups.Count == 0)
            {
                if (restoreTask != null)
                {
                    await RunOnUIAsync(() =>
                    {
                        restoreTask.IsIndeterminate = false;
                        restoreTask.Progress = 100;
                    });
                }
                return true;
            }

            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                double segmentBase = (double)i / groups.Count * 100;
                double segmentRange = 100.0 / groups.Count;
                string listFile = Path.GetTempFileName();

                try
                {
                    File.WriteAllLines(listFile, group.Files);
                    if (restoreTask != null && groups.Count > 1)
                    {
                        int fileIndex = i;
                        await RunOnUIAsync(() => restoreTask.Status = I18n.Format("BackupService_Task_Restoring_N", fileIndex + 1, groups.Count));
                    }

                    Log(I18n.Format("BackupService_Log_RestoreApplyingArchive", group.Archive.Name), LogLevel.Info);
                    string extractArgs = BuildRestoreExtractArguments(group.Archive.FullName, targetDir, password, listFile);
                    string safeExtractArgs = string.IsNullOrWhiteSpace(password) ? extractArgs : extractArgs.Replace(password, "***");
                    bool ok = await RunSevenZipProcessAsync(
                        sevenZipExe,
                        extractArgs,
                        group.Archive.DirectoryName,
                        safeExtractArgs,
                        restoreTask,
                        progressBase: segmentBase,
                        progressRange: segmentRange);

                    if (!ok)
                    {
                        return false;
                    }
                }
                finally
                {
                    try { File.Delete(listFile); } catch { }
                }
            }

            return true;
        }

        private static string BuildRestoreExtractArguments(string archivePath, string targetDir, string? password, string? listFile = null)
        {
            var sb = new StringBuilder();
            sb.Append($"x \"{archivePath}\"");
            if (!string.IsNullOrWhiteSpace(listFile))
            {
                sb.Append($" @\"{listFile}\"");
            }
            sb.Append($" -o\"{targetDir}\" -y -bsp1");
            if (!string.IsNullOrWhiteSpace(password))
            {
                sb.Append($" -p\"{password}\"");
            }
            return sb.ToString();
        }

        private static async Task<bool> ConfirmMissingBaseFullFallbackAsync(string folderDisplayName, string backupFileName)
        {
            var xamlRoot = MainWindowService.GetXamlRoot();
            if (xamlRoot == null)
            {
                return false;
            }

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("BackupService_RestoreMissingBaseFull_Title"),
                Content = I18n.Format("BackupService_RestoreMissingBaseFull_Content", backupFileName),
                PrimaryButtonText = I18n.GetString("BackupService_RestoreMissingBaseFull_Primary"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);

            var result = await RunOnUIAsync(async () => await dialog.ShowAsync());
            return result == ContentDialogResult.Primary;
        }

        private static bool TryPrepareSafeRestoreWorkspace(string targetDir, out string? tempDir, out string? errorMessage)
        {
            tempDir = null;
            errorMessage = null;

            try
            {
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                    return true;
                }

                // 先搬走旧目录，确保还原在“干净目录”执行，失败可直接回滚。
                tempDir = CreateSafeRestoreTempDirectoryPath(targetDir);
                Directory.Move(targetDir, tempDir);
                Directory.CreateDirectory(targetDir);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static bool TryCommitSafeRestoreWorkspace(string targetDir, string tempDir, IEnumerable<string>? whitelist, out string? errorMessage)
        {
            errorMessage = null;

            try
            {
                // 提交阶段要先补回白名单内容，再删除旧快照目录。
                CleanupInternalRestoreMarkers(targetDir);
                CopyRestoreWhitelistEntries(tempDir, targetDir, whitelist, targetDir);

                if (Directory.Exists(tempDir))
                {
                    ClearReadonlyAttributes(tempDir);
                    Directory.Delete(tempDir, true);
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static bool TryRollbackSafeRestoreWorkspace(string targetDir, string tempDir, out string? errorMessage)
        {
            errorMessage = null;

            try
            {
                // 回滚采用目录级替换，避免逐文件恢复造成新旧状态混杂。
                if (Directory.Exists(targetDir))
                {
                    ClearReadonlyAttributes(targetDir);
                    Directory.Delete(targetDir, true);
                }

                if (!Directory.Exists(tempDir))
                {
                    errorMessage = "Snapshot directory is missing.";
                    return false;
                }

                Directory.Move(tempDir, targetDir);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static string CreateSafeRestoreTempDirectoryPath(string targetDir)
        {
            string normalizedTarget = targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? parent = Path.GetDirectoryName(normalizedTarget);
            if (string.IsNullOrWhiteSpace(parent))
            {
                throw new InvalidOperationException("Restore target has no parent directory.");
            }

            string name = Path.GetFileName(normalizedTarget);
            string basePath = Path.Combine(parent, name + "-Temp");
            string candidate = basePath;
            int suffix = 1;

            while (Directory.Exists(candidate) || File.Exists(candidate))
            {
                candidate = basePath + "-" + suffix.ToString();
                suffix++;
            }

            return candidate;
        }

        private static List<FileInfo> BuildReverseCompatibilityChain(DirectoryInfo backupDir, FileInfo targetFile, BackupConfig? config = null, string? folderName = null)
        {
            if (!backupDir.Exists)
            {
                return new List<FileInfo>();
            }

            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive
            };

            var candidates = backupDir
                .EnumerateFiles("*", enumOptions)
                .Where(f => string.Equals(f.Extension, targetFile.Extension, StringComparison.OrdinalIgnoreCase))
                .Where(f => f.LastWriteTimeUtc >= targetFile.LastWriteTimeUtc
                    || string.Equals(f.FullName, targetFile.FullName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenByDescending(f => f.Name)
                .ToList();

            var chain = new List<FileInfo>();
            foreach (var candidate in candidates)
            {
                chain.Add(candidate);
                if (string.Equals(candidate.FullName, targetFile.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            if (!chain.Any(f => string.Equals(f.FullName, targetFile.FullName, StringComparison.OrdinalIgnoreCase)))
            {
                chain.Add(targetFile);
            }

            return chain
                .GroupBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private static void CleanupInternalRestoreMarkers(string targetDir)
        {
            try
            {
                string internalDir = Path.Combine(targetDir, InternalRestoreMarkerDirectoryName);
                if (Directory.Exists(internalDir))
                {
                    ClearReadonlyAttributes(internalDir);
                    Directory.Delete(internalDir, true);
                }
            }
            catch
            {
            }
        }

        private static void CopyRestoreWhitelistEntries(string sourceDir, string targetDir, IEnumerable<string>? whitelist, string? whitelistRootDir = null)
        {
            if (string.IsNullOrWhiteSpace(sourceDir)
                || string.IsNullOrWhiteSpace(targetDir)
                || whitelist == null)
            {
                return;
            }

            var rules = whitelist.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
            if (rules.Count == 0 || !Directory.Exists(sourceDir))
            {
                return;
            }

            string effectiveWhitelistRootDir = string.IsNullOrWhiteSpace(whitelistRootDir) ? sourceDir : whitelistRootDir;

            foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories).OrderBy(d => d.Length))
            {
                if (!IsPathOrAncestorInRestoreWhitelist(dir, sourceDir, effectiveWhitelistRootDir, rules))
                {
                    continue;
                }

                string relPath = Path.GetRelativePath(sourceDir, dir);
                Directory.CreateDirectory(Path.Combine(targetDir, relPath));
            }

            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                if (!IsPathOrAncestorInRestoreWhitelist(file, sourceDir, effectiveWhitelistRootDir, rules))
                {
                    continue;
                }

                string relPath = Path.GetRelativePath(sourceDir, file);
                string destFile = Path.Combine(targetDir, relPath);
                string? destParent = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrWhiteSpace(destParent))
                {
                    Directory.CreateDirectory(destParent);
                }
                File.Copy(file, destFile, true);
            }
        }

        private static bool IsPathOrAncestorInRestoreWhitelist(string entryPath, string rootDir, string whitelistRootDir, IReadOnlyCollection<string> whitelist)
        {
            if (IsInRestoreWhitelist(entryPath, rootDir, whitelist, whitelistRootDir))
            {
                return true;
            }

            string rootFullPath = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? current = Directory.Exists(entryPath) ? entryPath : Path.GetDirectoryName(entryPath);

            while (!string.IsNullOrWhiteSpace(current))
            {
                string currentFullPath = Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(currentFullPath, rootFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (IsInRestoreWhitelist(currentFullPath, rootDir, whitelist, whitelistRootDir))
                {
                    return true;
                }

                current = Path.GetDirectoryName(currentFullPath);
            }

            return false;
        }

        private static void ClearReadonlyAttributes(string dir)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var attrs = File.GetAttributes(file);
                        if ((attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// 检查文件/文件夹是否在还原白名单中（参考 MineBackup is_blacklisted 思路，复用名称匹配）
        /// 支持精确文件名匹配和路径边界匹配
        /// </summary>
        private static bool IsInRestoreWhitelist(string entryPath, string rootDir, IEnumerable<string> whitelist, string? whitelistRootDir = null)
        {
            if (whitelist == null) return false;

            string comparisonRootDir = string.IsNullOrWhiteSpace(whitelistRootDir) ? rootDir : whitelistRootDir;
            string comparisonEntryPath = GetRestoreWhitelistComparisonPath(entryPath, rootDir, comparisonRootDir);

            var entryName = Path.GetFileName(comparisonEntryPath);

            string relativePathLower = string.Empty;
            try
            {
                var relativePath = Path.GetRelativePath(comparisonRootDir, comparisonEntryPath);
                if (!relativePath.StartsWith("..", StringComparison.Ordinal))
                {
                    relativePathLower = relativePath.ToLowerInvariant();
                }
            }
            catch (Exception ex)
            {
                Log($"[Filter][Debug] Failed to build relative path for restore whitelist matching: {ex.Message}", LogLevel.Debug);
            }

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

                // 路径边界匹配（避免子串误伤）
                if (MatchesPathBoundary(comparisonEntryPath, relativePathLower, rule))
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
                    catch (Exception ex)
                    {
                        Log($"[Filter][Debug] Invalid wildcard restore whitelist rule '{rule}': {ex.Message}", LogLevel.Debug);
                    }
                }
            }

            return false;
        }

        private static string GetRestoreWhitelistComparisonPath(string entryPath, string physicalRootDir, string comparisonRootDir)
        {
            string entryFullPath = Path.GetFullPath(entryPath);
            string physicalRootFullPath = Path.GetFullPath(physicalRootDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            string relativePath;
            try
            {
                relativePath = Path.GetRelativePath(physicalRootFullPath, entryFullPath);
            }
            catch (Exception ex)
            {
                Log($"[Filter][Debug] Failed to compute restore whitelist comparison path: {ex.Message}", LogLevel.Debug);
                return entryFullPath;
            }

            if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
            {
                return entryFullPath;
            }

            return Path.GetFullPath(Path.Combine(comparisonRootDir, relativePath));
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
            await RestoreBackupAsync(config, folder, backupFileName, RestoreMode.Overwrite);
        }

        public static async Task RestoreBackupAsync(BackupConfig config, ManagedFolder folder, string backupFileName, RestoreMode mode)
        {
            // 构造一个临时的 HistoryItem
            string backupType = HistoryService.GetBackupTypeForFile(config.Id, folder.DisplayName, backupFileName)
                ?? InferBackupTypeFromFileName(backupFileName);

            var historyItem = new HistoryItem
            {
                FileName = backupFileName,
                BackupType = backupType
            };

            await RestoreBackupAsync(config, folder, historyItem, mode);
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

            if (result.Count == 0 && filters?.Blacklist != null && filters.Blacklist.Count > 0)
            {
                try
                {
                    bool hasAnyFile = dirInfo.EnumerateFiles("*", enumOptions).Any();
                    if (hasAnyFile)
                    {
                        Log($"[Filter][Warning] File state scan returned 0 items while source has files. Source={path}. Check blacklist rules for over-broad matches.", LogLevel.Warning);
                    }
                }
                catch (Exception ex)
                {
                    Log($"[Filter][Debug] Failed to probe source directory after filtering: {ex.Message}", LogLevel.Debug);
                }
            }

            return result;
        }

        private static async Task<bool> CreateDeletionOnlyArchiveAsync(string archivePath, ArchiveSettings settings, string? password, BackupTask? taskToUpdate)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FolderRewind_DeleteOnly_" + Guid.NewGuid().ToString("N"));
            try
            {
                string internalDir = Path.Combine(tempDir, InternalRestoreMarkerDirectoryName);
                Directory.CreateDirectory(internalDir);
                await File.WriteAllTextAsync(
                    Path.Combine(internalDir, InternalRestoreMarkerFileName),
                    DateTime.UtcNow.ToString("O"));

                return await Run7zCommandAsync("a", tempDir, archivePath, settings, password, null, null, null, taskToUpdate);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        ClearReadonlyAttributes(tempDir);
                        Directory.Delete(tempDir, true);
                    }
                }
                catch
                {
                }
            }
        }

        private static async Task<bool> UpdateMetadataAsync(
            string sourceDir,
            string metaDir,
            string currentBackupFile,
            string baseBackupFile,
            string backupType,
            BackupMetadata? previousMetadata,
            Dictionary<string, FileState>? states = null,
            BackupChangeSet? changeSet = null,
            FilterSettings? filters = null)
        {
            if (string.IsNullOrWhiteSpace(metaDir))
            {
                return true;
            }

            states ??= ScanDirectory(sourceDir, filters);
            previousMetadata = NormalizeBackupMetadata(previousMetadata);
            changeSet ??= CompareFileStates(states, previousMetadata.FileStates);
            string previousLastBackupFileName = previousMetadata.LastBackupFileName ?? string.Empty;

            var state = new BackupMetadataState
            {
                Version = "3.0",
                LastBackupTime = DateTime.Now,
                LastBackupFileName = currentBackupFile,
                BasedOnFullBackup = string.IsNullOrWhiteSpace(baseBackupFile) ? currentBackupFile : baseBackupFile,
                FileStates = new Dictionary<string, FileState>(states, StringComparer.OrdinalIgnoreCase)
            };

            var record = new BackupChangeRecord
            {
                ArchiveFileName = currentBackupFile,
                BackupType = backupType,
                BasedOnFullBackup = state.BasedOnFullBackup,
                PreviousBackupFileName = previousLastBackupFileName,
                CreatedAtUtc = DateTime.UtcNow,
                AddedFiles = changeSet.AddedFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                ModifiedFiles = changeSet.ModifiedFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                DeletedFiles = changeSet.DeletedFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                FullFileList = states.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
            };

            bool saved = await BackupMetadataStoreService.SaveAsync(metaDir, state, record).ConfigureAwait(false);
            if (!saved)
            {
                Log(I18n.GetString("BackupMetadataStore_Log_WriteFailedSimple"), LogLevel.Error);
            }

            return saved;
        }

        private static void SynchronizeMetadataAfterArchiveDeletion(
            BackupConfig config,
            string folderName,
            string deletedFileName,
            string? renamedOldFileName,
            string? renamedNewFileName,
            string? renamedBackupType)
        {
            if (config == null
                || string.IsNullOrWhiteSpace(config.DestinationPath)
                || string.IsNullOrWhiteSpace(folderName)
                || string.IsNullOrWhiteSpace(deletedFileName))
            {
                return;
            }

            if (!TryResolveBackupStoragePaths(
                config.DestinationPath,
                folderName,
                fallbackPath: null,
                out _,
                out _,
                out var metadataDir))
            {
                return;
            }

            if (!BackupMetadataStoreService.SynchronizeAfterArchiveDeletion(
                metadataDir,
                deletedFileName,
                renamedOldFileName,
                renamedNewFileName,
                renamedBackupType))
            {
                Log(I18n.GetString("BackupMetadataStore_Log_WriteFailedSimple"), LogLevel.Warning);
            }
        }

        private static void RebaseSuccessorBackupRecord(
            BackupChangeRecord? previousRecord,
            BackupChangeRecord deletedRecord,
            BackupChangeRecord successorRecord,
            string? renamedNewFileName,
            string? renamedBackupType)
        {
            successorRecord.ArchiveFileName = string.IsNullOrWhiteSpace(renamedNewFileName)
                ? successorRecord.ArchiveFileName
                : renamedNewFileName;
            successorRecord.BackupType = string.IsNullOrWhiteSpace(renamedBackupType)
                ? successorRecord.BackupType
                : renamedBackupType;

            var finalSet = new HashSet<string>(
                (successorRecord.FullFileList ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)),
                StringComparer.OrdinalIgnoreCase);

            if (string.Equals(successorRecord.BackupType, "Full", StringComparison.OrdinalIgnoreCase) || previousRecord == null)
            {
                successorRecord.BasedOnFullBackup = successorRecord.ArchiveFileName;
                successorRecord.PreviousBackupFileName = string.Empty;
                successorRecord.AddedFiles = finalSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                successorRecord.ModifiedFiles = new List<string>();
                successorRecord.DeletedFiles = new List<string>();
                successorRecord.FullFileList = finalSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                return;
            }

            var previousSet = new HashSet<string>(
                (previousRecord.FullFileList ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)),
                StringComparer.OrdinalIgnoreCase);
            var ownerMap = previousSet.ToDictionary(path => path, _ => string.Empty, StringComparer.OrdinalIgnoreCase);

            foreach (var deleted in (deletedRecord.DeletedFiles ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                ownerMap.Remove(deleted);
            }
            foreach (var added in (deletedRecord.AddedFiles ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                ownerMap[added] = deletedRecord.ArchiveFileName;
            }
            foreach (var modified in (deletedRecord.ModifiedFiles ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                ownerMap[modified] = deletedRecord.ArchiveFileName;
            }

            foreach (var deleted in (successorRecord.DeletedFiles ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                ownerMap.Remove(deleted);
            }
            foreach (var added in (successorRecord.AddedFiles ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                ownerMap[added] = successorRecord.ArchiveFileName;
            }
            foreach (var modified in (successorRecord.ModifiedFiles ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                ownerMap[modified] = successorRecord.ArchiveFileName;
            }

            successorRecord.PreviousBackupFileName = previousRecord.ArchiveFileName;
            successorRecord.BasedOnFullBackup = previousRecord.BasedOnFullBackup;
            successorRecord.AddedFiles = finalSet
                .Where(path => !previousSet.Contains(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            successorRecord.DeletedFiles = previousSet
                .Where(path => !finalSet.Contains(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            successorRecord.ModifiedFiles = finalSet
                .Where(path => previousSet.Contains(path)
                    && ownerMap.TryGetValue(path, out var owner)
                    && !string.IsNullOrWhiteSpace(owner))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            successorRecord.FullFileList = finalSet.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// 获取配置的加密密码（从 EncryptionService 安全存储中检索）。
        /// </summary>
        private static string? ResolvePassword(BackupConfig config)
        {
            if (!config.IsEncrypted) return null;
            return EncryptionService.RetrievePassword(config.Id);
        }

        private static bool TryResolveRequiredPassword(BackupConfig config, out string? password, BackupTask? taskToUpdate = null)
        {
            password = ResolvePassword(config);

            if (!config.IsEncrypted)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(password))
            {
                return true;
            }

            Log(MissingEncryptionPasswordMessage, LogLevel.Error);
            if (taskToUpdate != null)
            {
                UiDispatcherService.Enqueue(() =>
                {
                    if (string.IsNullOrWhiteSpace(taskToUpdate.ErrorMessage))
                    {
                        taskToUpdate.ErrorMessage = MissingEncryptionPasswordMessage;
                    }
                });
            }

            return false;
        }

        // --- 核心：7z 进程调用 ---
        private static async Task<bool> Run7zCommandAsync(string commandMode, string sourceDir, string archivePath, ArchiveSettings settings, string? password = null, string? listFile = null, FilterSettings? filters = null, IReadOnlyList<FileTypeRule>? fileTypeExclusions = null, BackupTask? taskToUpdate = null)
        {
            string? sevenZipExe = ResolveSevenZipExecutable();
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

            int cpuThreads = NormalizeCpuThreadCount(settings.CpuThreads);
            if (cpuThreads > 0)
            {
                sb.Append($" -mmt{cpuThreads}");
            }
            else
            {
                sb.Append(" -mmt"); // 默认自动线程数
            }

            if (!string.IsNullOrWhiteSpace(password))
            {
                sb.Append($" -p\"{password}\" -mhe=on");
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
            string safeArgs = string.IsNullOrWhiteSpace(password) ? args : args.Replace(password, "***");

            return await RunSevenZipProcessAsync(sevenZipExe, args, sourceDir, safeArgs, taskToUpdate, runAtLowPriority: settings.RunCompressionAtLowPriority);
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
        /// <param name="password">加密密码（可选）</param>
        /// <returns>所有追加操作是否全部成功</returns>
        private static async Task<bool> RunFileTypeRulePassesAsync(
            string sourceDir,
            string archivePath,
            ArchiveSettings settings,
            IReadOnlyList<string>? changedFileList = null,
            FilterSettings? filters = null,
            string? password = null)
        {
            if (!settings.FileTypeHandlingEnabled || settings.FileTypeRules == null || settings.FileTypeRules.Count == 0)
                return true;

            string? sevenZipExe = ResolveSevenZipExecutable();
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
                        int cpuThreads = NormalizeCpuThreadCount(settings.CpuThreads);
                        if (cpuThreads > 0) sb.Append($" -mmt{cpuThreads}"); else sb.Append(" -mmt");
                        if (!string.IsNullOrWhiteSpace(password)) sb.Append($" -p\"{password}\" -mhe=on");
                        sb.Append(" -bsp1");

                        string args = sb.ToString();
                        string safeArgs = string.IsNullOrWhiteSpace(password) ? args : args.Replace(password, "***");

                        bool ok = await RunSevenZipProcessAsync(sevenZipExe, args, sourceDir, safeArgs, runAtLowPriority: settings.RunCompressionAtLowPriority);
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
                        int cpuThreads = NormalizeCpuThreadCount(settings.CpuThreads);
                        if (cpuThreads > 0) sb.Append($" -mmt{cpuThreads}"); else sb.Append(" -mmt");
                        if (!string.IsNullOrWhiteSpace(password)) sb.Append($" -p\"{password}\" -mhe=on");
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
                        string safeArgs = string.IsNullOrWhiteSpace(password) ? args : args.Replace(password, "***");

                        bool ok = await RunSevenZipProcessAsync(sevenZipExe, args, sourceDir, safeArgs, runAtLowPriority: settings.RunCompressionAtLowPriority);
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

        private static string InferBackupTypeFromFileName(string backupFileName)
        {
            if (string.IsNullOrWhiteSpace(backupFileName))
            {
                return "Full";
            }

            if (backupFileName.Contains("[Smart]", StringComparison.OrdinalIgnoreCase))
            {
                return "Smart";
            }

            if (backupFileName.Contains("[Overwrite]", StringComparison.OrdinalIgnoreCase))
            {
                return "Overwrite";
            }

            return "Full";
        }

        private static string ResolveBackupType(FileInfo file, BackupConfig? config = null, string? folderName = null)
        {
            if (file == null)
            {
                return "Full";
            }

            if (config != null && !string.IsNullOrWhiteSpace(folderName))
            {
                var historyType = HistoryService.GetBackupTypeForFile(config.Id, folderName, file.Name);
                if (!string.IsNullOrWhiteSpace(historyType))
                {
                    return historyType;
                }
            }

            return InferBackupTypeFromFileName(file.Name);
        }

        private static bool IsIncrementalBackupType(string? backupType)
        {
            return !string.IsNullOrWhiteSpace(backupType)
                && (backupType.Equals("Incremental", StringComparison.OrdinalIgnoreCase)
                    || backupType.Equals("Smart", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsFullBackupFile(FileInfo file, BackupConfig? config = null, string? folderName = null)
        {
            var backupType = ResolveBackupType(file, config, folderName);
            return backupType.Equals("Full", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsIncrementalBackupFile(FileInfo file, BackupConfig? config = null, string? folderName = null)
        {
            var backupType = ResolveBackupType(file, config, folderName);
            return IsIncrementalBackupType(backupType);
        }

        private static (RestoreChainBuildStatus Status, List<FileInfo> Chain) BuildRestoreChainWithStatus(DirectoryInfo backupDir, FileInfo targetFile, string backupType, BackupConfig? config = null, string? folderName = null)
        {
            var chain = new List<FileInfo>();
            if (!backupDir.Exists)
            {
                return (RestoreChainBuildStatus.NotFound, chain);
            }

            bool isIncremental =
                IsIncrementalBackupType(backupType) ||
                IsIncrementalBackupFile(targetFile, config, folderName);

            if (!isIncremental)
            {
                chain.Add(targetFile);
                return (RestoreChainBuildStatus.Success, chain);
            }

            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive
            };

            // 查找最近的全量备份基准
            var baseFull = backupDir
                .EnumerateFiles("*", enumOptions)
                .Where(f => IsFullBackupFile(f, config, folderName) && f.LastWriteTime <= targetFile.LastWriteTime)
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();

            if (baseFull == null)
            {
                Log(I18n.Format("BackupService_Log_NoBaseFullFoundTryIncrementOnly"), LogLevel.Warning);
                return (RestoreChainBuildStatus.MissingBaseFull, chain);
            }

            chain.Add(baseFull);

            var increments = backupDir
                .EnumerateFiles("*", enumOptions)
                .Where(f => IsIncrementalBackupFile(f, config, folderName)
                            && f.LastWriteTime >= baseFull.LastWriteTime
                            && f.LastWriteTime <= targetFile.LastWriteTime)
                .OrderBy(f => f.LastWriteTime)
                .ThenBy(f => f.Name); // 二级排序确保稳定性

            // 去重是为了兼容“同名文件被重写/历史重复登记”的旧数据。
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

            return (RestoreChainBuildStatus.Success, chain);
        }

        private static List<FileInfo> BuildRestoreChain(DirectoryInfo backupDir, FileInfo targetFile, string backupType, BackupConfig? config = null, string? folderName = null)
        {
            return BuildRestoreChainWithStatus(backupDir, targetFile, backupType, config, folderName).Chain;
        }

        private static int NormalizeCpuThreadCount(int cpuThreads)
        {
            if (cpuThreads <= 0)
            {
                return 0;
            }

            return Math.Clamp(cpuThreads, 1, Math.Max(Environment.ProcessorCount, 1));
        }

        private static void ApplyLowPriorityIfRequested(Process process, bool runAtLowPriority)
        {
            if (!runAtLowPriority)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
            }
            catch
            {
            }
        }

        private static string? ResolveSevenZipExecutable()
        {
            var candidates = new List<string>();
            var configPath = ConfigService.CurrentConfig.GlobalSettings?.SevenZipPath;
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");

            lock (SevenZipResolutionLock)
            {
                if (string.Equals(_cachedSevenZipConfigPath, configPath, StringComparison.Ordinal)
                    && string.Equals(_cachedSevenZipPathEnvironment, pathEnv, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(_cachedSevenZipExecutable)
                    && File.Exists(_cachedSevenZipExecutable))
                {
                    return _cachedSevenZipExecutable;
                }
            }

            void AddCandidate(string? path)
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
                if (!File.Exists(path))
                {
                    continue;
                }

                lock (SevenZipResolutionLock)
                {
                    _cachedSevenZipExecutable = path;
                    _cachedSevenZipConfigPath = configPath;
                    _cachedSevenZipPathEnvironment = pathEnv;
                }

                return path;
            }

            lock (SevenZipResolutionLock)
            {
                _cachedSevenZipExecutable = null;
                _cachedSevenZipConfigPath = configPath;
                _cachedSevenZipPathEnvironment = pathEnv;
            }

            Log(I18n.Format("BackupService_Log_SevenZipNotFound"), LogLevel.Error);
            return null;
        }

        /// <summary>
        /// 匹配 7z 输出中的百分比进度（如 " 42%" 或 "100%"）
        /// </summary>
        private static readonly Regex _progressRegex = new(@"^\s*(\d{1,3})%", RegexOptions.Compiled);

        private static async Task<bool> RunSevenZipProcessAsync(
            string sevenZipExe, string arguments,
            string? workingDirectory = null, string? logArguments = null,
            BackupTask? taskToUpdate = null,
            double progressBase = 0, double progressRange = 100,
            bool runAtLowPriority = false)
        {
            arguments = EnsureSswArgument(arguments);
            logArguments = EnsureSswArgument(logArguments ?? arguments);

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

            Log($"[CMD] {Path.GetFileName(sevenZipExe)} {logArguments}", LogLevel.Debug);

            string? lastErrorLine = null;

            try
            {
                using var p = new Process { StartInfo = pInfo };
                p.OutputDataReceived += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    Log($"[7z] {e.Data}");

                    // 解析 7z 的百分比进度输出（如 " 42%" 或 " 15% 3 + file.txt"）
                    if (taskToUpdate != null)
                    {
                        var match = _progressRegex.Match(e.Data);
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int percent) && percent >= 0 && percent <= 100)
                        {
                            double mapped = progressBase + (double)percent / 100.0 * progressRange;
                            UiDispatcherService.Enqueue(() =>
                            {
                                if (taskToUpdate.IsIndeterminate) taskToUpdate.IsIndeterminate = false;
                                taskToUpdate.Progress = Math.Min(mapped, 100);
                            });
                        }
                    }
                };
                p.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    Log($"[7z Err] {e.Data}", LogLevel.Error);
                    lastErrorLine = e.Data; // 保留最后一条错误信息用于显示
                };

                p.Start();
                ApplyLowPriorityIfRequested(p, runAtLowPriority);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                await p.WaitForExitAsync();

                // 7z 返回非零退出码且有 stderr 输出时，将错误信息写入任务
                if (p.ExitCode != 0 && taskToUpdate != null && !string.IsNullOrWhiteSpace(lastErrorLine))
                {
                    UiDispatcherService.Enqueue(() =>
                    {
                        if (string.IsNullOrEmpty(taskToUpdate.ErrorMessage))
                            taskToUpdate.ErrorMessage = lastErrorLine;
                    });
                }

                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Log(I18n.Format("BackupService_Log_SystemError", ex.Message), LogLevel.Error);
                if (taskToUpdate != null)
                {
                    UiDispatcherService.Enqueue(() =>
                    {
                        if (string.IsNullOrEmpty(taskToUpdate.ErrorMessage))
                            taskToUpdate.ErrorMessage = ex.Message;
                    });
                }
                return false;
            }
        }

        private static string EnsureSswArgument(string? arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
                return "-ssw";

            if (Regex.IsMatch(arguments, @"(?:^|\s)-ssw(?:\s|$)", RegexOptions.IgnoreCase))
                return arguments;

            return arguments + " -ssw";
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

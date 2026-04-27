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
    public static partial class BackupService
    {
        // 还原入口、还原链和安全还原工作区集中在这里，便于后续单独审计恢复路径。

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

            bool shouldAttemptCloudRestoreCompletion =
                ConfigService.CurrentConfig?.GlobalSettings?.AutoDownloadMissingCloudBackupsBeforeRestore == true
                && CloudSyncService.CanUseManualCloudActions(config)
                && (!File.Exists(resolvedBackupFilePath) || targetIsIncremental);

            if (shouldAttemptCloudRestoreCompletion)
            {
                var cloudRestoreResult = await CloudSyncService.EnsureRestoreChainAvailableAsync(config, folder, historyItem);
                if (!cloudRestoreResult.Success)
                {
                    Log($"[Restore] Cloud restore-chain completion skipped or failed: {cloudRestoreResult.Message}", LogLevel.Warning);
                }

                backupFilePath = HistoryService.GetBackupFilePath(config, folder, historyItem);
                if (!string.IsNullOrWhiteSpace(backupFilePath))
                {
                    resolvedBackupFilePath = backupFilePath;
                }
            }

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
    }
}


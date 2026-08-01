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
            bool result = await Run7zCommandAsync("a", source, destFile, config.Archive, password, null, config.Filters, fileTypeExclusions, taskToUpdate, applyAdditionalArguments: true);

            // 2. 自定义文件类型追加压缩（不同压缩等级）
            if (result && config.Archive.FileTypeHandlingEnabled)
            {
                bool ruleResult = await RunFileTypeRulePassesAsync(source, destFile, config.Archive, null, config.Filters, password, taskToUpdate);
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
                var fileTypeMatchers = CompileFileTypeWildcardPatterns(
                    fileTypeRules.Select(rule => rule.Pattern));
                mainFiles = contentChangedFiles.Where(f =>
                    !MatchesAnyFileTypePattern(f, fileTypeMatchers))
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
                result = await Run7zCommandAsync("a", source, destFile, config.Archive, password, listFile, config.Filters, fileTypeExclusions, taskToUpdate, applyAdditionalArguments: true);
            }
            else
            {
                // 所有变更文件都被自定义规则接管，主压缩阶段跳过，后续规则追加负责创建归档。
                result = true;
            }

            // 4.5 自定义文件类型追加压缩（增量模式下传递变更文件列表用于筛选）
            if (result && hasFileTypeRules && contentChangedFiles.Count > 0)
            {
                bool ruleResult = await RunFileTypeRulePassesAsync(source, destFile, config.Archive, contentChangedFiles, config.Filters, password, taskToUpdate);
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
            bool result = await Run7zCommandAsync("u", source, targetFile.FullName, config.Archive, password, null, config.Filters, fileTypeExclusions, taskToUpdate, applyAdditionalArguments: true);

            // 2.5 自定义文件类型追加压缩
            if (result && config.Archive.FileTypeHandlingEnabled)
            {
                bool ruleResult = await RunFileTypeRulePassesAsync(source, targetFile.FullName, config.Archive, null, config.Filters, password, taskToUpdate);
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

    }
}

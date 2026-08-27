using FolderRewind.Models;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
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
        /// <summary>
        /// 模式 1：全量备份。压缩源目录内全部匹配文件，并把当前文件清单写入元数据，
        /// 作为后续增量备份的基准。
        /// </summary>
        /// <remarks>
        /// SkipIfUnchanged 短路需同时满足两个前提：确无变更，且元数据引用的上个归档文件仍然存在
        /// （防止基于已被手动删除的基线判定"无变化"）。FileTypeRules 追加压缩失败只记警告，
        /// 不使全量备份整体失败；元数据写入失败则视为本次备份失败。
        /// </remarks>
        private static async Task<SourceCaptureResult> DoFullBackupAsync(SourceId sourceId, FolderRewind.History.Domain.CaptureScope captureScope, string source, string destDir, string metaDir, string baseName, BackupConfig config, BackupSourceScope selection, string comment = "", BackupTask? taskToUpdate = null)
        {
            BackupMetadataState? oldState = null;
            if (!string.IsNullOrEmpty(metaDir))
            {
                var metadataLoadResult = await BackupMetadataStoreService.LoadStateAsync(metaDir).ConfigureAwait(false);
                oldState = metadataLoadResult.State;
                if (oldState == null && metadataLoadResult.StateLoadFailed)
                {
                    Log(I18n.Format("BackupService_Log_MetadataCorruptedFallbackFull"), LogLevel.Warning);
                }
            }

            var currentStates = ScanDirectory(source, config.Filters, selection: selection);
            if (BackupSourceAvailabilityPolicy.IsUnavailable(selection, currentStates.Count))
            {
                return SourceCaptureResult.Unavailable(sourceId, captureScope);
            }
            var changeSet = CompareFileStates(currentStates, oldState?.FileStates);

            if (config.Archive.SkipIfUnchanged && !string.IsNullOrEmpty(metaDir) && oldState != null)
            {
                bool referencedBackupExists = true;
                if (!string.IsNullOrEmpty(oldState.LastBackupFileName))
                {
                    string referencedBackupPath = Path.Combine(destDir, oldState.LastBackupFileName);
                    if (!File.Exists(referencedBackupPath))
                    {
                        referencedBackupExists = false;
                        Log(I18n.Format("BackupService_Log_ReferencedBackupMissing", oldState.LastBackupFileName), LogLevel.Warning);
                    }
                }

                if (referencedBackupExists && !changeSet.HasChanges)
                {
                    Log(I18n.Format("BackupService_Log_NoChangesDetected"), LogLevel.Info);
                    return SourceCaptureResult.NoChanges(sourceId, captureScope);
                }
            }

            string fileName = GenerateFileName(baseName, config.Archive.Format, "Full", comment);
            string destFile = Path.Combine(destDir, fileName);

            // 获取加密密码
            if (!TryResolveRequiredPassword(config, out var password, taskToUpdate))
            {
                return SourceCaptureResult.Failed(sourceId, captureScope);
            }

            // 1. 直接压缩（带黑名单过滤 + 自定义文件类型排除）
            var fileTypeExclusions = config.Archive.FileTypeHandlingEnabled ? (IReadOnlyList<FileTypeRule>)config.Archive.FileTypeRules : null;
            bool result = await Run7zCommandAsync("a", source, destFile, config.Archive, password, null, config.Filters, fileTypeExclusions, taskToUpdate, applyAdditionalArguments: true, selection: selection);

            // 2. 自定义文件类型追加压缩（不同压缩等级）
            if (result && config.Archive.FileTypeHandlingEnabled)
            {
                bool ruleResult = await RunFileTypeRulePassesAsync(source, destFile, config.Archive, null, config.Filters, password, taskToUpdate, selection);
                if (!ruleResult)
                {
                    Log(I18n.Format("BackupService_Log_FileTypeRulePassFailed"), LogLevel.Warning);
                    // 规则追加失败不影响主备份结果，仅记录警告
                }
            }

            // 3. 如果成功，生成新的元数据（为后续可能的增量备份做基准）
            if (result)
            {
                bool metadataSaved = await UpdateMetadataAsync(source, metaDir, fileName, fileName, "Full", oldState, currentStates, changeSet, config.Filters);
                if (!metadataSaved)
                {
                    return SourceCaptureResult.Failed(sourceId, captureScope);
                }

                return CreateLegacyArchiveCapture(sourceId, captureScope, destDir, fileName, RepresentationKind.CoreFull, config.Archive.Format);
            }
            return SourceCaptureResult.Failed(sourceId, captureScope);
        }

        // --- 模式 2: 智能增量备份 ---
        // 返回归档执行结果，并显式区分无变化、不可用和失败。
        /// <summary>
        /// 模式 2：智能增量备份。与元数据基线对比后仅压缩有变更的文件；
        /// 删除类变更用只含内部标记文件的"仅删除"归档表达。
        /// </summary>
        /// <remarks>
        /// 三种情况强制回退为全量：基线元数据缺失（含损坏）、基线引用的归档文件已被删除
        /// （增量链断裂）、或最近一次 Full 之后的 Smart 数量达到 MaxSmartBackupsPerFull 上限
        /// （截断链条）。变更文件会先按 FileTypeRules 拆分：不匹配规则的进主列表文件，
        /// 匹配的留给追加压缩阶段处理；若变更全部由删除构成，则跳过主压缩直接生成仅删除归档。
        /// </remarks>
        private static async Task<SourceCaptureResult> DoSmartBackupAsync(SourceId sourceId, FolderRewind.History.Domain.CaptureScope captureScope, string source, string destDir, string metaDir, string baseName, BackupConfig config, BackupSourceScope selection, string comment = "", BackupTask? taskToUpdate = null)
        {
            var metadataLoadResult = await BackupMetadataStoreService.LoadStateAsync(metaDir).ConfigureAwait(false);
            BackupMetadataState? oldState = metadataLoadResult.State;

            if (oldState == null && metadataLoadResult.StateLoadFailed)
            {
                Log(I18n.Format("BackupService_Log_MetadataCorruptedFallbackFull"), LogLevel.Warning);
            }

            // 如果没有元数据，强制全量
            if (oldState == null)
            {
                Log(I18n.Format("BackupService_Log_NoBaselineMetadataFallbackFull"), LogLevel.Info);
                return await DoFullBackupAsync(sourceId, captureScope, source, destDir, metaDir, baseName, config, selection, comment, taskToUpdate);
            }

            // 校验元数据引用的备份文件是否仍然存在
            // 如果用户删除了最近的备份文件，增量链已断裂，应强制全量备份
            if (!string.IsNullOrEmpty(oldState.LastBackupFileName))
            {
                string referencedBackupPath = Path.Combine(destDir, oldState.LastBackupFileName);
                if (!File.Exists(referencedBackupPath))
                {
                    Log(I18n.Format("BackupService_Log_ReferencedBackupMissing", oldState.LastBackupFileName), LogLevel.Warning);
                    return await DoFullBackupAsync(sourceId, captureScope, source, destDir, metaDir, baseName, config, selection, comment, taskToUpdate);
                }
            }
            if (!string.IsNullOrEmpty(oldState.BasedOnFullBackup) && oldState.BasedOnFullBackup != oldState.LastBackupFileName)
            {
                string baseBackupPath = Path.Combine(destDir, oldState.BasedOnFullBackup);
                if (!File.Exists(baseBackupPath))
                {
                    Log(I18n.Format("BackupService_Log_ReferencedBackupMissing", oldState.BasedOnFullBackup), LogLevel.Warning);
                    return await DoFullBackupAsync(sourceId, captureScope, source, destDir, metaDir, baseName, config, selection, comment, taskToUpdate);
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
                    return await DoFullBackupAsync(sourceId, captureScope, source, destDir, metaDir, baseName, config, selection, comment, taskToUpdate);
                }
            }

            // 2. 扫描并对比文件（带黑名单过滤）
            Log(I18n.Format("BackupService_Log_AnalyzingDiff"), LogLevel.Info);
            var currentStates = ScanDirectory(source, config.Filters, selection: selection);
            if (BackupSourceAvailabilityPolicy.IsUnavailable(selection, currentStates.Count))
            {
                return SourceCaptureResult.Unavailable(sourceId, captureScope);
            }
            var changeSet = CompareFileStates(currentStates, oldState.FileStates);

            if (!changeSet.HasChanges)
            {
                Log(I18n.Format("BackupService_Log_NoChangesDetected"), LogLevel.Info);
                return SourceCaptureResult.NoChanges(sourceId, captureScope);
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
                return SourceCaptureResult.Failed(sourceId, captureScope);
            }
            bool deletionOnlyChange = contentChangedFiles.Count == 0 && changeSet.DeletedFiles.Count > 0;
            bool result;

            if (deletionOnlyChange)
            {
                result = await CreateDeletionOnlyArchiveAsync(destFile, config.Archive, password, taskToUpdate);
            }
            else if (!string.IsNullOrWhiteSpace(listFile))
            {
                result = await Run7zCommandAsync("a", source, destFile, config.Archive, password, listFile, config.Filters, fileTypeExclusions, taskToUpdate, applyAdditionalArguments: true, selection: selection);
            }
            else
            {
                // 所有变更文件都被自定义规则接管，主压缩阶段跳过，后续规则追加负责创建归档。
                result = true;
            }

            // 4.5 自定义文件类型追加压缩（增量模式下传递变更文件列表用于筛选）
            if (result && hasFileTypeRules && contentChangedFiles.Count > 0)
            {
                bool ruleResult = await RunFileTypeRulePassesAsync(source, destFile, config.Archive, contentChangedFiles, config.Filters, password, taskToUpdate, selection);
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
                    return SourceCaptureResult.Failed(sourceId, captureScope);
                }

                // 更新元数据：基准文件保持不变（指向最初的Full），LastBackup指向自己
                bool metadataSaved = await UpdateMetadataAsync(source, metaDir, fileName, oldState.BasedOnFullBackup, "Smart", oldState, currentStates, changeSet, config.Filters);
                if (!metadataSaved)
                {
                    return SourceCaptureResult.Failed(sourceId, captureScope);
                }

                return CreateLegacyArchiveCapture(sourceId, captureScope, destDir, fileName, RepresentationKind.CoreSmartDelta, config.Archive.Format);
            }
            else
            {
                try { if (!string.IsNullOrWhiteSpace(listFile)) File.Delete(listFile); } catch { }
                return SourceCaptureResult.Failed(sourceId, captureScope);
            }
        }

        // --- 模式 3: 覆写备份 ---
        // 返回归档执行结果，并显式区分无变化、不可用和失败。
        /// <summary>
        /// 模式 3：覆写备份。不生成新文件，而是更新目标目录中最近的归档并重命名刷新时间戳；
        /// 目标目录为空时回退为全量备份。
        /// </summary>
        /// <remarks>
        /// Include 来源必须整体重建：先用 7z a 在同目录构建精确临时归档、成功后原子替换，
        /// 避免旧归档残留已取消选择或已删除的文件；All 来源继续用 7z u 增量更新原文件。
        /// 时间戳重命名失败时保留原文件名，不影响备份内容。
        /// </remarks>
        private static async Task<SourceCaptureResult> DoOverwriteBackupAsync(SourceId sourceId, FolderRewind.History.Domain.CaptureScope captureScope, string source, string destDir, string metaDir, string baseName, BackupConfig config, BackupSourceScope selection, string comment = "", BackupTask? taskToUpdate = null)
        {
            BackupMetadataState? oldState = null;
            if (!string.IsNullOrEmpty(metaDir))
            {
                var metadataLoadResult = await BackupMetadataStoreService.LoadStateAsync(metaDir).ConfigureAwait(false);
                oldState = metadataLoadResult.State;
                if (oldState == null && metadataLoadResult.StateLoadFailed)
                {
                    Log(I18n.Format("BackupService_Log_MetadataCorruptedFallbackFull"), LogLevel.Warning);
                }
            }

            var currentStates = ScanDirectory(source, config.Filters, selection: selection);
            if (BackupSourceAvailabilityPolicy.IsUnavailable(selection, currentStates.Count))
            {
                return SourceCaptureResult.Unavailable(sourceId, captureScope);
            }
            var changeSet = CompareFileStates(currentStates, oldState?.FileStates);

            // 1. 寻找最近的备份文件
            var dirInfo = new DirectoryInfo(destDir);
            var files = dirInfo.GetFiles($"*.{config.Archive.Format}")
                               .OrderByDescending(f => f.LastWriteTime)
                               .ToList();

            if (files.Count == 0)
            {
                Log(I18n.Format("BackupService_Log_NoExistingBackupFallbackFull"), LogLevel.Info);
                return await DoFullBackupAsync(sourceId, captureScope, source, destDir, metaDir, baseName, config, selection, comment, taskToUpdate);
            }

            FileInfo targetFile = files[0];
            Log(I18n.Format("BackupService_Log_OverwriteUpdating", targetFile.Name), LogLevel.Info);

            // Include 来源不能复用旧归档内容，否则已取消选择或已删除的文件仍会残留。
            // 先在同目录构建精确临时归档，成功后再替换；All 来源继续使用原有 7z update 行为。
            var fileTypeExclusions = config.Archive.FileTypeHandlingEnabled ? (IReadOnlyList<FileTypeRule>)config.Archive.FileTypeRules : null;
            if (!TryResolveRequiredPassword(config, out var password, taskToUpdate))
            {
                return SourceCaptureResult.Failed(sourceId, captureScope);
            }
            var isExactSelection = selection.Mode == BackupSourceScopeMode.Include;
            string? replacementArchivePath = isExactSelection
                ? Path.Combine(destDir, $".{Guid.NewGuid():N}.{config.Archive.Format}")
                : null;
            string archiveToUpdate = replacementArchivePath ?? targetFile.FullName;
            bool result = await Run7zCommandAsync(
                isExactSelection ? "a" : "u",
                source,
                archiveToUpdate,
                config.Archive,
                password,
                null,
                config.Filters,
                fileTypeExclusions,
                taskToUpdate,
                applyAdditionalArguments: true,
                selection: selection);

            // 2.5 自定义文件类型追加压缩
            if (result && config.Archive.FileTypeHandlingEnabled)
            {
                bool ruleResult = await RunFileTypeRulePassesAsync(source, archiveToUpdate, config.Archive, null, config.Filters, password, taskToUpdate, selection);
                if (!ruleResult)
                {
                    Log(I18n.Format("BackupService_Log_FileTypeRulePassFailed"), LogLevel.Warning);
                    if (isExactSelection)
                    {
                        result = false;
                    }
                }
            }

            if (result && replacementArchivePath != null)
            {
                try
                {
                    File.Move(replacementArchivePath, targetFile.FullName, overwrite: true);
                    replacementArchivePath = null;
                }
                catch (Exception ex)
                {
                    Log($"[Backup] Failed to replace exact overwrite archive: {ex.Message}", LogLevel.Error);
                    result = false;
                }
            }
            if (replacementArchivePath != null)
            {
                try { File.Delete(replacementArchivePath); } catch { }
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
                        oldState,
                        currentStates,
                        changeSet,
                        config.Filters);
                    if (!metadataSaved)
                    {
                        return SourceCaptureResult.Failed(sourceId, captureScope);
                    }
                }
            }

            return result
                ? CreateLegacyArchiveCapture(sourceId, captureScope, destDir, resultingFileName ?? targetFile.Name, RepresentationKind.CoreRolling, config.Archive.Format)
                : SourceCaptureResult.Failed(sourceId, captureScope);
        }

        /// <summary>
        /// 创建"仅删除"归档：只包含一个内部标记文件（不含任何用户数据），
        /// 恢复阶段据此识别该历史点的变更全部为文件删除。临时目录在 finally 中尽力清理。
        /// </summary>
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

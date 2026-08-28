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
        /// 模式 1：全量备份。压缩源目录内全部匹配文件，并返回可删除重建的 capture baseline candidate，
        /// 仅在 Native History 提交成功后由 runtime 更新本机缓存。
        /// </summary>
        /// <remarks>
        /// SkipIfUnchanged 短路需同时满足两个前提：确无变更，且缓存引用的上个归档文件仍然存在
        /// （防止基于已被手动删除的 payload 判定"无变化"）。FileTypeRules 追加压缩失败只记警告。
        /// </remarks>
        private static async Task<SourceCaptureResult> DoFullBackupAsync(SourceId sourceId, FolderRewind.History.Domain.CaptureScope captureScope, string source, string destDir, SourceCaptureBaseline? baseline, string baseName, BackupConfig config, BackupSourceScope selection, string comment = "", BackupTask? taskToUpdate = null)
        {
            var currentStates = ScanDirectory(source, config.Filters, selection: selection);
            if (BackupSourceAvailabilityPolicy.IsUnavailable(selection, currentStates.Count))
            {
                return SourceCaptureResult.Unavailable(sourceId, captureScope);
            }
            var changeSet = CompareFileStates(currentStates, baseline?.FileStates);

            if (config.Archive.SkipIfUnchanged && baseline is not null)
            {
                bool referencedBackupExists = File.Exists(baseline.PayloadPath);

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

            // 3. 如果成功，生成 Native Representation 与下一次 capture baseline candidate。
            if (result)
            {
                return CreateArchiveCapture(
                    sourceId, captureScope, destDir, fileName, RepresentationKind.CoreFull,
                    config.Archive.Format, currentStates, baseline, dependencies: [], consecutiveSmartCaptures: 0);
            }
            return SourceCaptureResult.Failed(sourceId, captureScope);
        }

        // --- 模式 2: 智能增量备份 ---
        // 返回归档执行结果，并显式区分无变化、不可用和失败。
        /// <summary>
        /// 模式 2：智能增量备份。与本机 capture baseline 对比后仅压缩有变更的文件；
        /// 删除类变更用只含内部标记文件的"仅删除"归档表达。
        /// </summary>
        /// <remarks>
        /// 三种情况强制回退为全量：baseline cache 缺失（含损坏）、缓存引用的归档文件已被删除
        /// （增量链断裂）、或最近一次 Full 之后的 Smart 数量达到 MaxSmartBackupsPerFull 上限
        /// （截断链条）。变更文件会先按 FileTypeRules 拆分：不匹配规则的进主列表文件，
        /// 匹配的留给追加压缩阶段处理；若变更全部由删除构成，则跳过主压缩直接生成仅删除归档。
        /// </remarks>
        private static async Task<SourceCaptureResult> DoSmartBackupAsync(SourceId sourceId, FolderRewind.History.Domain.CaptureScope captureScope, string source, string destDir, SourceCaptureBaseline? baseline, string baseName, BackupConfig config, BackupSourceScope selection, string comment = "", BackupTask? taskToUpdate = null)
        {
            if (baseline is null)
            {
                Log(I18n.Format("BackupService_Log_NoBaselineMetadataFallbackFull"), LogLevel.Info);
                return await DoFullBackupAsync(sourceId, captureScope, source, destDir, baseline: null, baseName, config, selection, comment, taskToUpdate);
            }
            if (!File.Exists(baseline.PayloadPath))
            {
                Log(I18n.Format("BackupService_Log_NoBaselineMetadataFallbackFull"), LogLevel.Info);
                return await DoFullBackupAsync(sourceId, captureScope, source, destDir, baseline, baseName, config, selection, comment, taskToUpdate);
            }

            // 2. 智能备份链长度检查（参考 MineBackup maxSmartBackupsPerFull 逻辑）
            // 当连续的增量备份数量达到上限时，强制执行全量备份以截断链条
            int maxChain = config.Archive.MaxSmartBackupsPerFull;
            if (maxChain > 0 && baseline.ConsecutiveSmartCaptures >= maxChain)
            {
                Log(I18n.Format("BackupService_Log_SmartChainLimitReached", maxChain), LogLevel.Info);
                return await DoFullBackupAsync(sourceId, captureScope, source, destDir, baseline, baseName, config, selection, comment, taskToUpdate);
            }

            // 2. 扫描并对比文件（带黑名单过滤）
            Log(I18n.Format("BackupService_Log_AnalyzingDiff"), LogLevel.Info);
            var currentStates = ScanDirectory(source, config.Filters, selection: selection);
            if (BackupSourceAvailabilityPolicy.IsUnavailable(selection, currentStates.Count))
            {
                return SourceCaptureResult.Unavailable(sourceId, captureScope);
            }
            var changeSet = CompareFileStates(currentStates, baseline.FileStates);

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

            if (result)
            {
                try { if (!string.IsNullOrWhiteSpace(listFile)) File.Delete(listFile); } catch { }

                if (!File.Exists(destFile))
                {
                    return SourceCaptureResult.Failed(sourceId, captureScope);
                }

                return CreateArchiveCapture(
                    sourceId, captureScope, destDir, fileName, RepresentationKind.CoreSmartDelta,
                    config.Archive.Format, currentStates, baseline,
                    dependencies: [baseline.BaseRepresentationId],
                    consecutiveSmartCaptures: checked(baseline.ConsecutiveSmartCaptures + 1),
                    expectedBaseVersionId: baseline.BaseVersionId,
                    deletedFiles: changeSet.DeletedFiles);
            }
            else
            {
                try { if (!string.IsNullOrWhiteSpace(listFile)) File.Delete(listFile); } catch { }
                return SourceCaptureResult.Failed(sourceId, captureScope);
            }
        }

        // --- 模式 3: Rolling copy-on-write 备份 ---
        /// <summary>
        /// 从元数据指定的、已验证的自包含基线创建唯一 staging copy，在 copy 上应用增删改，
        /// 校验通过后再以 create-once 语义安装为新归档。任何时候都不修改已提交的基线字节。
        /// </summary>
        /// <remarks>
        /// 基线缺失、类型不明、缓存损坏、归档校验失败或不能证明选择范围精确时，保守回退为 Full。
        /// </remarks>
        private static async Task<SourceCaptureResult> DoRollingBackupAsync(SourceId sourceId, FolderRewind.History.Domain.CaptureScope captureScope, string source, string destDir, SourceCaptureBaseline? baseline, string baseName, BackupConfig config, BackupSourceScope selection, string comment = "", BackupTask? taskToUpdate = null)
        {
            async Task<SourceCaptureResult> FallbackToFullAsync()
                => await DoFullBackupAsync(sourceId, captureScope, source, destDir, baseline, baseName, config, selection, comment, taskToUpdate);

            if (baseline is null
                || selection.Mode == BackupSourceScopeMode.Include
                || baseline.BaseRepresentationKind is not (RepresentationKind.CoreFull or RepresentationKind.CoreRolling)
                || !File.Exists(baseline.PayloadPath))
            {
                Log(I18n.Format("BackupService_Log_NoBaselineMetadataFallbackFull"), LogLevel.Info);
                return await FallbackToFullAsync();
            }

            string baselinePath = baseline.PayloadPath;
            string baselineFileName = Path.GetFileName(baselinePath);

            var currentStates = ScanDirectory(source, config.Filters, selection: selection);
            if (BackupSourceAvailabilityPolicy.IsUnavailable(selection, currentStates.Count))
            {
                return SourceCaptureResult.Unavailable(sourceId, captureScope);
            }

            var changeSet = CompareFileStates(currentStates, baseline.FileStates);
            if (config.Archive.SkipIfUnchanged && !changeSet.HasChanges)
            {
                Log(I18n.Format("BackupService_Log_NoChangesDetected"), LogLevel.Info);
                return SourceCaptureResult.NoChanges(sourceId, captureScope);
            }

            if (!TryResolveRequiredPassword(config, out var password, taskToUpdate))
            {
                return SourceCaptureResult.Failed(sourceId, captureScope);
            }

            string? sevenZipExe = ResolveSevenZipExecutable();
            if (string.IsNullOrWhiteSpace(sevenZipExe)
                || !await ValidateRestoreChainAsync(new List<FileInfo> { new(baselinePath) }, sevenZipExe, password, taskToUpdate).ConfigureAwait(false))
            {
                Log(I18n.Format("BackupService_Log_RestoreIntegrityArchiveCheckFailed", baselineFileName), LogLevel.Warning);
                return await FallbackToFullAsync();
            }

            string finalFileName = CreateUniqueRollingFileName(destDir, baseName, config.Archive.Format, comment);
            string finalPath = Path.Combine(destDir, finalFileName);
            string? changedListFile = null;
            string? deletedListFile = null;
            bool committed = false;

            try
            {
                using var transaction = RollingArchiveTransaction.Create(baselinePath, destDir);
                Log(I18n.Format("BackupService_Log_RollingUpdating", baselineFileName), LogLevel.Info);

                var changedFiles = changeSet.AddedFiles
                    .Concat(changeSet.ModifiedFiles)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var fileTypeRules = config.Archive.FileTypeRules;
                bool hasFileTypeRules = config.Archive.FileTypeHandlingEnabled
                    && fileTypeRules != null
                    && fileTypeRules.Count > 0;
                var fileTypeMatchers = hasFileTypeRules
                    ? CompileFileTypeWildcardPatterns(fileTypeRules!.Select(rule => rule.Pattern))
                    : Array.Empty<Regex>();
                var mainChangedFiles = hasFileTypeRules
                    ? changedFiles.Where(path => !MatchesAnyFileTypePattern(path, fileTypeMatchers)).ToList()
                    : changedFiles;

                bool updated = true;
                if (mainChangedFiles.Count > 0)
                {
                    changedListFile = Path.GetTempFileName();
                    await File.WriteAllLinesAsync(changedListFile, mainChangedFiles, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(false);
                    updated = await Run7zCommandAsync(
                        "u",
                        source,
                        transaction.StagingPath,
                        config.Archive,
                        password,
                        changedListFile,
                        filters: null,
                        fileTypeExclusions: null,
                        taskToUpdate,
                        applyAdditionalArguments: true).ConfigureAwait(false);
                }

                if (updated && hasFileTypeRules && changedFiles.Count > 0)
                {
                    updated = await RunFileTypeRulePassesAsync(
                        source,
                        transaction.StagingPath,
                        config.Archive,
                        changedFiles,
                        filters: null,
                        password,
                        taskToUpdate).ConfigureAwait(false);
                }

                if (updated && changeSet.DeletedFiles.Count > 0)
                {
                    deletedListFile = Path.GetTempFileName();
                    await File.WriteAllLinesAsync(deletedListFile, changeSet.DeletedFiles, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(false);
                    updated = await Run7zCommandAsync(
                        "d",
                        source,
                        transaction.StagingPath,
                        config.Archive,
                        password,
                        deletedListFile,
                        filters: null,
                        fileTypeExclusions: null,
                        taskToUpdate).ConfigureAwait(false);
                }

                bool exactArchiveState = updated
                    && await ValidateRestoreChainAsync(new List<FileInfo> { new(transaction.StagingPath) }, sevenZipExe, password, taskToUpdate).ConfigureAwait(false)
                    && await ValidateArchiveLogicalStateAsync(sevenZipExe, transaction.StagingPath, password, currentStates).ConfigureAwait(false);
                if (!exactArchiveState)
                {
                    transaction.Dispose();
                    return await FallbackToFullAsync();
                }

                transaction.Commit(finalPath);
                committed = true;

                return CreateArchiveCapture(
                    sourceId,
                    captureScope,
                    destDir,
                    finalFileName,
                    RepresentationKind.CoreRolling,
                    config.Archive.Format,
                    currentStates,
                    baseline,
                    dependencies: [],
                    consecutiveSmartCaptures: 0,
                    payloadState: CapturePayloadState.VerifiedFinal);
            }
            catch (Exception ex)
            {
                Log($"[Rolling] {ex.Message}", LogLevel.Error);
                if (committed)
                {
                    try { File.Delete(finalPath); } catch { }
                }
                return SourceCaptureResult.Failed(sourceId, captureScope);
            }
            finally
            {
                try { if (!string.IsNullOrWhiteSpace(changedListFile)) File.Delete(changedListFile); } catch { }
                try { if (!string.IsNullOrWhiteSpace(deletedListFile)) File.Delete(deletedListFile); } catch { }
            }
        }

        private static bool IsSafeArchiveFileName(string? fileName)
            => !string.IsNullOrWhiteSpace(fileName)
                && string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
                && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

        private static string CreateUniqueRollingFileName(string destinationDirectory, string baseName, string format, string comment)
        {
            string candidate = GenerateFileName(baseName, format, "Rolling", comment);
            if (!File.Exists(Path.Combine(destinationDirectory, candidate)))
            {
                return candidate;
            }

            string extension = Path.GetExtension(candidate);
            string stem = Path.GetFileNameWithoutExtension(candidate);
            return $"{stem}-{Guid.NewGuid():N}{extension}";
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

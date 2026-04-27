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
        // 7-Zip 解析与压缩执行集中在这里，备份、还原、安全删除共用同一套进程封装。

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
    }
}


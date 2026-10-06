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
            string? password = null,
            BackupTask? taskToUpdate = null,
            BackupSourceScope? selection = null)
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
                    var patternMatchers = CompileFileTypeWildcardPatterns(patterns);

                    Log(I18n.Format("BackupService_Log_FileTypeRulePass", level, string.Join(", ", patterns)), LogLevel.Info);

                    if (changedFileList != null)
                    {
                        // 增量模式：从变更文件列表中筛选匹配的文件
                        var matchedFiles = new List<string>();
                        foreach (var relPath in changedFileList)
                        {
                            foreach (var matcher in patternMatchers)
                            {
                                if (MatchesFileTypePattern(relPath, matcher))
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
                        sb.Append($"a -t{settings.Format} \"{archivePath}\" @\"{tmpList}\" -scsUTF-8");
                        sb.Append($" -mx={level} -m0={settings.Method} -ms=off -ssw");
                        int cpuThreads = NormalizeCpuThreadCount(settings.CpuThreads);
                        if (cpuThreads > 0) sb.Append($" -mmt{cpuThreads}"); else sb.Append(" -mmt");
                        if (!string.IsNullOrWhiteSpace(password)) sb.Append($" -p\"{password}\" -mhe=on");
                        sb.Append(" -bsp1");

                        if (!AppendAdditionalBackupArguments(sb, settings, taskToUpdate))
                        {
                            allSuccess = false;
                            continue;
                        }

                        string args = sb.ToString();
                        string safeArgs = string.IsNullOrWhiteSpace(password) ? args : args.Replace(password, "***");

                        bool ok = await RunSevenZipProcessAsync(sevenZipExe, args, sourceDir, safeArgs, taskToUpdate, runAtLowPriority: settings.RunCompressionAtLowPriority);
                        if (!ok) allSuccess = false;
                    }
                    else
                    {
                        // 全量/覆写模式：白名单下仍使用 listfile，避免 -ir! 把白名单外同类型文件追加进归档。
                        List<string>? matchedWhitelistFiles = null;
                        if (HasBackupWhitelist(filters)
                            || selection?.Mode == BackupSourceScopeMode.Include)
                        {
                            matchedWhitelistFiles = EnumerateBackupRelativeFiles(
                                    sourceDir,
                                    filters,
                                    selection: selection)
                                .Where(relPath => MatchesAnyFileTypePattern(relPath, patternMatchers))
                                .ToList();

                            if (matchedWhitelistFiles.Count == 0)
                            {
                                continue;
                            }
                        }

                        var sb = new StringBuilder();
                        sb.Append($"a -t{settings.Format} \"{archivePath}\"");

                        if (matchedWhitelistFiles != null)
                        {
                            string tmpList = Path.GetTempFileName();
                            tempFiles.Add(tmpList);
                            File.WriteAllLines(tmpList, matchedWhitelistFiles);
                            sb.Append($" @\"{tmpList}\" -scsUTF-8");
                        }
                        else
                        {
                            // 全量/覆写模式：使用 -ir! 包含匹配模式
                            foreach (var pattern in patterns)
                            {
                                sb.Append($" -ir!\"{pattern}\"");
                            }
                        }

                        sb.Append($" -mx={level} -m0={settings.Method} -ms=off -ssw");
                        int cpuThreads = NormalizeCpuThreadCount(settings.CpuThreads);
                        if (cpuThreads > 0) sb.Append($" -mmt{cpuThreads}"); else sb.Append(" -mmt");
                        if (!string.IsNullOrWhiteSpace(password)) sb.Append($" -p\"{password}\" -mhe=on");
                        sb.Append(" -bsp1");

                        // 添加黑名单排除（同主压缩一致）。白名单模式已用 listfile 精确限制。
                        if (filters?.BackupFilterMode != BackupFilterMode.Whitelist && filters?.Blacklist != null)
                        {
                            foreach (var rule in filters.Blacklist.Where(r => !string.IsNullOrWhiteSpace(r)))
                            {
                                var trimmedRule = rule.Trim();
                                if (trimmedRule.StartsWith("regex:", StringComparison.OrdinalIgnoreCase)) continue;
                                sb.Append($" -xr!\"{trimmedRule}\"");
                            }
                        }

                        if (!AppendAdditionalBackupArguments(sb, settings, taskToUpdate))
                        {
                            allSuccess = false;
                            continue;
                        }

                        string args = sb.ToString();
                        string safeArgs = string.IsNullOrWhiteSpace(password) ? args : args.Replace(password, "***");

                        bool ok = await RunSevenZipProcessAsync(sevenZipExe, args, sourceDir, safeArgs, taskToUpdate, runAtLowPriority: settings.RunCompressionAtLowPriority);
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

        private static IReadOnlyList<Regex> CompileFileTypeWildcardPatterns(
            IEnumerable<string> patterns)
            => (patterns ?? Array.Empty<string>())
                .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                .Select(pattern => new Regex(
                    "^" + Regex.Escape(pattern.Trim())
                        .Replace("\\*", ".*", StringComparison.Ordinal)
                        .Replace("\\?", ".", StringComparison.Ordinal) + "$",
                    RegexOptions.IgnoreCase
                        | RegexOptions.CultureInvariant
                        | RegexOptions.Compiled,
                    PathRuleMatcher.RegexTimeout))
                .ToArray();

        private static bool MatchesAnyFileTypePattern(
            string filePath,
            IReadOnlyList<Regex> matchers)
            => matchers.Any(matcher => MatchesFileTypePattern(filePath, matcher));

        private static bool MatchesFileTypePattern(string filePath, Regex matcher)
        {
            string fileName = Path.GetFileName(filePath);
            return (!string.IsNullOrEmpty(fileName) && matcher.IsMatch(fileName))
                || matcher.IsMatch(filePath);
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

        private static bool AppendAdditionalBackupArguments(StringBuilder builder, ArchiveSettings settings, BackupTask? taskToUpdate)
        {
            if (SevenZipAdditionalArguments.AppendValidated(builder, settings.AdditionalSevenZipArguments, out var errorMessage))
            {
                return true;
            }

            var message = I18n.Format("BackupService_Log_InvalidAdditional7zArgs", errorMessage ?? string.Empty);
            Log(message, LogLevel.Error);
            if (taskToUpdate != null)
            {
                UiDispatcherService.Enqueue(() =>
                {
                    if (string.IsNullOrEmpty(taskToUpdate.ErrorMessage))
                    {
                        taskToUpdate.ErrorMessage = message;
                    }
                });
            }

            return false;
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

    }
}

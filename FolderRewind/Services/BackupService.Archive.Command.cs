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
using System.Threading;

namespace FolderRewind.Services
{
    public static partial class BackupService
    {
        private static async Task<bool> Run7zCommandAsync(
            string commandMode,
            string sourceDir,
            string archivePath,
            ArchiveSettings settings,
            string? password = null,
            string? listFile = null,
            FilterSettings? filters = null,
            IReadOnlyList<FileTypeRule>? fileTypeExclusions = null,
            BackupTask? taskToUpdate = null,
            bool applyAdditionalArguments = false,
            BackupSourceScope? selection = null,
            string? originalSourcePath = null,
            IReadOnlyList<string>? capturedPaths = null,
            IReadOnlyList<string>? capturedDirectories = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? sevenZipExe = ResolveSevenZipExecutable();
            if (string.IsNullOrEmpty(sevenZipExe)) return false;

            string? generatedWhitelistListFile = null;

            try
            {
                if (listFile is null && capturedPaths is not null)
                {
                    generatedWhitelistListFile = Path.GetTempFileName();
                    // Directory arguments recurse in 7z. Include only genuinely empty captured directories;
                    // parent entries are emitted by the selected files without widening the boundary.
                    var nonempty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in capturedPaths)
                    {
                        var parent = file;
                        while (parent.LastIndexOf('/') is var separator && separator >= 0)
                        { parent = parent[..separator]; nonempty.Add(parent); }
                    }
                    var empty = (capturedDirectories ?? Array.Empty<string>()).Where(directory =>
                        !nonempty.Contains(directory)
                        && !Directory.EnumerateFileSystemEntries(Path.Combine(sourceDir, directory)).Any());
                    var special = CompileFileTypeWildcardPatterns((fileTypeExclusions ?? Array.Empty<FileTypeRule>()).Select(rule => rule.Pattern));
                    File.WriteAllLines(generatedWhitelistListFile,
                        capturedPaths.Where(path => !MatchesAnyFileTypePattern(path, special)).Concat(empty), new UTF8Encoding(false));
                    listFile = generatedWhitelistListFile;
                }
                // 白名单无法可靠翻译成 7z 的排除参数，统一转为相对路径 listfile，
                // 让压缩包内容、差异扫描和元数据看到的是同一批文件。
                if (string.IsNullOrWhiteSpace(listFile)
                    && ((HasBackupWhitelist(filters) || filters?.Blacklist.Count > 0) || selection?.Mode == BackupSourceScopeMode.Include))
                {
                    var includedFiles = EnumerateBackupRelativeFiles(
                        sourceDir,
                        filters,
                        originalSourcePath,
                        selection: selection);
                    if (includedFiles.Count == 0)
                    {
                        Log("[Filter][Warning] Backup whitelist matched no files; archive command skipped.", LogLevel.Warning);
                        return false;
                    }

                    generatedWhitelistListFile = Path.GetTempFileName();
                    File.WriteAllLines(generatedWhitelistListFile, includedFiles);
                    listFile = generatedWhitelistListFile;
                }

            // 构建参数
            // -mx: 压缩等级
            // -ssw: 即使打开也压缩
            // -m0: 算法
            var sb = new StringBuilder();
            sb.Append($"{commandMode} -t{settings.Format} \"{archivePath}\"");

            if (listFile != null)
            {
                // 使用文件列表
                sb.Append($" @\"{listFile}\" -scsUTF-8");
            }
            else
            {
                // 直接指定源目录 (加通配符以包含内容而非目录本身，视需求而定)
                sb.Append($" \"{sourceDir}\\*\"");
            }

            sb.Append($" -mx={settings.CompressionLevel} -m0={settings.Method} -ssw");
            if (listFile is not null) sb.Append(" -spd"); // List entries are literal paths, never wildcards.

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
                foreach (var rule in fileTypeExclusions.Where(r => capturedPaths is null && !string.IsNullOrWhiteSpace(r.Pattern)))
                {
                    sb.Append($" -xr!\"{rule.Pattern.Trim()}\"");
                }
            }

            // 添加黑名单排除规则。白名单模式下不叠加黑名单，避免旧规则误伤明确纳入的文件。
            if (listFile is null && filters?.BackupFilterMode != BackupFilterMode.Whitelist
                && filters?.Blacklist != null
                && filters.Blacklist.Count > 0)
            {
                foreach (var rule in filters.Blacklist.Where(r => r.SemanticsVersion == 1 && !string.IsNullOrWhiteSpace(r.Pattern)))
                {
                    var trimmedRule = rule.Pattern.Trim();

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
                            var relative = Path.GetRelativePath(originalSourcePath ?? sourceDir, trimmedRule);
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

            if (applyAdditionalArguments && !AppendAdditionalBackupArguments(sb, settings, taskToUpdate))
            {
                return false;
            }

            string args = sb.ToString();
            string safeArgs = string.IsNullOrWhiteSpace(password) ? args : args.Replace(password, "***");

            return await RunSevenZipProcessAsync(sevenZipExe, args, sourceDir, safeArgs, taskToUpdate, runAtLowPriority: settings.RunCompressionAtLowPriority, cancellationToken: cancellationToken);
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(generatedWhitelistListFile))
                    {
                        File.Delete(generatedWhitelistListFile);
                    }
                }
                catch
                {
                }
            }
        }
    }
}

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
            bool applyAdditionalArguments = false)
        {
            string? sevenZipExe = ResolveSevenZipExecutable();
            if (string.IsNullOrEmpty(sevenZipExe)) return false;

            string? generatedWhitelistListFile = null;

            try
            {
                // 白名单无法可靠翻译成 7z 的排除参数，统一转为相对路径 listfile，
                // 让压缩包内容、差异扫描和元数据看到的是同一批文件。
                if (string.IsNullOrWhiteSpace(listFile) && HasBackupWhitelist(filters))
                {
                    var includedFiles = EnumerateBackupRelativeFiles(sourceDir, filters);
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

            // 添加黑名单排除规则。白名单模式下不叠加黑名单，避免旧规则误伤明确纳入的文件。
            if (filters?.BackupFilterMode != BackupFilterMode.Whitelist
                && filters?.Blacklist != null
                && filters.Blacklist.Count > 0)
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

            if (applyAdditionalArguments && !AppendAdditionalBackupArguments(sb, settings, taskToUpdate))
            {
                return false;
            }

            string args = sb.ToString();
            string safeArgs = string.IsNullOrWhiteSpace(password) ? args : args.Replace(password, "***");

            return await RunSevenZipProcessAsync(sevenZipExe, args, sourceDir, safeArgs, taskToUpdate, runAtLowPriority: settings.RunCompressionAtLowPriority);
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
    }
}

using FolderRewind.Models;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static partial class BackupService
    {
        // 过滤规则集中在这里：备份扫描和插件热备份都会复用同一套黑名单语义。

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
            if (string.IsNullOrWhiteSpace(fileToCheck) || blacklist == null)
            {
                return false;
            }

            return PathRuleMatcher.CreateForBackup(
                blacklist,
                backupSourceRoot,
                originalSourceRoot,
                useRegex).IsMatch(fileToCheck);
        }

        public static bool HasBackupWhitelist(FilterSettings? filters)
        {
            return filters?.BackupFilterMode == BackupFilterMode.Whitelist
                && filters.BackupWhitelist != null
                && filters.BackupWhitelist.Any(rule => !string.IsNullOrWhiteSpace(rule));
        }

        public static bool IsPartialBackupFilter(FilterSettings? filters)
        {
            return HasBackupWhitelist(filters);
        }

        public static bool TryValidateFilterRules(FilterSettings? filters, out string errorMessage)
        {
            errorMessage = string.Empty;
            if (filters == null)
            {
                return true;
            }

            try
            {
                IEnumerable<string>? backupRules = filters.BackupFilterMode == BackupFilterMode.Whitelist
                    ? filters.BackupWhitelist
                    : filters.Blacklist;
                PathRuleMatcher.ValidateBackupRules(backupRules, filters.UseRegex);
                PathRuleMatcher.ValidateRestoreRules(filters.RestoreWhitelist);
                return true;
            }
            catch (PathRuleValidationException ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static bool TryValidateBackupFilterRules(FilterSettings? filters, out string errorMessage)
        {
            errorMessage = string.Empty;
            if (filters == null)
            {
                return true;
            }

            try
            {
                IEnumerable<string>? rules = filters.BackupFilterMode == BackupFilterMode.Whitelist
                    ? filters.BackupWhitelist
                    : filters.Blacklist;
                PathRuleMatcher.ValidateBackupRules(rules, filters.UseRegex);
                return true;
            }
            catch (PathRuleValidationException ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 检查文件是否命中备份白名单。白名单只决定“纳入备份”，不会参与 Clean 还原保护。
        /// </summary>
        public static bool IsWhitelistedForBackup(
            string fileToCheck,
            string backupSourceRoot,
            string originalSourceRoot,
            IEnumerable<string>? whitelist,
            bool useRegex = false)
        {
            if (string.IsNullOrWhiteSpace(fileToCheck) || whitelist == null)
            {
                return false;
            }

            return PathRuleMatcher.CreateForBackup(
                whitelist,
                backupSourceRoot,
                originalSourceRoot,
                useRegex).IsMatch(fileToCheck);
        }

        public static bool ShouldIncludeInBackup(
            string fileToCheck,
            string backupSourceRoot,
            string originalSourceRoot,
            FilterSettings? filters)
        {
            if (filters == null)
            {
                return true;
            }

            if (filters.BackupFilterMode == BackupFilterMode.Whitelist)
            {
                return IsWhitelistedForBackup(
                    fileToCheck,
                    backupSourceRoot,
                    originalSourceRoot,
                    filters.BackupWhitelist,
                    filters.UseRegex);
            }

            return !IsBlacklisted(
                fileToCheck,
                backupSourceRoot,
                originalSourceRoot,
                filters.Blacklist,
                filters.UseRegex);
        }

        private static PathRuleMatcher? CreateBackupMatcher(
            string backupSourceRoot,
            string originalSourceRoot,
            FilterSettings? filters)
        {
            if (filters == null)
            {
                return null;
            }

            IEnumerable<string>? rules = filters.BackupFilterMode == BackupFilterMode.Whitelist
                ? filters.BackupWhitelist
                : filters.Blacklist;
            return PathRuleMatcher.CreateForBackup(
                rules,
                backupSourceRoot,
                originalSourceRoot,
                filters.UseRegex);
        }

        private static bool ShouldIncludeInBackup(
            string fileToCheck,
            FilterSettings? filters,
            PathRuleMatcher? matcher)
        {
            if (filters == null || matcher == null)
            {
                return true;
            }

            bool matched = matcher.IsMatch(fileToCheck);
            return filters.BackupFilterMode == BackupFilterMode.Whitelist ? matched : !matched;
        }

        /// <summary>
        /// 过滤文件列表，按当前备份过滤模式保留实际应进入归档的文件。
        /// </summary>
        public static List<string> FilterBlacklist(
            IEnumerable<string> files,
            string backupSourceRoot,
            string originalSourceRoot,
            FilterSettings? filters)
        {
            if (filters == null)
            {
                return files.ToList();
            }

            var matcher = CreateBackupMatcher(backupSourceRoot, originalSourceRoot, filters);
            return files.Where(file => ShouldIncludeInBackup(file, filters, matcher)).ToList();
        }

        private static List<string> EnumerateBackupRelativeFiles(string path, FilterSettings? filters = null, string? originalSourcePath = null)
        {
            var result = new List<string>();
            var dirInfo = new DirectoryInfo(path);
            var enumOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System
            };

            var originalRoot = originalSourcePath ?? path;
            var matcher = CreateBackupMatcher(path, originalRoot, filters);
            foreach (var file in dirInfo.EnumerateFiles("*", enumOptions))
            {
                if (!ShouldIncludeInBackup(file.FullName, filters, matcher))
                {
                    continue;
                }

                result.Add(Path.GetRelativePath(path, file.FullName));
            }

            return result;
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
            var matcher = CreateBackupMatcher(path, originalRoot, filters);

            // 获取所有文件，使用相对路径作为 Key，采用流式枚举避免一次性加载大目录列表。
            foreach (var file in dirInfo.EnumerateFiles("*", enumOptions))
            {
                // 统一检查备份过滤模式：黑名单排除，白名单纳入。
                if (!ShouldIncludeInBackup(file.FullName, filters, matcher))
                {
                    continue;
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

            if (result.Count == 0 && filters != null
                && ((filters.BackupFilterMode == BackupFilterMode.Blacklist && filters.Blacklist.Count > 0)
                    || HasBackupWhitelist(filters)))
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

    }
}


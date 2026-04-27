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
        // 过滤规则集中在这里：备份扫描和插件热备份都会复用同一套黑名单语义。

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
    }
}


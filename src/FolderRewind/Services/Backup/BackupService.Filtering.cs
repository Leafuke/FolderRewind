using FolderRewind.Models;
using FolderRewind.History.Capture;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.History.Application;
using FolderRewind.History.Storage;

namespace FolderRewind.Services
{
    public static partial class BackupService
    {

        public static bool HasBackupWhitelist(FilterSettings? filters)
        {
            return filters?.BackupFilterMode == BackupFilterMode.Whitelist
                && filters.BackupWhitelist != null
                && filters.BackupWhitelist.Any(rule => !string.IsNullOrWhiteSpace(rule.Pattern));
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
                IEnumerable<PathFilterRule>? backupRules = filters.BackupFilterMode == BackupFilterMode.Whitelist
                    ? filters.BackupWhitelist
                    : filters.Blacklist;
                PathRuleMatcher.ValidateRules(backupRules);
                PathRuleMatcher.ValidateRules(filters.RestoreWhitelist, restore: true);
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
                IEnumerable<PathFilterRule>? rules = filters.BackupFilterMode == BackupFilterMode.Whitelist
                    ? filters.BackupWhitelist
                    : filters.Blacklist;
                PathRuleMatcher.ValidateRules(rules);
                return true;
            }
            catch (PathRuleValidationException ex)
            {
                errorMessage = ex.Message;
                return false;
            }
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

            return ShouldIncludeInBackup(fileToCheck, filters, CreateBackupMatcher(backupSourceRoot, originalSourceRoot, filters));
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

            IEnumerable<PathFilterRule>? rules = filters.BackupFilterMode == BackupFilterMode.Whitelist
                ? filters.BackupWhitelist
                : filters.Blacklist;
            return PathRuleMatcher.CreateForBackupRules(
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

        private static bool MayDescendForBackup(string relative, string root, FilterSettings? filters, PathRuleMatcher? matcher)
            => filters is null || matcher is null || (filters.BackupFilterMode == BackupFilterMode.Whitelist
                ? matcher.MayContainDescendant(Path.Combine(root, relative)) : !matcher.ExcludesWholeSubtree(Path.Combine(root, relative)));

        private static List<string> EnumerateBackupRelativeFiles(
            string path,
            FilterSettings? filters = null,
            string? originalSourcePath = null,
            BackupSourceScope? selection = null)
        {
            var originalRoot = originalSourcePath ?? path;
            var matcher = CreateBackupMatcher(path, originalRoot, filters);
            return BackupSourceFileEnumerator.Enumerate(
                    path,
                    selection,
                    file => ShouldIncludeInBackup(file, filters, matcher),
                    mayDescend: relative => MayDescendForBackup(relative, path, filters, matcher))
                .Select(file => file.RelativePath)
                .ToList();
        }

        // --- 辅助：元数据处理 ---
        /// <summary>
        /// 扫描源目录构建"相对路径 → 文件状态"字典——这是全模式差异比对（增量基准）的唯一数据源。
        /// Hash 刻意留空：默认口径是大小 + 修改时间比对（与 MineBackup 一致），计算哈希代价过高。
        /// 配置了黑/白名单却扫出 0 项时，探测无过滤枚举是否有文件，有则警告过滤器可能过宽。
        /// </summary>
        private static Dictionary<string, SourceCaptureFileState> ScanDirectory(
            string path,
            FilterSettings? filters = null,
            string? originalSourcePath = null,
            BackupSourceScope? selection = null,
            System.Threading.CancellationToken cancellationToken = default,
            HistoryRestoreSourceBinding? historicalBinding = null)
        {
            using var stage = FolderRewind.Plugin.Runtime.Artifacts.ArtifactDiagnostics.Source.StartActivity("source.scan");
            using var timing = HistoryOperationDiagnostics.Current?.Measure("source-scan");
            // 预估容量以减少字典扩容开销
            var result = new Dictionary<string, SourceCaptureFileState>(1024, StringComparer.OrdinalIgnoreCase);
            var originalRoot = originalSourcePath ?? path;
            var matcher = CreateBackupMatcher(path, originalRoot, filters);
            var historical = historicalBinding is null ? null : new HistoryRestoreTreeScope(historicalBinding);

            foreach (var file in BackupSourceFileEnumerator.EnumerateUnordered(
                         path,
                         selection,
                         candidate => historical is null ? ShouldIncludeInBackup(candidate, filters, matcher)
                             : historical.Includes(Path.GetRelativePath(path, candidate).Replace('\\', '/')), cancellationToken, requireComplete: true,
                         mayDescend: relative => historical?.MayDescend(relative) ?? MayDescendForBackup(relative, path, filters, matcher)))
            {
                if (!result.TryAdd(file.RelativePath, new SourceCaptureFileState(file.Size, file.LastWriteTimeUtc)))
                    throw new System.IO.InvalidDataException("Capture input contains ambiguous file paths: " + file.RelativePath);
            }

            if (result.Count == 0 && filters != null
                && ((filters.BackupFilterMode == BackupFilterMode.Blacklist && filters.Blacklist.Count > 0)
                    || HasBackupWhitelist(filters)))
            {
                try
                {
                    bool hasAnyFile = BackupSourceFileEnumerator.EnumerateUnordered(path, selection).Any();
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

            HistoryOperationDiagnostics.Current?.Count("source-scan-files", result.Count);
            return result;
        }

    }
}


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Services;

internal sealed class PathRuleValidationException : Exception
{
    public PathRuleValidationException(string message)
        : base(message)
    {
    }

    public PathRuleValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Compiles path-filter rules once per backup or restore operation.
/// Literal rules are looked up by path segments, so matching cost does not grow
/// linearly with the number of selected-region files.
/// </summary>
internal sealed class PathRuleMatcher
{
    internal const int MaxRuleCount = 65_536;
    internal const int MaxRuleLength = 4_096;
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private readonly HashSet<string> _literalRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Regex> _wildcardRules = new();
    private readonly List<Regex> _regexRules = new();
    private readonly bool _matchWildcardAgainstRelativePath;
    private readonly string _backupSourceRoot;
    private readonly string _originalSourceRoot;
    private readonly HashSet<string> _fileNames = new(StringComparer.OrdinalIgnoreCase), _filePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directoryNames = new(StringComparer.OrdinalIgnoreCase), _directoryPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(PathFilterPattern Pattern, Regex Matcher)> _typedPatterns = new();
    private readonly bool _hasTypedDirectoryRules;

    private PathRuleMatcher(
        IEnumerable<string>? rules,
        string backupSourceRoot,
        string originalSourceRoot,
        bool recognizeRegexRules,
        bool enableRegexRules,
        bool matchWildcardAgainstRelativePath)
    {
        _backupSourceRoot = NormalizeFullPath(backupSourceRoot);
        _originalSourceRoot = NormalizeFullPath(originalSourceRoot);
        _matchWildcardAgainstRelativePath = matchWildcardAgainstRelativePath;

        var normalizedRules = (rules ?? Array.Empty<string>())
            .Where(rule => !string.IsNullOrWhiteSpace(rule))
            .Select(rule => rule.Trim())
            .ToList();

        if (normalizedRules.Count > MaxRuleCount)
        {
            throw new PathRuleValidationException(
                $"Filter contains {normalizedRules.Count} rules; the maximum is {MaxRuleCount}.");
        }

        foreach (string rule in normalizedRules)
        {
            if (rule.Length > MaxRuleLength)
            {
                throw new PathRuleValidationException(
                    $"Filter rule exceeds the maximum length of {MaxRuleLength} characters.");
            }

            if (recognizeRegexRules
                && rule.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
            {
                if (!enableRegexRules)
                {
                    continue;
                }

                string pattern = rule[6..];
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    throw new PathRuleValidationException("Regular-expression filter rule is empty.");
                }

                try
                {
                    _regexRules.Add(new Regex(
                        pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                        RegexTimeout));
                }
                catch (ArgumentException ex)
                {
                    throw new PathRuleValidationException($"Invalid regular-expression filter rule '{rule}'.", ex);
                }

                continue;
            }

            if (rule.Contains('*') || rule.Contains('?'))
            {
                string wildcardPattern = "^" + Regex.Escape(NormalizePath(rule))
                    .Replace("\\*", ".*", StringComparison.Ordinal)
                    .Replace("\\?", ".", StringComparison.Ordinal) + "$";
                try
                {
                    _wildcardRules.Add(new Regex(
                        wildcardPattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                        RegexTimeout));
                }
                catch (ArgumentException ex)
                {
                    throw new PathRuleValidationException($"Invalid wildcard filter rule '{rule}'.", ex);
                }

                continue;
            }

            AddLiteralRule(rule);
        }
    }

    private PathRuleMatcher(IEnumerable<PathFilterRule>? rules, string root, string originalRoot, bool enableRegexRules, bool restore)
        : this((rules ?? []).Where(r => r.SemanticsVersion == 1).Select(r => r.Pattern), root, originalRoot,
            recognizeRegexRules: !restore, enableRegexRules: !restore && enableRegexRules, matchWildcardAgainstRelativePath: !restore)
    {
        var materialized = (rules ?? []).ToArray();
        if (materialized.Length > MaxRuleCount) throw new PathRuleValidationException($"Filter contains more than {MaxRuleCount} rules.");
        foreach (var rule in materialized)
        {
            if (!Enum.IsDefined(rule.Target) || rule.SemanticsVersion is not (1 or 2)
                || rule.SemanticsVersion == 1 && rule.Target != PathFilterTarget.Any)
                throw new PathRuleValidationException("Invalid filter target or semantics version.");
            if (rule.SemanticsVersion == 1) continue;
            var pattern = PathFilterPattern.Parse(rule, originalRoot);
            if (!pattern.Applicable || pattern.IsRegex && !enableRegexRules) continue;
            if (rule.Target != PathFilterTarget.File) _hasTypedDirectoryRules = true;
            if (pattern.IsLiteral)
            {
                if (rule.Target != PathFilterTarget.Directory) (pattern.NameOnly ? _fileNames : _filePaths).Add(pattern.Pattern);
                if (rule.Target != PathFilterTarget.File) (pattern.NameOnly ? _directoryNames : _directoryPaths).Add(pattern.Pattern);
            }
            else
            {
                try { _typedPatterns.Add((pattern, pattern.Compile())); }
                catch (ArgumentException error) { throw new PathRuleValidationException($"Invalid filter '{rule.Pattern}'.", error); }
            }
        }
    }

    internal static PathRuleMatcher CreateForBackupRules(IEnumerable<PathFilterRule>? rules, string root, string originalRoot, bool enableRegexRules)
        => new(rules, root, originalRoot, enableRegexRules, restore: false);
    internal static PathRuleMatcher CreateForRestoreRules(IEnumerable<PathFilterRule>? rules, string root, bool enableRegexRules = false)
        => new(rules, root, root, enableRegexRules, restore: true);
    internal static void ValidateRules(IEnumerable<PathFilterRule>? rules, bool restore = false)
        => _ = new PathRuleMatcher(rules, "", "", enableRegexRules: true, restore);

        /// <summary>
        /// 构建备份用的匹配器：识别 regex: 前缀规则（可按设置禁用），通配符规则同时
        /// 匹配文件名与相对路径。backupSourceRoot 与 originalSourceRoot 分离用于快照源路径
        /// （插件热备份把源替换为快照目录后仍按原路径规则匹配）。
        /// </summary>
        public static PathRuleMatcher CreateForBackup(
        IEnumerable<string>? rules,
        string backupSourceRoot,
        string originalSourceRoot,
        bool enableRegexRules)
        => new(
            rules,
            backupSourceRoot,
            originalSourceRoot,
            recognizeRegexRules: true,
            enableRegexRules,
            matchWildcardAgainstRelativePath: true);

        /// <summary>
        /// 构建恢复白名单用的匹配器：不识别正则规则，通配符只匹配完整路径段（不匹配相对路径）。
        /// </summary>
        public static PathRuleMatcher CreateForRestore(
        IEnumerable<string>? rules,
        string comparisonRoot)
        => new(
            rules,
            comparisonRoot,
            comparisonRoot,
            recognizeRegexRules: false,
            enableRegexRules: false,
            matchWildcardAgainstRelativePath: false);

    public static void ValidateBackupRules(IEnumerable<string>? rules, bool enableRegexRules)
        // 即使当前关闭正则匹配，保存配置时也必须验证已写入的 regex: 规则。
        => _ = CreateForBackup(
            rules,
            string.Empty,
            string.Empty,
            enableRegexRules: true);

    public static void ValidateRestoreRules(IEnumerable<string>? rules)
        => _ = CreateForRestore(rules, string.Empty);

    // 字面规则匹配路径段且覆盖其后代；任意正则/通配符命中本目录不代表覆盖子树。
    internal bool ExcludesWholeSubtree(string directory)
    {
        var full = NormalizeFullPath(directory);
        var relative = TryGetRelativePath(_backupSourceRoot, full);
        return MatchesLiteralPath(full) || (!string.IsNullOrWhiteSpace(relative) && MatchesLiteralPath(relative))
            || MatchesTyped(relative, isDirectory: true, directoriesOnly: true);
    }

        /// <summary>
        /// 判断候选路径是否命中任一规则，按字面量 → 通配符 → 正则的顺序匹配。
        /// 候选先换算为相对源根的路径再参与匹配；正则规则的执行带 250ms 超时
        /// （<see cref="RegexTimeout"/>），防止病态模式拖垮备份。
        /// </summary>
        public bool IsMatch(string candidatePath, bool isDirectory = false)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        string fullPath = NormalizeFullPath(candidatePath);
        string relativePath = TryGetRelativePath(_backupSourceRoot, fullPath);
        string fileName = Path.GetFileName(fullPath);

        if (MatchesTyped(relativePath, isDirectory)) return true;

        if (_literalRules.Contains(fullPath) || _literalRules.Contains(relativePath)
            || MatchesLiteralPath(fullPath)
            || (!string.IsNullOrWhiteSpace(relativePath) && MatchesLiteralPath(relativePath)))
        {
            return true;
        }

        foreach (Regex wildcard in _wildcardRules)
        {
            if ((!string.IsNullOrEmpty(fileName) && wildcard.IsMatch(fileName))
                || (_matchWildcardAgainstRelativePath
                    && !string.IsNullOrWhiteSpace(relativePath)
                    && wildcard.IsMatch(relativePath)))
            {
                return true;
            }
        }

        foreach (Regex regex in _regexRules)
        {
            if (regex.IsMatch(candidatePath)
                || (!string.IsNullOrWhiteSpace(relativePath) && regex.IsMatch(relativePath)))
            {
                return true;
            }
        }

        return false;
    }

    private bool MatchesTyped(string relative, bool isDirectory, bool directoriesOnly = false)
    {
        if (string.IsNullOrEmpty(relative)) return false;
        if (!isDirectory && !directoriesOnly && (_filePaths.Contains(relative) || _fileNames.Contains(Path.GetFileName(relative)))) return true;
        foreach (var (pattern, matcher) in _typedPatterns)
            if (!isDirectory && !directoriesOnly && pattern.Rule.Target != PathFilterTarget.Directory
                && matcher.IsMatch(pattern.NameOnly ? Path.GetFileName(relative) : relative)) return true;
        if (!_hasTypedDirectoryRules) return false;
        var directory = isDirectory ? relative : Parent(relative);
        while (directory.Length > 0)
        {
            if (_directoryPaths.Contains(directory) || _directoryNames.Contains(Path.GetFileName(directory))) return true;
            foreach (var (pattern, matcher) in _typedPatterns)
                if (pattern.Rule.Target != PathFilterTarget.File && matcher.IsMatch(pattern.NameOnly ? Path.GetFileName(directory) : directory)) return true;
            directory = Parent(directory);
        }
        return false;
    }
    private static string Parent(string path) => path.LastIndexOf('/') is var separator && separator >= 0 ? path[..separator] : "";

    private void AddLiteralRule(string rule)
    {
        string normalizedRule = NormalizePath(rule);
        if (!string.IsNullOrWhiteSpace(normalizedRule))
        {
            _literalRules.Add(normalizedRule);
        }

        if (!Path.IsPathRooted(rule)
            || string.IsNullOrWhiteSpace(_originalSourceRoot)
            || string.IsNullOrWhiteSpace(_backupSourceRoot))
        {
            return;
        }

        string fullRule = NormalizeFullPath(rule);
        string relativeRule = TryGetRelativePath(_originalSourceRoot, fullRule);
        if (!string.IsNullOrWhiteSpace(relativeRule))
        {
            _literalRules.Add(relativeRule);
        }
    }

    private bool MatchesLiteralPath(string path)
    {
        if (_literalRules.Count == 0 || path.Length == 0) return false;
        var lookup = _literalRules.GetAlternateLookup<ReadOnlySpan<char>>();
        var span = path.AsSpan();
        for (var start = 0; start < span.Length; start++)
        {
            if (start > 0 && span[start - 1] != '/') continue;
            for (var end = start + 1; end <= span.Length; end++)
            {
                if (end != span.Length && span[end] != '/') continue;
                if (lookup.Contains(span[start..end])) return true;
            }
        }

        return false;
    }

        /// <summary>
        /// 计算候选路径相对根的规范化路径；候选不在根内（结果为根路径或以 ../ 开头）时返回空串。
        /// </summary>
        private static string TryGetRelativePath(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
        {
            return string.Empty;
        }

        try
        {
            string relative = Path.GetRelativePath(root, candidate);
            if (Path.IsPathRooted(relative)
                || string.Equals(relative, "..", StringComparison.Ordinal)
                || relative.StartsWith("../", StringComparison.Ordinal)
                || relative.StartsWith(@"..\", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return NormalizePath(relative);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return NormalizePath(Path.GetFullPath(path));
        }
        catch
        {
            return NormalizePath(path);
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string normalized = path.Trim()
            .Replace(Path.AltDirectorySeparatorChar, '/')
            .Replace(Path.DirectorySeparatorChar, '/');

        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized.Trim('/');
    }
}

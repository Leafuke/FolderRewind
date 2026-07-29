using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

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

    private PathRuleMatcher(
        IEnumerable<string>? rules,
        string backupSourceRoot,
        string originalSourceRoot,
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

            if (rule.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
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

    public static PathRuleMatcher CreateForBackup(
        IEnumerable<string>? rules,
        string backupSourceRoot,
        string originalSourceRoot,
        bool enableRegexRules)
        => new(
            rules,
            backupSourceRoot,
            originalSourceRoot,
            enableRegexRules,
            matchWildcardAgainstRelativePath: true);

    public static PathRuleMatcher CreateForRestore(
        IEnumerable<string>? rules,
        string comparisonRoot)
        => new(
            rules,
            comparisonRoot,
            comparisonRoot,
            enableRegexRules: false,
            matchWildcardAgainstRelativePath: false);

    public static void ValidateBackupRules(IEnumerable<string>? rules, bool enableRegexRules)
        => _ = CreateForBackup(rules, string.Empty, string.Empty, enableRegexRules);

    public static void ValidateRestoreRules(IEnumerable<string>? rules)
        => _ = CreateForRestore(rules, string.Empty);

    public bool IsMatch(string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        string fullPath = NormalizeFullPath(candidatePath);
        string relativePath = TryGetRelativePath(_backupSourceRoot, fullPath);
        string fileName = Path.GetFileName(fullPath);

        if (MatchesLiteralPath(fullPath)
            || (!string.IsNullOrWhiteSpace(relativePath) && MatchesLiteralPath(relativePath)))
        {
            return true;
        }

        foreach (Regex wildcard in _wildcardRules)
        {
            if ((!string.IsNullOrEmpty(fileName) && wildcard.IsMatch(NormalizePath(fileName)))
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
        string normalized = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        // Check all segment-bounded contiguous subpaths. This preserves the old
        // boundary semantics while making lookup independent of rule count.
        for (int start = 0; start < segments.Length; start++)
        {
            var builder = new StringBuilder();
            for (int end = start; end < segments.Length; end++)
            {
                if (builder.Length > 0)
                {
                    builder.Append('/');
                }

                builder.Append(segments[end]);
                if (_literalRules.Contains(builder.ToString()))
                {
                    return true;
                }
            }
        }

        return false;
    }

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

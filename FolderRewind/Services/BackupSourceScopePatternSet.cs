using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FolderRewind.Services;

/// <summary>
/// A validated, precompiled set of source-scope globs. The same semantics are used
/// by discovery previews and by the backup runtime.
/// </summary>
public sealed class BackupSourceScopePatternSet
{
    public const int MaximumPatternCount = 256;
    public const int MaximumPatternLength = 1024;

    private readonly IReadOnlyList<Regex> _matchers;

    private BackupSourceScopePatternSet(IReadOnlyList<string> patterns, IReadOnlyList<Regex> matchers)
    {
        Patterns = patterns;
        _matchers = matchers;
    }

    public IReadOnlyList<string> Patterns { get; }

    public bool IsMatch(string relativePath)
    {
        if (!IsSafeRelativePattern(relativePath))
        {
            return false;
        }

        var normalized = relativePath.Replace('\\', '/');
        return _matchers.Any(matcher => matcher.IsMatch(normalized));
    }

    public static BackupSourceScopePatternSet Compile(IEnumerable<string>? patterns)
    {
        var normalized = NormalizeAndValidate(patterns);
        var matchers = new List<Regex>(normalized.Count);
        foreach (var pattern in normalized)
        {
            try
            {
                matchers.Add(new Regex(
                    "^" + ToRegex(pattern) + "$",
                    RegexOptions.IgnoreCase
                    | RegexOptions.CultureInvariant
                    | RegexOptions.NonBacktracking));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException($"Invalid source scope glob '{pattern}'.", ex);
            }
        }

        return new BackupSourceScopePatternSet(normalized, matchers);
    }

    public static IReadOnlyList<string> NormalizeAndValidate(IEnumerable<string>? patterns)
    {
        var materialized = (patterns ?? Array.Empty<string>())
            .Select(pattern => pattern?.Trim().Replace('\\', '/') ?? string.Empty)
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (materialized.Count == 0)
        {
            throw new InvalidDataException("Include source scope must contain at least one relative glob.");
        }
        if (materialized.Count > MaximumPatternCount)
        {
            throw new InvalidDataException($"A source scope may contain at most {MaximumPatternCount} include globs.");
        }

        foreach (var pattern in materialized)
        {
            if (pattern.Length > MaximumPatternLength)
            {
                throw new InvalidDataException($"A source scope glob may contain at most {MaximumPatternLength} characters.");
            }
            if (!IsSafeRelativePattern(pattern))
            {
                throw new InvalidDataException("Include source scope contains an absolute or parent-traversing glob.");
            }
        }

        return materialized;
    }

    public static bool IsSafeRelativePattern(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumPatternLength
            || value.Contains('\0')
            || value.Contains('\r')
            || value.Contains('\n')
            || Path.IsPathRooted(value))
        {
            return false;
        }

        return !value.Replace('\\', '/').Split('/').Any(segment => segment == "..");
    }

    private static string ToRegex(string pattern)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < pattern.Length; index++)
        {
            var current = pattern[index];
            switch (current)
            {
                case '*':
                    if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                    {
                        if (index + 2 < pattern.Length && pattern[index + 2] == '/')
                        {
                            builder.Append("(?:.*/)?");
                            index += 2;
                        }
                        else
                        {
                            builder.Append(".*");
                            index++;
                        }
                    }
                    else
                    {
                        builder.Append("[^/]*");
                    }
                    break;
                case '?':
                    builder.Append("[^/]");
                    break;
                case '[':
                    var closing = pattern.IndexOf(']', index + 1);
                    if (closing > index + 1)
                    {
                        var content = pattern[(index + 1)..closing];
                        builder.Append('[');
                        if (content.StartsWith('!'))
                        {
                            builder.Append('^');
                            content = content[1..];
                        }
                        builder.Append(content.Replace("\\", "\\\\", StringComparison.Ordinal));
                        builder.Append(']');
                        index = closing;
                    }
                    else
                    {
                        builder.Append("\\[");
                    }
                    break;
                case '/':
                    builder.Append('/');
                    break;
                default:
                    builder.Append(Regex.Escape(current.ToString()));
                    break;
            }
        }

        return builder.ToString();
    }
}

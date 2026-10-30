using FolderRewind.Plugin.Abstractions;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FolderRewind.Services;

/// <summary>The parsed version-2 grammar shared by runtime matching and historical coverage proofs.</summary>
internal sealed record PathFilterPattern(PathFilterRule Rule, string Pattern, bool NameOnly, bool IsRegex, bool Applicable)
{
    internal bool IsLiteral => !IsRegex && !Pattern.Contains('*') && !Pattern.Contains('?');

    // Preserve literal Unicode casing: lowercasing can change OrdinalIgnoreCase's equivalence class.
    internal PathFilterRule ToBoundaryRule()
        => Rule with { Pattern = IsRegex ? "regex:" + Pattern : (NameOnly ? "" : "./") + Pattern };

    internal static PathFilterPattern Parse(PathFilterRule rule, string sourceRoot)
    {
        if (rule is null || string.IsNullOrWhiteSpace(rule.Pattern) || rule.Pattern.Length > PathRuleMatcher.MaxRuleLength
            || rule.SemanticsVersion != 2 || !Enum.IsDefined(rule.Target)) throw new PathRuleValidationException("Invalid typed filter rule.");
        var pattern = rule.Pattern.Trim();
        if (pattern.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(pattern[6..])) throw new PathRuleValidationException("Regular-expression filter rule is empty.");
            return new(rule, pattern[6..], false, true, true);
        }
        var rooted = Path.IsPathRooted(pattern);
        if (rooted && sourceRoot.Length == 0)
        {
            if (pattern.Any(char.IsControl)) throw new PathRuleValidationException("Invalid absolute filter path.");
            return new(rule, pattern.Replace('\\', '/').TrimEnd('/'), false, false, true);
        }
        if (rooted && sourceRoot.Length > 0)
        {
            pattern = Path.GetRelativePath(Path.GetFullPath(sourceRoot), Path.GetFullPath(pattern));
            if (Path.IsPathRooted(pattern) || pattern == ".." || pattern.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return new(rule, "", false, false, false);
        }
        pattern = pattern.Replace('\\', '/');
        var anchored = rooted || pattern.StartsWith("./", StringComparison.Ordinal) || pattern.Contains('/');
        if (pattern.StartsWith("./", StringComparison.Ordinal)) pattern = pattern[2..];
        pattern = pattern.TrimEnd('/');
        if (rule.Target == PathFilterTarget.Directory)
            while (pattern.EndsWith("/**", StringComparison.Ordinal)) pattern = pattern[..^3];
        if (pattern.Length == 0 || pattern.Any(char.IsControl) || (!rooted && pattern.Contains(':'))
            || pattern.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new PathRuleValidationException("A typed filter must name a file, directory, or safe source-relative pattern.");
        return new(rule, pattern, !anchored && !pattern.Contains('/'), false, true);
    }

    internal Regex Compile()
        => new(IsRegex ? Pattern : "\\A" + GlobRegex(Pattern) + "\\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, PathRuleMatcher.RegexTimeout);

    private static string GlobRegex(string pattern)
    {
        var result = new StringBuilder();
        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '*')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    if (index + 2 < pattern.Length && pattern[index + 2] == '/') { result.Append("(?:.*/)?"); index += 2; }
                    else { result.Append(".*"); index++; }
                }
                else result.Append("[^/]*");
            }
            else if (pattern[index] == '?') result.Append("[^/]");
            else result.Append(Regex.Escape(pattern[index].ToString()));
        }
        return result.ToString();
    }
}

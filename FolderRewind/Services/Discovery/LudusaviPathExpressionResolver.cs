using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FolderRewind.Services.Discovery;

public sealed class LudusaviPathEnvironment
{
    public string Home { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string AppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string LocalAppDataLow { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData",
        "LocalLow");
    public string Documents { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public string Public { get; init; } = Environment.GetEnvironmentVariable("PUBLIC") ?? string.Empty;
    public string ProgramData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    public string WindowsDirectory { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public string UserName { get; init; } = Environment.UserName;
}

public sealed class ResolvedLudusaviResource
{
    public required string FixedRoot { get; init; }
    public IReadOnlyList<string> IncludePatterns { get; init; } = Array.Empty<string>();
    public BackupResourceKind Kind { get; init; }
    public bool UsesStoreUserIdWildcard { get; init; }
    public LudusaviPathSafety Safety { get; init; }
    public string SafetyWarning { get; init; } = string.Empty;
}

public enum LudusaviPathSafety
{
    Normal = 0,
    RequiresConfirmation = 1,
    Blocked = 2
}

public sealed class LudusaviPathExpressionResolver
{
    private static readonly Regex PlaceholderRegex = new(
        "<(?<name>[A-Za-z][A-Za-z0-9]*)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly LudusaviPathEnvironment _environment;

    public LudusaviPathExpressionResolver(LudusaviPathEnvironment? environment = null)
    {
        _environment = environment ?? new LudusaviPathEnvironment();
    }

    public ResolvedLudusaviResource? Resolve(
        LudusaviCompiledResource resource,
        DetectedGameInstallation? installation,
        string storeUserId)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.Kind == BackupResourceKind.Registry)
        {
            return null;
        }

        var replacements = CreateReplacements(installation, storeUserId);
        var unresolved = false;
        var usesStoreUserIdWildcard = false;
        var expansion = ExpandExpression(
            resource.Expression,
            replacements,
            ref unresolved,
            ref usesStoreUserIdWildcard);
        if (unresolved || string.IsNullOrWhiteSpace(expansion.Value))
        {
            return null;
        }

        var expanded = expansion.Value
            .Replace('/', Path.DirectorySeparatorChar);
        if (!Path.IsPathRooted(expanded))
        {
            return null;
        }

        string fixedRoot;
        string relativePattern;
        try
        {
            var prefixEnd = expansion.FirstGlobIndex < 0 ? expanded.Length : expansion.FirstGlobIndex;
            var separatorIndex = expanded.LastIndexOfAny(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                Math.Max(0, prefixEnd - 1));
            if (separatorIndex < 0)
            {
                return null;
            }

            var expandedRoot = Path.GetPathRoot(expanded);
            fixedRoot = !string.IsNullOrWhiteSpace(expandedRoot) && separatorIndex < expandedRoot.Length
                ? Path.GetFullPath(expandedRoot)
                : Path.GetFullPath(expanded[..separatorIndex]);
            relativePattern = BuildRelativePattern(
                expanded,
                separatorIndex + 1,
                expansion.LiteralGlobIndexes);
        }
        catch
        {
            return null;
        }

        if (!LudusaviGlobMatcher.IsSafeRelativePattern(relativePattern))
        {
            return null;
        }

        var includePatterns = new List<string> { relativePattern };
        if (!IsAlreadyRecursive(relativePattern))
        {
            includePatterns.Add(relativePattern.TrimEnd('/') + "/**");
        }

        try
        {
            includePatterns = BackupSourceScopePatternSet.NormalizeAndValidate(includePatterns).ToList();
        }
        catch (InvalidDataException)
        {
            return null;
        }

        var (safety, safetyWarning) = EvaluateSafety(fixedRoot, installation);

        return new ResolvedLudusaviResource
        {
            FixedRoot = fixedRoot,
            IncludePatterns = includePatterns,
            Kind = BackupResourceKind.FileSet,
            UsesStoreUserIdWildcard = usesStoreUserIdWildcard,
            Safety = safety,
            SafetyWarning = safetyWarning
        };
    }

    private static (string Value, int FirstGlobIndex, IReadOnlySet<int> LiteralGlobIndexes) ExpandExpression(
        string expression,
        IReadOnlyDictionary<string, string> replacements,
        ref bool unresolved,
        ref bool usesStoreUserIdWildcard)
    {
        var output = new StringBuilder(expression.Length + 64);
        var firstGlobIndex = -1;
        var literalGlobIndexes = new HashSet<int>();
        var sourceIndex = 0;
        foreach (Match match in PlaceholderRegex.Matches(expression))
        {
            AppendManifestLiteral(expression[sourceIndex..match.Index], output, ref firstGlobIndex);
            var key = match.Groups["name"].Value;
            if (replacements.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                // Placeholder values are literal path components. In particular, brackets in
                // an installation directory must never become manifest character classes.
                for (var index = 0; index < value.Length; index++)
                {
                    if (value[index] is '*' or '?' or '[')
                    {
                        literalGlobIndexes.Add(output.Length + index);
                    }
                }
                output.Append(value);
            }
            else if (string.Equals(key, "storeUserId", StringComparison.OrdinalIgnoreCase))
            {
                usesStoreUserIdWildcard = true;
                firstGlobIndex = firstGlobIndex < 0 ? output.Length : firstGlobIndex;
                output.Append('*');
            }
            else
            {
                unresolved = true;
            }
            sourceIndex = match.Index + match.Length;
        }
        AppendManifestLiteral(expression[sourceIndex..], output, ref firstGlobIndex);
        return (output.ToString(), firstGlobIndex, literalGlobIndexes);
    }

    private static string BuildRelativePattern(
        string expanded,
        int startIndex,
        IReadOnlySet<int> literalGlobIndexes)
    {
        var pattern = new StringBuilder(expanded.Length - startIndex + 8);
        for (var index = startIndex; index < expanded.Length; index++)
        {
            var value = expanded[index];
            if (literalGlobIndexes.Contains(index))
            {
                pattern.Append(value switch
                {
                    '[' => "[[]",
                    '*' => "[*]",
                    '?' => "[?]",
                    _ => value.ToString()
                });
            }
            else
            {
                pattern.Append(value == Path.DirectorySeparatorChar ? '/' : value);
            }
        }
        return pattern.ToString();
    }

    private static void AppendManifestLiteral(string value, StringBuilder output, ref int firstGlobIndex)
    {
        if (firstGlobIndex < 0)
        {
            var localIndex = IndexOfWildcard(value);
            if (localIndex >= 0)
            {
                firstGlobIndex = output.Length + localIndex;
            }
        }
        output.Append(value);
    }

    private (LudusaviPathSafety Safety, string Warning) EvaluateSafety(
        string fixedRoot,
        DetectedGameInstallation? installation)
    {
        string normalized;
        try
        {
            normalized = NormalizePath(fixedRoot);
        }
        catch
        {
            return (LudusaviPathSafety.Blocked, "The manifest resolved to an invalid path.");
        }

        var volumeRoot = Path.GetPathRoot(normalized);
        if (string.IsNullOrWhiteSpace(volumeRoot)
            || string.Equals(normalized, NormalizePath(volumeRoot), StringComparison.OrdinalIgnoreCase))
        {
            return (LudusaviPathSafety.Blocked, "The manifest resolved to a volume root.");
        }

        var broadRoots = new[]
        {
            _environment.Home,
            _environment.Documents,
            _environment.AppData,
            _environment.LocalAppData,
            _environment.LocalAppDataLow,
            _environment.ProgramData,
            _environment.WindowsDirectory,
            installation?.RootPath ?? string.Empty
        };
        if (broadRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Any(path => PathsEqual(normalized, path)))
        {
            return (
                LudusaviPathSafety.RequiresConfirmation,
                "This rule resolves to a broad system, user, or game-library root and requires explicit confirmation.");
        }

        return (LudusaviPathSafety.Normal, string.Empty);
    }

    private static bool IsAlreadyRecursive(string pattern) =>
        string.Equals(pattern, "**", StringComparison.Ordinal)
        || pattern.EndsWith("/**", StringComparison.Ordinal);

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizePath(string value)
    {
        var full = Path.GetFullPath(value);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length < root.Length ? root : trimmed;
    }

    private Dictionary<string, string> CreateReplacements(
        DetectedGameInstallation? installation,
        string storeUserId)
    {
        var basePath = installation?.BasePath ?? string.Empty;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["home"] = _environment.Home,
            ["winAppData"] = _environment.AppData,
            ["winLocalAppData"] = _environment.LocalAppData,
            ["winLocalAppDataLow"] = _environment.LocalAppDataLow,
            ["winDocuments"] = _environment.Documents,
            ["winPublic"] = _environment.Public,
            ["winProgramData"] = _environment.ProgramData,
            ["winDir"] = _environment.WindowsDirectory,
            ["osUserName"] = _environment.UserName,
            ["base"] = basePath,
            ["game"] = installation?.InstalledGameName ?? string.Empty,
            ["root"] = installation?.RootPath ?? string.Empty,
            ["storeGameId"] = installation?.StoreGameId ?? string.Empty,
            ["storeUserId"] = storeUserId
        };
    }

    private static int IndexOfWildcard(string value)
    {
        var indexes = new[] { value.IndexOf('*'), value.IndexOf('?'), value.IndexOf('[') }
            .Where(index => index >= 0)
            .ToList();
        return indexes.Count == 0 ? -1 : indexes.Min();
    }
}

public static class LudusaviGlobMatcher
{
    public static bool IsMatch(string relativePath, string pattern)
    {
        if (!IsSafeRelativePattern(relativePath) || !IsSafeRelativePattern(pattern))
        {
            return false;
        }

        try
        {
            return BackupSourceScopePatternSet.Compile(new[] { pattern }).IsMatch(relativePath);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public static bool IsSafeRelativePattern(string value) =>
        BackupSourceScopePatternSet.IsSafeRelativePattern(value);
}

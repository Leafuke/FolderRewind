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
        var expanded = PlaceholderRegex.Replace(resource.Expression, match =>
        {
            var key = match.Groups["name"].Value;
            if (replacements.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            unresolved = true;
            return match.Value;
        });
        if (unresolved)
        {
            return null;
        }

        expanded = Environment.ExpandEnvironmentVariables(expanded)
            .Replace('/', Path.DirectorySeparatorChar);
        if (!Path.IsPathRooted(expanded))
        {
            return null;
        }

        string fullExpression;
        try
        {
            fullExpression = Path.GetFullPath(expanded);
        }
        catch
        {
            return null;
        }

        var firstWildcard = IndexOfWildcard(fullExpression);
        string fixedRoot;
        IReadOnlyList<string> includePatterns;
        BackupResourceKind kind;
        if (firstWildcard < 0 && Directory.Exists(fullExpression))
        {
            fixedRoot = fullExpression;
            includePatterns = Array.Empty<string>();
            kind = BackupResourceKind.Directory;
        }
        else
        {
            var prefixEnd = firstWildcard < 0 ? fullExpression.Length : firstWildcard;
            var separatorIndex = fullExpression.LastIndexOfAny(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                Math.Max(0, prefixEnd - 1));
            if (separatorIndex <= 2)
            {
                return null;
            }

            fixedRoot = fullExpression[..separatorIndex];
            var relativePattern = fullExpression[(separatorIndex + 1)..]
                .Replace(Path.DirectorySeparatorChar, '/');
            if (!LudusaviGlobMatcher.IsSafeRelativePattern(relativePattern))
            {
                return null;
            }

            includePatterns = new[] { relativePattern };
            kind = BackupResourceKind.FileSet;
        }

        return new ResolvedLudusaviResource
        {
            FixedRoot = fixedRoot,
            IncludePatterns = includePatterns,
            Kind = kind
        };
    }

    private Dictionary<string, string> CreateReplacements(
        DetectedGameInstallation? installation,
        string storeUserId)
    {
        var installPath = installation?.InstallPath ?? string.Empty;
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
            ["base"] = installation?.LibraryRoot ?? string.Empty,
            ["game"] = string.IsNullOrWhiteSpace(installPath) ? string.Empty : Path.GetFileName(installPath.TrimEnd('\\', '/')),
            ["root"] = installPath,
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

        var regex = new Regex(
            "^" + ToRegex(pattern.Replace('\\', '/')) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return regex.IsMatch(relativePath.Replace('\\', '/'));
    }

    public static bool IsSafeRelativePattern(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
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
                        builder.Append(content.Replace("\\", "\\\\"));
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

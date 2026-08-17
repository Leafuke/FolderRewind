using System.Text.RegularExpressions;
using NuGet.Versioning;

namespace FolderRewind.Plugin.Runtime.Packaging;

/// <summary>Strict SemVer 2.0 parsing and precedence for Host-side package facts.</summary>
public static partial class PluginSemanticVersion
{
    public static string RequireStrict(string? value, string fieldName = "Version")
    {
        if (string.IsNullOrEmpty(value)
            || !StrictSemVerPattern().IsMatch(value)
            || !NuGetVersion.TryParse(value, out _)
            || HasLeadingZeroNumericPrereleaseIdentifier(value))
        {
            throw new InvalidDataException($"{fieldName} must be a strict SemVer 2.0.0 value.");
        }

        return value;
    }

    public static int ComparePrecedence(string left, string right)
    {
        var leftVersion = ParseStrict(left, nameof(left));
        var rightVersion = ParseStrict(right, nameof(right));
        return VersionComparer.VersionRelease.Compare(leftVersion, rightVersion);
    }

    public static bool TryComparePrecedence(string? left, string? right, out int comparison)
    {
        comparison = 0;
        if (!TryParseStrict(left, out var leftVersion)
            || !TryParseStrict(right, out var rightVersion))
        {
            return false;
        }

        comparison = VersionComparer.VersionRelease.Compare(leftVersion, rightVersion);
        return true;
    }

    private static NuGetVersion ParseStrict(string? value, string fieldName)
    {
        RequireStrict(value, fieldName);
        return NuGetVersion.Parse(value!);
    }

    private static bool TryParseStrict(string? value, out NuGetVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(value)
            || !StrictSemVerPattern().IsMatch(value)
            || HasLeadingZeroNumericPrereleaseIdentifier(value))
        {
            return false;
        }

        return NuGetVersion.TryParse(value, out version);
    }

    private static bool HasLeadingZeroNumericPrereleaseIdentifier(string value)
    {
        var dash = value.IndexOf('-');
        if (dash < 0) return false;
        var plus = value.IndexOf('+', dash + 1);
        var prerelease = plus < 0
            ? value[(dash + 1)..]
            : value[(dash + 1)..plus];
        return prerelease.Split('.').Any(identifier =>
            identifier.Length > 1
            && identifier[0] == '0'
            && identifier.All(char.IsAsciiDigit));
    }

    [GeneratedRegex(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex StrictSemVerPattern();
}

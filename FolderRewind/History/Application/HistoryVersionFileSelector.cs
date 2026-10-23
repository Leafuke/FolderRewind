using FolderRewind.History.Domain;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FolderRewind.History.Application;

/// <summary>Protocol file references identify logical Versions, never payload paths.</summary>
public static class HistoryVersionFileSelector
{
    public const string InvalidReferenceError = "[history.invalid-version-reference]";
    public const string AmbiguousReferenceError = "[history.ambiguous-version-reference]";
    public const string AmbiguousFileNameError = "[history.ambiguous-backup-name]";
    private const string VirtualPrefix = "[frv_]";
    private const string LegacyVirtualPrefix = "frv_";
    private const string TimestampFormat = "yyyy-MM-dd_HH-mm-ss";
    private const string InvalidNameCharacters = "<>:\"/\\|?*;";
    private const int ShortIdLength = 12;

    public static IReadOnlyDictionary<VersionId, string> CreateReferences(
        IEnumerable<TimelineEntrySummary> entries,
        IReadOnlyDictionary<VersionId, SourceVersion> versions,
        IReadOnlyDictionary<RepresentationId, VersionRepresentation> representations)
    {
        var items = entries.GroupBy(item => item.VersionId).Select(group => group.First()).ToArray();
        var fileOwners = items.Where(item => !string.IsNullOrEmpty(item.FileName))
            .GroupBy(item => item.FileName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.VersionId).ToHashSet(),
                StringComparer.OrdinalIgnoreCase);
        var collidingPrefixes = versions.Keys.GroupBy(id => id.ToString()[..ShortIdLength], StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var references = new Dictionary<VersionId, string>();
        foreach (var item in items)
        {
            var pluginArtifact = item.RepresentationId is { } representationId
                && representations.TryGetValue(representationId, out var representation)
                && representation.Kind == RepresentationKind.PluginArtifact;
            if (!pluginArtifact && IsSafeFileName(item.FileName) && fileOwners[item.FileName!].Count == 1)
            {
                // A literal name must not also select another version through the virtual grammar.
                if (!TryParseReference(item.FileName!, out var prefix)
                    || !items.Any(entry => entry.VersionId != item.VersionId
                        && entry.VersionId.ToString().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    references[item.VersionId] = item.FileName!;
                    continue;
                }
            }

            var version = versions[item.VersionId];
            var fullId = version.VersionId.ToString();
            var shortId = fullId[..ShortIdLength];
            var reference = FormatReference(version, item.Comment, collidingPrefixes.Contains(shortId) ? fullId : shortId);
            if (fileOwners.TryGetValue(reference, out var owners) && owners.Any(id => id != item.VersionId))
            {
                reference = FormatReference(version, item.Comment, fullId);
                if (fileOwners.TryGetValue(reference, out owners) && owners.Any(id => id != item.VersionId))
                    throw new InvalidOperationException(AmbiguousReferenceError);
            }
            references[item.VersionId] = reference;
        }
        return references;
    }

    public static VersionId? FindUnique(IEnumerable<(VersionId VersionId, string? FileName)> entries, string fileName)
    {
        var items = entries.ToArray();
        var literalMatches = items.Where(e => StringComparer.OrdinalIgnoreCase.Equals(e.FileName, fileName))
            .Select(e => e.VersionId).Distinct().Take(2).ToArray();
        if (literalMatches.Length > 1) throw new InvalidOperationException(AmbiguousFileNameError);
        if (!TryParseReference(fileName, out var idPrefix))
        {
            if (literalMatches.Length == 0 && (fileName.StartsWith(VirtualPrefix, StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith(LegacyVirtualPrefix, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(InvalidReferenceError);
            return literalMatches.Length == 1 ? literalMatches[0] : null;
        }

        var matches = literalMatches.Concat(items.Select(item => item.VersionId)
                .Where(id => id.ToString().StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase)))
            .Distinct().Take(2).ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(AmbiguousReferenceError)
        };
    }

    private static bool TryParseReference(string value, out string idPrefix)
    {
        idPrefix = string.Empty;
        if (!IsSafeFileName(value)) return false;
        if (!value.StartsWith(VirtualPrefix, StringComparison.OrdinalIgnoreCase))
            return TryParseLegacyReference(value, out idPrefix);

        // Match the existing mod's [type][timestamp]body [comment].7z/zip metadata grammar.
        const int timestampStart = 7;
        const int bodyStart = 27;
        var extensionLength = value.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ? 3
            : value.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? 4 : 0;
        if (extensionLength == 0 || value.Length < bodyStart + ShortIdLength + extensionLength
            || value[6] != '[' || value[26] != ']'
            || !DateTime.TryParseExact(value.AsSpan(timestampStart, 19), TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _)) return false;

        var body = value.AsSpan(bodyStart, value.Length - bodyStart - extensionLength);
        var commentStart = body.IndexOf(" [", StringComparison.Ordinal);
        if (commentStart >= 0)
        {
            if (body[^1] != ']' || body.Slice(commentStart + 2, body.Length - commentStart - 3).IndexOfAny('[', ']') >= 0)
                return false;
            body = body[..commentStart];
        }
        return TryReadVersionId(body, out idPrefix);
    }

    private static bool TryParseLegacyReference(string value, out string idPrefix)
    {
        idPrefix = string.Empty;
        // frv_[timestamp]comment_[id]; only the final ID determines identity.
        if (value.Length < 40 || !value.StartsWith("frv_[", StringComparison.OrdinalIgnoreCase)
            || value[24] != ']' || !value.EndsWith(']') || !IsSafeFileName(value)
            || !DateTime.TryParseExact(value.AsSpan(5, 19), TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _)) return false;
        var separator = value.LastIndexOf("_[", StringComparison.Ordinal);
        if (separator < 25 || value.AsSpan(25, separator - 25).IndexOfAny('[', ']') >= 0) return false;
        var id = value.AsSpan(separator + 2, value.Length - separator - 3);
        return TryReadVersionId(id, out idPrefix);
    }

    private static bool TryReadVersionId(ReadOnlySpan<char> id, out string idPrefix)
    {
        idPrefix = string.Empty;
        if (id.Length is not ShortIdLength and not 32) return false;
        foreach (var character in id)
            if (!char.IsAsciiHexDigit(character)) return false;
        idPrefix = id.ToString();
        return true;
    }

    private static string FormatReference(SourceVersion version, string comment, string id)
    {
        var sanitizedComment = SanitizeComment(comment);
        var commentPart = sanitizedComment.Length == 0 ? string.Empty : $" [{sanitizedComment}]";
        return $"{VirtualPrefix}[{version.CreatedAtUtc.ToLocalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture)}]{id}{commentPart}.7z";
    }

    private static bool IsSafeFileName(string? value)
        => !string.IsNullOrWhiteSpace(value) && value is not "." and not ".."
            && value == value.Trim() && !value.EndsWith('.')
            && !value.Any(character => char.IsControl(character) || InvalidNameCharacters.Contains(character));

    private static string SanitizeComment(string comment)
    {
        var result = new StringBuilder();
        var count = 0;
        var pendingSpace = false;
        foreach (var original in comment.EnumerateRunes())
        {
            if (original.Value is '[' or ']') continue;
            var rune = Rune.IsControl(original) || (original.IsAscii && InvalidNameCharacters.Contains((char)original.Value))
                ? new Rune('_') : original;
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace && count < 64)
            {
                result.Append(' ');
                count++;
            }
            if (count == 64) break;
            result.Append(rune.ToString());
            count++;
            pendingSpace = false;
        }
        return result.ToString().TrimEnd();
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FolderRewind.Services;

/// <summary>Exact file or subtree selectors, never glob rules. A trailing slash selects a subtree.</summary>
internal static class RestorePreservePaths
{
    public static string[] Normalize(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var raw in paths)
        {
            var path = raw.Trim().Replace('\\', '/');
            if (path.Length == 0 || path.Length > 512 || path.StartsWith('/')
                || path.IndexOfAny([':', ',', '*', '?', '"', '<', '>', '|']) >= 0
                || path.Any(char.IsControl)) throw new InvalidDataException("Invalid preserve path.");
            var segments = path.TrimEnd('/').Split('/');
            if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."
                || segment.EndsWith('.') || segment.EndsWith(' ')))
                throw new InvalidDataException("Preserve paths must be relative, canonical files or directories.");
            if (result.Any(existing => StringComparer.OrdinalIgnoreCase.Equals(existing, path)
                || existing.EndsWith('/') && path.StartsWith(existing, StringComparison.OrdinalIgnoreCase))) continue;
            if (path.EndsWith('/')) result.RemoveAll(existing => existing.StartsWith(path, StringComparison.OrdinalIgnoreCase));
            result.Add(path);
            if (result.Count > 16) throw new InvalidDataException("Too many preserve paths.");
        }
        return result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool Matches(string selector, string path)
        => selector.EndsWith('/') ? path.StartsWith(selector, StringComparison.OrdinalIgnoreCase)
            : StringComparer.OrdinalIgnoreCase.Equals(selector, path);
}

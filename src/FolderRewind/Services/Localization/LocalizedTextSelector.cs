using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

/// <summary>Language matching without WinRT; a default is preferable to an unrelated translation.</summary>
internal static class LocalizedTextSelector
{
    internal static string EffectiveLanguage(string? language)
        => (language ?? "").Replace('_', '-').StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";

    internal static string? Select(IReadOnlyDictionary<string, string>? values, string? fallback, string language)
    {
        var tag = language.Trim().Replace('_', '-');
        var translations = values?.Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .OrderBy(p => p.Key, StringComparer.Ordinal).ToArray() ?? [];
        string? Find(string key) => translations.FirstOrDefault(p =>
            string.Equals(p.Key.Replace('_', '-'), key, StringComparison.OrdinalIgnoreCase)).Value;
        var exact = Find(tag);
        if (exact is not null) return exact;
        var prefix = tag.Split('-')[0];
        var neutral = Find(prefix);
        if (neutral is not null) return neutral;
        if (prefix.Equals("zh", StringComparison.OrdinalIgnoreCase))
        {
            var traditional = tag.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) || tag.Equals("zh-HK", StringComparison.OrdinalIgnoreCase);
            var alias = Find(traditional ? "zh-TW" : "zh-CN") ?? Find(traditional ? "zh-Hant" : "zh-Hans");
            if (alias is not null) return alias;
        }
        else
        {
            var related = translations.FirstOrDefault(p => p.Key.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)).Value;
            if (related is not null) return related;
        }
        return !string.IsNullOrWhiteSpace(fallback) ? fallback : Find("en-US") ?? Find("en") ?? translations.FirstOrDefault().Value;
    }
}

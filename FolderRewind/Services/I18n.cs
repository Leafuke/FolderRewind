using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Windows.ApplicationModel.Resources;
using Windows.Globalization;

namespace FolderRewind.Services
{
    public static class I18n
    {
        private static readonly ResourceLoader _rl = ResourceLoader.GetForViewIndependentUse();

        public static string GetString(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return string.Empty;

            try
            {
                var value = _rl.GetString(key);
                return string.IsNullOrWhiteSpace(value) ? key : value;
            }
            catch
            {
                return key;
            }
        }

        public static string Format(string key, params object[] args)
        {
            var fmt = GetString(key);
            if (args == null || args.Length == 0) return fmt;

            try
            {
                return string.Format(CultureInfo.CurrentCulture, fmt, args);
            }
            catch
            {
                return fmt;
            }
        }

        /// <summary>
        /// 从多语言字典中选择最符合当前语言环境的值。
        /// Key 期望形如: "en-US" / "en" / "zh-CN"。
        /// </summary>
        public static string? PickBest(IReadOnlyDictionary<string, string>? localized, string? fallback)
        {
            if (localized == null || localized.Count == 0)
            {
                return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
            }

            foreach (var lang in GetLanguageCandidates())
            {
                if (string.IsNullOrWhiteSpace(lang)) continue;

                if (localized.TryGetValue(lang, out var exact) && !string.IsNullOrWhiteSpace(exact))
                {
                    return exact;
                }

                // 尝试语言前缀（en-US -> en）
                var dash = lang.IndexOf('-', StringComparison.Ordinal);
                if (dash > 0)
                {
                    var prefix = lang[..dash];
                    if (localized.TryGetValue(prefix, out var pref) && !string.IsNullOrWhiteSpace(pref))
                    {
                        return pref;
                    }
                }
            }

            // 兜底
            var first = localized.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (!string.IsNullOrWhiteSpace(first)) return first;

            return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
        }

        private static IEnumerable<string> GetLanguageCandidates()
        {
            var result = new List<string>();

            // App override（设置页会写入 PrimaryLanguageOverride）
            var primary = ApplicationLanguages.PrimaryLanguageOverride;
            if (!string.IsNullOrWhiteSpace(primary))
            {
                result.Add(NormalizeTag(primary));
            }

            try
            {
                foreach (var l in ApplicationLanguages.Languages)
                {
                    if (!string.IsNullOrWhiteSpace(l)) result.Add(NormalizeTag(l));
                }
            }
            catch
            {

            }

            try
            {
                var c = CultureInfo.CurrentUICulture?.Name;
                if (!string.IsNullOrWhiteSpace(c)) result.Add(NormalizeTag(c));
            }
            catch
            {

            }

            // 兜底
            result.Add("en-US");
            result.Add("zh-CN");

            return result
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string NormalizeTag(string tag)
        {
            return tag.Trim().Replace('_', '-');
        }
    }
}

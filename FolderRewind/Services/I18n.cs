using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Windows.ApplicationModel.Resources.Core;
using Windows.Globalization;
using ResourceLoader = FolderRewind.Services.AppResourceLoader;

namespace FolderRewind.Services
{
    public static class I18n
    {
        private static readonly ResourceLoader _rl = ResourceLoader.GetForViewIndependentUse();
        private static readonly CultureInfo _startupCulture = CultureInfo.CurrentCulture;
        private static readonly CultureInfo _startupUiCulture = CultureInfo.CurrentUICulture;
        private static string _languageOverride = string.Empty;

        public static void SetLanguageOverride(string? language)
        {
            var requestedOverride = LanguageSettingPolicy.ToOverride(language);

            try
            {
                ApplyPlatformLanguageOverride(requestedOverride);
                SetEffectiveLanguageOverride(requestedOverride);
            }
            catch (Exception ex)
            {
                Exception? resetException = null;
                try
                {
                    ApplyPlatformLanguageOverride(string.Empty);
                }
                catch (Exception resetEx)
                {
                    resetException = resetEx;
                }

                SetEffectiveLanguageOverride(string.Empty);

                var resetDetail = resetException == null
                    ? string.Empty
                    : $" Reset also failed: {resetException.Message}";
                LogService.LogWarning(
                    $"[I18n] Failed to apply language override; falling back to system language: {ex.Message}.{resetDetail}",
                    "I18n");
            }
        }

        private static void ApplyPlatformLanguageOverride(string languageOverride)
        {
            if (AppRuntimeInfo.IsPackaged)
            {
                ApplicationLanguages.PrimaryLanguageOverride = languageOverride;
                if (string.IsNullOrWhiteSpace(languageOverride))
                {
                    ResourceContext.ResetGlobalQualifierValues(new[] { "Language" });
                }
                else
                {
                    ResourceContext.SetGlobalQualifierValue("Language", languageOverride);
                }
            }

            var uiCulture = string.IsNullOrWhiteSpace(languageOverride)
                ? _startupUiCulture
                : CultureInfo.GetCultureInfo(languageOverride);
            var culture = string.IsNullOrWhiteSpace(languageOverride)
                ? _startupCulture
                : uiCulture;

            CultureInfo.CurrentUICulture = uiCulture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = uiCulture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
        }

        private static void SetEffectiveLanguageOverride(string languageOverride)
        {
            _languageOverride = languageOverride;
            AppResourceLoader.SetLanguageOverride(languageOverride);
        }

        public static string GetCurrentUiLanguage()
        {
            if (!string.IsNullOrWhiteSpace(_languageOverride))
            {
                return _languageOverride;
            }

            if (AppRuntimeInfo.IsPackaged)
            {
                try
                {
                    var packagedOverride = ApplicationLanguages.PrimaryLanguageOverride;
                    if (!string.IsNullOrWhiteSpace(packagedOverride))
                    {
                        return packagedOverride;
                    }
                }
                catch
                {
                }
            }

            return CultureInfo.CurrentUICulture.Name;
        }

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

            string? GetValue(string tag)
            {
                if (string.IsNullOrWhiteSpace(tag)) return null;

                if (localized.TryGetValue(tag, out var exact) && !string.IsNullOrWhiteSpace(exact))
                {
                    return exact;
                }

                foreach (var kv in localized)
                {
                    if (string.Equals(NormalizeTag(kv.Key), tag, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(kv.Value))
                    {
                        return kv.Value;
                    }
                }

                return null;
            }

            foreach (var lang in GetLanguageCandidates())
            {
                if (string.IsNullOrWhiteSpace(lang)) continue;

                var exact = GetValue(lang);
                if (!string.IsNullOrWhiteSpace(exact))
                {
                    return exact;
                }

                // 尝试语言前缀（en-US -> en）
                var dash = lang.IndexOf('-', StringComparison.Ordinal);
                if (dash > 0)
                {
                    var prefix = lang[..dash];
                    var pref = GetValue(prefix);
                    if (!string.IsNullOrWhiteSpace(pref))
                    {
                        return pref;
                    }
                }

                // 脚本/地区互通：zh-Hans <-> zh-CN, zh-Hant <-> zh-TW
                if (lang.StartsWith("zh-", StringComparison.OrdinalIgnoreCase))
                {
                    if (lang.Contains("Hans", StringComparison.OrdinalIgnoreCase))
                    {
                        var zhCn = GetValue("zh-CN");
                        if (!string.IsNullOrWhiteSpace(zhCn)) return zhCn;
                    }
                    else if (lang.Contains("Hant", StringComparison.OrdinalIgnoreCase))
                    {
                        var zhTw = GetValue("zh-TW");
                        if (!string.IsNullOrWhiteSpace(zhTw)) return zhTw;
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
            var primary = GetCurrentUiLanguage();
            if (!string.IsNullOrWhiteSpace(primary))
            {
                result.Add(NormalizeTag(primary));
            }

            if (AppRuntimeInfo.IsPackaged)
            {
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

using System;
using System.Collections.Generic;
using System.Globalization;
using Windows.ApplicationModel.Resources.Core;
using ApplicationLanguages = Microsoft.Windows.Globalization.ApplicationLanguages;
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
            // MRT Core owns XAML x:Uid lookup in both packaged and unpackaged WinUI.
            // Updating only our ResourceLoader's context leaves XAML in the system language.
            ApplicationLanguages.PrimaryLanguageOverride = LocalizedTextSelector.EffectiveLanguage(
                string.IsNullOrWhiteSpace(languageOverride) ? _startupUiCulture.Name : languageOverride);
            if (AppRuntimeInfo.IsPackaged)
            {
                if (string.IsNullOrWhiteSpace(languageOverride))
                {
                    ResourceContext.SetGlobalQualifierValue("Language", LocalizedTextSelector.EffectiveLanguage(_startupUiCulture.Name));
                }
                else
                {
                    ResourceContext.SetGlobalQualifierValue("Language", languageOverride);
                }
            }

            var uiCulture = CultureInfo.GetCultureInfo(LocalizedTextSelector.EffectiveLanguage(
                string.IsNullOrWhiteSpace(languageOverride) ? _startupUiCulture.Name : languageOverride));
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
            AppResourceLoader.SetLanguageOverride(LocalizedTextSelector.EffectiveLanguage(
                string.IsNullOrWhiteSpace(languageOverride) ? _startupUiCulture.Name : languageOverride));
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

            return LocalizedTextSelector.EffectiveLanguage(CultureInfo.CurrentUICulture.Name);
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
            return LocalizedTextSelector.Select(localized, fallback, GetCurrentUiLanguage());
        }

    }
}

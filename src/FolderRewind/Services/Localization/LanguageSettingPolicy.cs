using System;

namespace FolderRewind.Services
{
    /// <summary>
    /// Canonicalizes the persisted UI language setting and prevents unsupported
    /// values from reaching WinRT globalization APIs.
    /// </summary>
    internal static class LanguageSettingPolicy
    {
        internal const string System = "system";
        internal const string English = "en-US";
        internal const string Chinese = "zh-CN";

        internal static string Normalize(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return System;
            }

            var value = language.Trim().Replace('_', '-');
            if (string.Equals(value, System, StringComparison.OrdinalIgnoreCase))
            {
                return System;
            }

            if (string.Equals(value, "en", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, English, StringComparison.OrdinalIgnoreCase))
            {
                return English;
            }

            if (string.Equals(value, "zh", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, Chinese, StringComparison.OrdinalIgnoreCase))
            {
                return Chinese;
            }

            return System;
        }

        internal static string ToOverride(string? language)
        {
            var normalized = Normalize(language);
            return string.Equals(normalized, System, StringComparison.Ordinal)
                ? string.Empty
                : normalized;
        }

        internal static int ToSelectionIndex(string? language)
        {
            return Normalize(language) switch
            {
                English => 1,
                Chinese => 2,
                _ => 0,
            };
        }

        internal static string FromSelectionIndex(int index)
        {
            return index switch
            {
                1 => English,
                2 => Chinese,
                _ => System,
            };
        }

        internal static bool IsChinese(string? language)
        {
            return string.Equals(Normalize(language), Chinese, StringComparison.Ordinal);
        }
    }
}

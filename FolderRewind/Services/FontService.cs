using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Windows.System.UserProfile;

namespace FolderRewind.Services
{
    public static class FontService
    {
        private static readonly Regex FontSuffixRegex = new(@"\s*\(.*\)\s*$", RegexOptions.Compiled);

        public static IReadOnlyList<string> GetInstalledFontFamilies()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void ReadFontsKey(RegistryKey? key)
            {
                if (key == null) return;
                try
                {
                    foreach (var valueName in key.GetValueNames())
                    {
                        var name = NormalizeRegistryFontName(valueName);
                        if (!string.IsNullOrWhiteSpace(name)) set.Add(name);
                    }
                }
                catch
                {
                    
                }
            }

            // 系统级字体
            ReadFontsKey(Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"));
            ReadFontsKey(Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Fonts"));

            // 用户级字体（Windows 10+）
            ReadFontsKey(Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"));

            // 兜底
            set.Add("Segoe UI Variable");
            set.Add("Segoe UI");
            set.Add("Microsoft YaHei");
            set.Add("Microsoft YaHei UI");

            return set
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string GetRecommendedDefaultFontFamily()
        {
            var installed = GetInstalledFontFamilies();

            if (IsChinesePreferred())
            {
                // 优先 UI 字体，其次普通微软雅黑。
                var yaheiUi = installed.FirstOrDefault(f => string.Equals(f, "Microsoft YaHei UI", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(yaheiUi)) return yaheiUi;

                var yahei = installed.FirstOrDefault(f => string.Equals(f, "Microsoft YaHei", StringComparison.OrdinalIgnoreCase)
                                                         || string.Equals(f, "微软雅黑", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(yahei)) return yahei;
            }

            var segVar = installed.FirstOrDefault(f => string.Equals(f, "Segoe UI Variable", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(segVar)) return segVar;

            return "Segoe UI";
        }

        private static bool IsChinesePreferred()
        {
            try
            {
                var langs = GlobalizationPreferences.Languages;
                if (langs != null)
                {
                    foreach (var l in langs)
                    {
                        if (string.IsNullOrWhiteSpace(l)) continue;
                        if (l.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static string NormalizeRegistryFontName(string valueName)
        {
            if (string.IsNullOrWhiteSpace(valueName)) return string.Empty;
            var name = valueName.Trim();

            // 去掉 (TrueType) 等后缀
            name = FontSuffixRegex.Replace(name, string.Empty).Trim();

            // 一些项可能形如 "Microsoft YaHei & Microsoft YaHei UI"，这里不做拆分，保持可选。
            return name;
        }
    }
}

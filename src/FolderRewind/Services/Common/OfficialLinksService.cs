using System;

namespace FolderRewind.Services
{
    internal static class OfficialLinksService
    {
        private const string ChineseOfficialWebsiteUrl = "https://folderrewind.top/";
        private const string EnglishOfficialWebsiteUrl = "https://folderrewind.top/en/";
        private const string ChineseCloudGuideUrl = "https://folderrewind.top/docs/guides/cloud-archive";
        private const string EnglishCloudGuideUrl = "https://folderrewind.top/en/docs/guides/cloud-archive";
        private const string IntroVideoUrl = "https://www.bilibili.com/video/BV1zbcjzhE1y";

        public static string GetOfficialWebsiteUrl()
        {
            return IsChineseUi() ? ChineseOfficialWebsiteUrl : EnglishOfficialWebsiteUrl;
        }

        public static string GetCloudGuideUrl()
        {
            return IsChineseUi() ? ChineseCloudGuideUrl : EnglishCloudGuideUrl;
        }

        public static string GetFirstLaunchGuideUrl()
        {
            return IsChineseUi() ? IntroVideoUrl : GetOfficialWebsiteUrl();
        }

        public static bool IsChineseUi()
        {
            try
            {
                var language = I18n.GetCurrentUiLanguage();

                return !string.IsNullOrWhiteSpace(language)
                    && language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return true;
            }
        }
    }
}

using System;

namespace FolderRewind.Services
{
    /// <summary>
    /// Shell 背景图片绑定所需的最小状态快照。
    /// </summary>
    public readonly record struct SponsorBackgroundImageState(
        string Path,
        bool IsEnabled,
        bool IsSponsorUnlocked,
        bool FileExists)
    {
        public bool ShouldDisplay => IsEnabled
            && IsSponsorUnlocked
            && !string.IsNullOrWhiteSpace(Path)
            && FileExists;
    }

    public static class SponsorBackgroundImageCachePolicy
    {
        public static bool ShouldReload(
            SponsorBackgroundImageState previous,
            SponsorBackgroundImageState current,
            bool hasCachedImage,
            bool forceReload)
        {
            if (!current.ShouldDisplay)
            {
                return false;
            }

            return forceReload
                || !previous.Equals(current)
                || !hasCachedImage;
        }

        public static bool ShouldClear(SponsorBackgroundImageState current)
        {
            return !current.ShouldDisplay;
        }

        public static bool IsCurrentLoad(
            int requestVersion,
            int currentVersion,
            bool isDisposed)
        {
            return !isDisposed && requestVersion == currentVersion;
        }
    }
}

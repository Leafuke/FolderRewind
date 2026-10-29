using System.Text.RegularExpressions;

namespace FolderRewind.Services
{
    internal static class KnotLinkReleaseAssetPolicy
    {
        private static readonly Regex WindowsX86InstallerRegex = new(
            @"^KnotLinkService-\d+(?:\.\d+){3}-windows-x86-Installer\.exe$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool IsWindowsX86Installer(string assetName) =>
            !string.IsNullOrWhiteSpace(assetName)
            && WindowsX86InstallerRegex.IsMatch(assetName);
    }
}

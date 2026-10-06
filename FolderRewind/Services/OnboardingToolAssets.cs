using System;
using System.Runtime.InteropServices;

namespace FolderRewind.Services;

internal sealed record OnboardingToolAsset(string Tool, string Version, Architecture Architecture, string Url, string Sha256)
{
    internal string FileName => System.IO.Path.GetFileName(new Uri(Url).AbsolutePath);
}

internal static class OnboardingToolAssets
{
    internal static OnboardingToolAsset Get(string tool, Architecture osArchitecture)
    {
        var (version, repository, fileName, hash) = (tool, osArchitecture) switch
        {
            ("rclone", Architecture.X64) => ("v1.75.1", "rclone/rclone", "rclone-v1.75.1-windows-amd64.zip", "200eb602c126d82aa38b51e0f6b9ae837473ff99b51278d3f6f837574c494d6e"),
            ("rclone", Architecture.Arm64) => ("v1.75.1", "rclone/rclone", "rclone-v1.75.1-windows-arm64.zip", "c3c6cd0424dd49076ad179c30c3f9e5cde2c004ec07ea9fe6911f23e32eafe0f"),
            ("openlist", Architecture.X64) => ("v4.2.6", "OpenListTeam/OpenList", "openlist-windows-amd64.zip", "10d24913f86843e347eefac219c61224628bbd5d3c7443b2ee119c168a8cb3b9"),
            ("openlist", Architecture.Arm64) => ("v4.2.6", "OpenListTeam/OpenList", "openlist-windows-arm64.zip", "e866c455b63a50992fa720be227f8a3241c7236d90523494a165fe512cc395db"),
            _ => throw new PlatformNotSupportedException(I18n.GetString("Onboarding_ToolPlatformUnsupported"))
        };
        return new(tool, version, osArchitecture, $"https://github.com/{repository}/releases/download/{version}/{fileName}", hash);
    }
}

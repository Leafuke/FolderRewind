using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Windows.ApplicationModel;

namespace FolderRewind.Services
{
    /// <summary>
    /// 集中处理不同发行形态的运行时信息，避免各功能散落依赖 Package.Current。
    /// </summary>
    internal static class AppRuntimeInfo
    {
        private const string DistributionChannelMetadataName = "FolderRewindDistributionChannel";

        public static bool IsMsiDistribution => string.Equals(
            GetAssemblyMetadata(DistributionChannelMetadataName),
            "Msi",
            StringComparison.OrdinalIgnoreCase);

        public static bool IsPackaged
        {
            get
            {
                try
                {
                    return Package.Current != null;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static string ApplicationBaseDirectory
        {
            get
            {
                if (IsPackaged)
                {
                    try
                    {
                        var installedPath = Package.Current.InstalledLocation.Path;
                        if (!string.IsNullOrWhiteSpace(installedPath))
                        {
                            return installedPath;
                        }
                    }
                    catch
                    {
                    }
                }

                return AppContext.BaseDirectory;
            }
        }

        public static string ExecutablePath
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
                {
                    return Environment.ProcessPath;
                }

                return Path.Combine(ApplicationBaseDirectory, "FolderRewind.exe");
            }
        }

        public static Version? GetAssemblyVersion()
        {
            try
            {
                var version = typeof(AppRuntimeInfo).Assembly.GetName().Version;
                return version == null ? null : NormalizeVersion(version);
            }
            catch
            {
                return null;
            }
        }

        private static string? GetAssemblyMetadata(string name)
        {
            try
            {
                return typeof(AppRuntimeInfo).Assembly
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(attribute => string.Equals(attribute.Key, name, StringComparison.Ordinal))
                    ?.Value;
            }
            catch
            {
                return null;
            }
        }

        private static Version NormalizeVersion(Version version)
        {
            return new Version(
                Math.Max(0, version.Major),
                Math.Max(0, version.Minor),
                version.Build < 0 ? 0 : version.Build,
                version.Revision < 0 ? 0 : version.Revision);
        }
    }
}

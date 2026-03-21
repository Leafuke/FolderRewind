using System;
using Windows.ApplicationModel;

namespace FolderRewind.Services
{
    internal enum InstallChannel
    {
        Unknown = 0,
        Store = 1,
        Sideload = 2,
        Developer = 3
    }

    internal static class AppDistributionService
    {
        private const string StoreProductUrl = "https://apps.microsoft.com/detail/9nwsdgxdqws4?referrer=appbadge&mode=direct";

        public static string MicrosoftStoreProductUrl => StoreProductUrl;

        public static InstallChannel GetCurrentChannel()
        {
            try
            {
                var package = Package.Current;
                if (package == null)
                {
                    return InstallChannel.Unknown;
                }

                try
                {
                    return MapSignatureKind(package.SignatureKind);
                }
                catch
                {
                    // 某些运行时投影可能不暴露 SignatureKind，回退到反射读取。
                    var signatureKind = package.GetType()
                        .GetProperty("SignatureKind")
                        ?.GetValue(package)
                        ?.ToString();

                    return MapSignatureKindName(signatureKind);
                }
            }
            catch
            {
                return InstallChannel.Unknown;
            }
        }

        private static InstallChannel MapSignatureKind(PackageSignatureKind signatureKind)
        {
            return signatureKind switch
            {
                PackageSignatureKind.Store => InstallChannel.Store,
                PackageSignatureKind.Developer => InstallChannel.Developer,
                PackageSignatureKind.Enterprise => InstallChannel.Sideload,
                PackageSignatureKind.System => InstallChannel.Sideload,
                _ => InstallChannel.Unknown
            };
        }

        private static InstallChannel MapSignatureKindName(string? signatureKind)
        {
            if (string.IsNullOrWhiteSpace(signatureKind))
            {
                return InstallChannel.Unknown;
            }

            if (string.Equals(signatureKind, "Store", StringComparison.OrdinalIgnoreCase))
            {
                return InstallChannel.Store;
            }

            if (string.Equals(signatureKind, "Developer", StringComparison.OrdinalIgnoreCase))
            {
                return InstallChannel.Developer;
            }

            if (string.Equals(signatureKind, "Enterprise", StringComparison.OrdinalIgnoreCase)
                || string.Equals(signatureKind, "System", StringComparison.OrdinalIgnoreCase))
            {
                return InstallChannel.Sideload;
            }

            return InstallChannel.Unknown;
        }
    }
}

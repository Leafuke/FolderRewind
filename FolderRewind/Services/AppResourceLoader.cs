using System;
using MrtCoreResourceManager = Microsoft.Windows.ApplicationModel.Resources.ResourceManager;
using MrtCoreResourceMap = Microsoft.Windows.ApplicationModel.Resources.ResourceMap;
using MrtCoreResourceLoader = Microsoft.Windows.ApplicationModel.Resources.ResourceLoader;
using PackagedResourceLoader = Windows.ApplicationModel.Resources.ResourceLoader;

namespace FolderRewind.Services
{
    /// <summary>
    /// Resource loader that works with both Windows App SDK unpackaged/MSI builds and
    /// packaged MSIX builds. New code should not access the package-only loader directly.
    /// </summary>
    public sealed class AppResourceLoader
    {
        private readonly MrtCoreResourceLoader? _mrtCoreLoader;
        private readonly MrtCoreResourceManager? _mrtCoreManager;
        private readonly MrtCoreResourceMap? _mrtCoreResourceMap;
        private readonly PackagedResourceLoader? _packagedLoader;
        private static string _languageOverride = string.Empty;

        private AppResourceLoader()
        {
            try
            {
                _mrtCoreLoader = new MrtCoreResourceLoader();
                _mrtCoreManager = new MrtCoreResourceManager();
                _mrtCoreResourceMap = _mrtCoreManager.MainResourceMap.GetSubtree("Resources");
            }
            catch
            {
            }

            if (AppRuntimeInfo.IsPackaged)
            {
                try
                {
                    _packagedLoader = PackagedResourceLoader.GetForViewIndependentUse();
                }
                catch
                {
                }
            }
        }

        public static AppResourceLoader GetForViewIndependentUse()
        {
            return new AppResourceLoader();
        }

        internal static void SetLanguageOverride(string? language)
        {
            _languageOverride = language?.Trim() ?? string.Empty;
        }

        public string GetString(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            if (_mrtCoreManager != null && _mrtCoreResourceMap != null)
            {
                try
                {
                    var context = _mrtCoreManager.CreateResourceContext();
                    if (!string.IsNullOrWhiteSpace(_languageOverride))
                    {
                        context.QualifierValues["Language"] = _languageOverride;
                    }
                    var candidate = _mrtCoreResourceMap.TryGetValue(key, context);
                    if (!string.IsNullOrWhiteSpace(candidate?.ValueAsString))
                    {
                        return candidate.ValueAsString;
                    }
                }
                catch
                {
                }
            }

            try
            {
                var value = _mrtCoreLoader?.GetString(key);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            catch
            {
            }

            try
            {
                return _packagedLoader?.GetString(key) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}

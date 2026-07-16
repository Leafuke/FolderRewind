using FolderRewind.Services.Plugins;

namespace FolderRewind.Services
{
    internal static class LogService
    {
        public static void LogWarning(string message, string source)
        {
        }
    }
}

namespace FolderRewind.Services.Plugins
{
    internal static class PluginService
    {
        internal static IReadOnlyList<(string PluginId, PluginKnotLinkCapabilityContribution Contribution)>
            GetKnotLinkCapabilityContributions() =>
            Array.Empty<(string, PluginKnotLinkCapabilityContribution)>();
    }
}

using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;

namespace FolderRewind.Services
{
    internal static class LogService
    {
        public static void LogWarning(string message, string source)
        {
        }
    }
}

namespace FolderRewind.Services.Plugins.V3
{
    internal static class PluginV3RuntimeService
    {
        internal static PluginRuntimeManager Runtime { get; } = new(safeMode: false);

        internal static IReadOnlyList<PluginId> GetActivePlugins() => Array.Empty<PluginId>();
    }
}

using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;

namespace FolderRewind.Services
{
    internal static class LogService
    {
        public static void Log(string message, FolderRewind.Models.LogLevel level = FolderRewind.Models.LogLevel.Info, string? source = null, Exception? exception = null) { }
        public static void LogWarning(string message, string source)
        {
        }
        public static void LogError(string message, string source, Exception error) { }
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

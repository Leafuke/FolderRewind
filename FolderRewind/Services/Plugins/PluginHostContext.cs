using FolderRewind.Services.KnotLink;
using System;
using System.Threading.Tasks;

namespace FolderRewind.Services.Plugins
{
    public sealed class PluginHostContext
    {
        public string PluginId { get; }
        public string PluginName { get; }

        private PluginHostContext(string pluginId, string pluginName)
        {
            PluginId = pluginId;
            PluginName = pluginName;
        }

        public bool IsKnotLinkAvailable => Services.KnotLinkService.IsEnabled && Services.KnotLinkService.IsInitialized;

        public void BroadcastEvent(string eventData)
        {
            Services.KnotLinkService.BroadcastEvent(eventData);
        }

        public Task<string> QueryKnotLinkAsync(string question, int timeoutMs = 5000)
        {
            return Services.KnotLinkService.QueryAsync(question, timeoutMs);
        }

        public static PluginHostContext CreateForCurrentApp(string pluginId, string pluginName)
        {
            return new PluginHostContext(pluginId ?? string.Empty, pluginName ?? string.Empty);
        }
    }
}

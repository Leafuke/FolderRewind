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

        public bool IsKnotLinkSenderReady => Services.KnotLinkService.IsSenderRunning;

        public bool IsKnotLinkResponserReady => Services.KnotLinkService.IsResponserRunning;

        public void BroadcastEvent(string eventData)
        {
            Services.KnotLinkService.BroadcastEvent(eventData);
        }

        public Task BroadcastEventAsync(string eventData)
        {
            return Services.KnotLinkService.BroadcastEventAsync(eventData);
        }

        public Task<string> QueryKnotLinkAsync(string question, int timeoutMs = 5000)
        {
            return Services.KnotLinkService.QueryAsync(question, timeoutMs);
        }

        public IDisposable? SubscribeSignal(string signalId, Func<string, Task> onSignal)
        {
            return Services.KnotLinkService.SubscribeSignal(signalId, onSignal);
        }

        public void SendKnotLinkCommand(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Services.KnotLinkService.QueryAsync(message, 5000).ConfigureAwait(false);
                }
                catch
                {
                }
            });
        }

        public void LogInfo(string message)
        {
            try { Services.LogService.LogInfo(message ?? string.Empty, PluginName); } catch { }
        }

        public void LogWarning(string message)
        {
            try { Services.LogService.LogWarning(message ?? string.Empty, PluginName); } catch { }
        }

        public void LogError(string message, Exception? ex = null)
        {
            try { Services.LogService.LogError(message ?? string.Empty, PluginName, ex); } catch { }
        }

        public static PluginHostContext CreateForCurrentApp(string pluginId, string pluginName)
        {
            return new PluginHostContext(pluginId ?? string.Empty, pluginName ?? string.Empty);
        }
    }
}

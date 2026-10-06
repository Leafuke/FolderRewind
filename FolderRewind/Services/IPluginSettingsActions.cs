using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal enum PluginSettingsAction
{
    OpenPluginStore,
    ManualInstallPlugin,
    OpenPluginFolder,
    RefreshPlugins,
    RestartSafeMode,
    PluginUninstall,
    PluginDeleteData,
    CheckPluginUpdates,
    PluginUpdate,
    PluginSettings,
    KnotLinkRestart,
    KnotLinkTest,
    KnotLinkSendCustom,
    KnotLinkStartServer,
    KnotLinkCheckServerUpdate,
    KnotLinkUpdateServer,
    RefreshOnExpand, SetPluginEnabled, ToggleKnotLink
}

internal sealed record PluginSettingsRequest(PluginSettingsAction Action, object? Parameter = null);
internal sealed record PluginEnabledEdit(string PluginId, bool Enabled);

internal interface IPluginSettingsActions
{
    Task ExecuteAsync(PluginSettingsRequest request, CancellationToken token);
}

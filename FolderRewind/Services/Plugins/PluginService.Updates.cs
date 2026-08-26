using FolderRewind.Models;
using FolderRewind.Plugin.Runtime.Packaging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ResourceLoader = FolderRewind.Services.AppResourceLoader;

namespace FolderRewind.Services.Plugins;

public static partial class PluginService
{
    private static readonly ResourceLoader UpdateResources = ResourceLoader.GetForViewIndependentUse();

    /// <summary>
    /// v3 更新只信任 Official Catalog 中经过 URL、SHA-256 和 Manifest 绑定校验的条目。
    /// 不再从插件自报的仓库下载 zip，也不再执行 flat payload 覆盖更新。
    /// </summary>
    public static async Task CheckAllPluginUpdatesAsync(
        CancellationToken ct = default,
        bool respectAutoCheckSetting = true)
    {
        if (respectAutoCheckSetting
            && !ConfigService.CurrentConfig.GlobalSettings.Plugins.AutoCheckUpdates)
            return;

        var catalog = await PluginStoreService.GetOfficialCatalogAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(catalog.ErrorMessage)) return;

        InstalledPluginInfo[] plugins = [];
        await UiDispatcherService.RunOnUiAsync(() => plugins = Installed.ToArray()).ConfigureAwait(false);
        foreach (var plugin in plugins)
        {
            var item = catalog.Items.FirstOrDefault(candidate =>
                string.Equals(candidate.PluginId, plugin.Id, StringComparison.OrdinalIgnoreCase));
            await UiDispatcherService.RunOnUiAsync(() => ApplyCatalogUpdate(plugin, item)).ConfigureAwait(false);
        }
    }

    public static async Task CheckPluginUpdateAsync(
        InstalledPluginInfo plugin,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var catalog = await PluginStoreService.GetOfficialCatalogAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(catalog.ErrorMessage))
        {
            LogService.LogWarning(
                I18n.Format("PluginService_CheckUpdateFailed", plugin.Id, catalog.ErrorMessage),
                nameof(PluginService));
            return;
        }

        var item = catalog.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.PluginId, plugin.Id, StringComparison.OrdinalIgnoreCase));
        await UiDispatcherService.RunOnUiAsync(() => ApplyCatalogUpdate(plugin, item)).ConfigureAwait(false);
    }

    public static async Task<PluginStoreInstallResult> UpdatePluginFromUrlAsync(
        InstalledPluginInfo plugin,
        CancellationToken ct = default)
    {
        if (plugin is null || string.IsNullOrWhiteSpace(plugin.UpdateDownloadUrl))
            return new PluginStoreInstallResult(false, UpdateResources.GetString("PluginService_NoUpdateUrl"));

        var catalog = await PluginStoreService.GetOfficialCatalogAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(catalog.ErrorMessage))
            return new PluginStoreInstallResult(false, catalog.ErrorMessage!);

        var item = catalog.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.PluginId, plugin.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Version, plugin.LatestVersion, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.DownloadUrl, plugin.UpdateDownloadUrl, StringComparison.Ordinal));
        if (item is null)
            return new PluginStoreInstallResult(false, UpdateResources.GetString("PluginService_NoUpdateUrl"));

        var result = await PluginStoreService.DownloadAndInstallAsync(item, ct).ConfigureAwait(false);
        if (result.Success)
        {
            await UiDispatcherService.RunOnUiAsync(() =>
            {
                plugin.HasUpdate = false;
                plugin.LatestVersion = null;
                plugin.UpdateDownloadUrl = null;
            }).ConfigureAwait(false);
        }
        return result;
    }

    private static void ApplyCatalogUpdate(InstalledPluginInfo plugin, PluginStoreAssetItem? item)
    {
        plugin.HasUpdate = false;
        plugin.LatestVersion = null;
        plugin.UpdateDownloadUrl = null;
        if (item is null || !IsNewerVersion(item.Version, plugin.Version)) return;

        plugin.HasUpdate = true;
        plugin.LatestVersion = item.Version;
        plugin.UpdateDownloadUrl = item.DownloadUrl;
    }

    private static bool IsNewerVersion(string candidate, string current)
        => PluginSemanticVersion.TryComparePrecedence(candidate, current, out var comparison)
           && comparison > 0;
}

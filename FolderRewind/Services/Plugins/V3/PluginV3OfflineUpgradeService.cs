using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Packaging;

namespace FolderRewind.Services.Plugins.V3;

internal static class PluginV3OfflineUpgradeService
{
    private const string MineRewindId = "com.folderrewind.minerewind";
    private const string BundledFileName = "MineRewind-1.9.0.frplugin";
    private const string BundledSha256 = "0e639eb558b561c2d15ba10a144402838b43c8a8cb9981fb484b0ab24708c2f8";

    public static async ValueTask RunAsync(CancellationToken cancellationToken = default)
    {
        var pluginId = new PluginId(MineRewindId);
        var pluginRoot = Path.Combine(PluginV3PackageService.PluginsRoot, MineRewindId);
        var flatManifest = Path.Combine(pluginRoot, "manifest.json");
        var settings = ConfigService.CurrentConfig.GlobalSettings.Plugins;
        var hasLegacyState = File.Exists(flatManifest)
            || settings.PluginEnabled.ContainsKey(MineRewindId)
            || settings.PluginSettings.ContainsKey(MineRewindId)
            || ConfigService.CurrentConfig.BackupConfigs.Any(config =>
                config.ProviderStates.ContainsKey(MineRewindId)
                || config.SourceFolders.Any(folder => folder.ProviderStates.ContainsKey(MineRewindId)));
        if (!hasLegacyState) return;

        var priorIntent = settings.EnabledIntent.TryGetValue(MineRewindId, out var intended)
            ? intended
            : settings.PluginEnabled.TryGetValue(MineRewindId, out var enabled) && enabled;
        var packagePath = ResolveBundledPackagePath();
        if (!File.Exists(packagePath))
            throw new FileNotFoundException("Bundled MineRewind migration package is missing.", packagePath);

        await PluginV3PackageService.InstallAsync(
            packagePath,
            PluginInstallProvenance.BundledOfficial,
            BundledSha256,
            cancellationToken).ConfigureAwait(false);
        settings.EnabledIntent[MineRewindId] = priorIntent;
        ConfigService.Save();
        await LegacyPluginQuarantineService.QuarantineFlatPayloadAsync(
            pluginId,
            pluginRoot,
            Path.Combine(AppRuntimeInfo.WritableAppDataBaseDirectory, "FolderRewind", "legacy-quarantine", "plugins"),
            cancellationToken).ConfigureAwait(false);
        if (priorIntent)
        {
            var transition = await PluginV3PackageService.SetEnabledAsync(pluginId, true, cancellationToken)
                .ConfigureAwait(false);
            if (!transition.Success)
                throw new InvalidOperationException("Bundled MineRewind installed, but its prior Enabled Intent could not be activated.");
        }
    }

    private static string ResolveBundledPackagePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "Plugins", BundledFileName),
            Path.Combine(AppContext.BaseDirectory, BundledFileName)
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

}

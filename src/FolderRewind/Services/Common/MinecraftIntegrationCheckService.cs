using FolderRewind.Models;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static class MinecraftIntegrationCheckService
{
    public static async Task<IReadOnlyList<OnboardingCheck>> CheckAsync(BackupConfig config, ManagedFolder source, CancellationToken token)
    {
        var signature = NativeHistoryConfigLease.Signature(config);
        var now = DateTimeOffset.UtcNow;
        OnboardingCheck Item(string code, OnboardingCheckState state, string key, params string[] actions) => new(code, state, I18n.GetString(key), now, actions);
        var result = new List<OnboardingCheck>();
        var plugin = (await PluginV3PackageService.GetInstalledPluginInfosAsync(cancellationToken: token)).FirstOrDefault(p => p.Id == "com.folderrewind.minerewind");
        var pluginActive = plugin is not null && plugin.IsEnabled && string.IsNullOrEmpty(plugin.LoadError)
            && PluginV3RuntimeService.IsActive(new FolderRewind.Plugin.Abstractions.PluginId(plugin.Id));
        result.Add(Item("minecraft.host-plugin", plugin is null ? OnboardingCheckState.NeedsInput : OnboardingCheckState.Ready,
            plugin is null ? "MinecraftCheck_PluginMissing" : "MinecraftCheck_PluginInstalled", "settings.plugins"));
        result.Add(Item("minecraft.host-plugin-runtime", plugin is null ? OnboardingCheckState.NeedsInput : plugin.RequiresRestart ? OnboardingCheckState.Blocked
            : pluginActive ? OnboardingCheckState.Ready : OnboardingCheckState.NeedsInput,
            plugin is null ? "MinecraftCheck_PluginMissing" : plugin.RequiresRestart ? "Onboarding_RestartRequired" : pluginActive ? "MinecraftCheck_PluginAvailable" : "MinecraftCheck_PluginInactive", "settings.plugins"));
        var server = KnotLinkServerManagerService.GetServerCompatibilityInfo();
        result.Add(Item("minecraft.knotlink-server", server.RequiresUpdate ? OnboardingCheckState.NeedsInput : OnboardingCheckState.Unknown,
            server.RequiresUpdate ? "MinecraftCheck_ServerUpdate" : "MinecraftCheck_ServerUnknown", "settings.knotlink"));
        result.Add(Item("minecraft.host-connection", KnotLinkService.IsInitialized ? OnboardingCheckState.Ready : OnboardingCheckState.NeedsInput,
            KnotLinkService.IsInitialized ? "MinecraftCheck_HostInitialized" : "MinecraftCheck_HostNotInitialized", "settings.knotlink"));
        result.Add(Item("minecraft.source-association", config.SourceFolders.Any(f => f.Id == source.Id) && Directory.Exists(source.Path) ? OnboardingCheckState.Ready : OnboardingCheckState.Blocked,
            "MinecraftCheck_SourceAssociation", "project.source"));
        // 当前插件命令不提供独立的只读按实例握手。不得以保存/恢复作为探测。
        result.Add(Item("minecraft.game-response", OnboardingCheckState.Unknown, "MinecraftCheck_GameUnknown", "minecraft.manual-install", "minecraft.recheck"));
        result.Add(Item("minecraft.hot-backup", OnboardingCheckState.Unknown, "MinecraftCheck_HotBackupUnknown", "minecraft.recheck"));
        result.Add(Item("minecraft.hot-restore", OnboardingCheckState.Unknown, "MinecraftCheck_HotRestoreUnknown", "minecraft.recheck"));
        result.Add(Item("minecraft.game-schedule", OnboardingCheckState.Unknown, "MinecraftCheck_ScheduleUnknown", "settings.automation"));
        token.ThrowIfCancellationRequested();
        if (NativeHistoryConfigLease.Signature(config) != signature || !ConfigService.CurrentConfig.BackupConfigs.Contains(config)) throw new InvalidOperationException(I18n.GetString("SettingsProject_Stale"));
        return result;
    }
}

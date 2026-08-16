using FolderRewind.Models;
using FolderRewind.Services.Hotkeys;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Plugins;

/// <summary>
/// v3 插件系统的 Host 入口。这里仅负责包目录、运行时初始化和 UI 投影；
/// v2 flat payload 只允许由离线迁移器读取并隔离，绝不能再作为可执行插件扫描或加载。
/// </summary>
public static partial class PluginService
{
    private static readonly object SyncRoot = new();
    private static readonly ObservableCollection<InstalledPluginInfo> Installed = new();
    private static bool _initialized;
    private static Task _initializationTask = Task.CompletedTask;

    public static ReadOnlyObservableCollection<InstalledPluginInfo> InstalledPlugins { get; } = new(Installed);

    public static Task Initialization => Volatile.Read(ref _initializationTask);

    public static string PluginRootDirectory => Path.Combine(
        AppRuntimeInfo.WritableAppDataBaseDirectory,
        "FolderRewind",
        "plugins");

    public static void Initialize()
    {
        lock (SyncRoot)
        {
            if (_initialized) return;
            Directory.CreateDirectory(PluginRootDirectory);
            _initialized = true;
            _initializationTask = Task.Run(InitializePluginSystemsAsync);
        }
    }

    private static async Task InitializePluginSystemsAsync()
    {
        LogService.LogInfo("Plugin initialization started on the background worker.", "PluginV3");
        try
        {
            if (!PluginRuntimeModeService.IsSafeMode)
            {
                var migration = await PluginV3OfflineUpgradeService.RunAsync().ConfigureAwait(false);
                if (migration.RecoveryRequired)
                {
                    LogService.LogWarning(
                        $"Plugin migration requires recovery: {migration.DiagnosticCode}: {migration.DiagnosticMessage}",
                        "PluginV3Migration");
                }
            }

            await PluginV3PackageService.InitializeAsync().ConfigureAwait(false);
            if (!PluginRuntimeModeService.IsSafeMode)
                await PluginV3DiscoveryService.RunStartupAutoCreateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogService.LogError($"Plugin v3 initialization failed: {ex.Message}", "PluginV3", ex);
        }

        try
        {
            await UiDispatcherService.RunOnUiAsync(RefreshInstalledList).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogService.LogError($"Plugin UI initialization failed: {ex.Message}", "PluginV3", ex);
        }

        LogService.LogInfo("Plugin initialization completed without blocking the UI dispatcher.", "PluginV3");

        if (PluginRuntimeModeService.IsSafeMode) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000).ConfigureAwait(false);
                await CheckAllPluginUpdatesAsync().ConfigureAwait(false);
            }
            catch
            {
                // 后台更新检查不能影响插件运行时初始化。
            }
        });
    }

    public static void RefreshInstalledList()
    {
        lock (SyncRoot)
        {
            Installed.Clear();
            try
            {
                Directory.CreateDirectory(PluginRootDirectory);
                foreach (var info in PluginV3PackageService.GetInstalledPluginInfos())
                {
                    Installed.Add(info);
                }
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("PluginService_ScanPluginsFailed", ex.Message), "PluginService", ex);
            }
        }
    }

    /// <summary>
    /// 刷新 v3 安装状态。启停和替换由 PluginV3PackageService 事务化执行，
    /// 因此这里不再扫描或重新加载任何 flat DLL。
    /// </summary>
    public static void RefreshRuntimeUi()
    {
        RefreshInstalledList();
        if (PluginRuntimeModeService.IsSafeMode) return;
        try
        {
            HotkeyManager.ApplyBindingsToUiAndNative();
        }
        catch (Exception ex)
        {
            LogService.LogWarning($"Plugin hotkeys could not be refreshed: {ex.Message}", "PluginV3");
        }
    }

    public static bool IsPluginSystemEnabled() => !PluginRuntimeModeService.IsSafeMode;

    public static bool GetPluginEnabled(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || PluginRuntimeModeService.IsSafeMode) return false;
        try { return PluginV3RuntimeService.IsActive(new FolderRewind.Plugin.Abstractions.PluginId(pluginId)); }
        catch { return false; }
    }

    public static void OpenPluginFolder()
    {
        Directory.CreateDirectory(PluginRootDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = PluginRootDirectory,
            UseShellExecute = true
        });
    }

    internal static InstalledPluginInfo? FindInstalled(string pluginId)
        => Installed.FirstOrDefault(item =>
            string.Equals(item.Id, pluginId, StringComparison.OrdinalIgnoreCase));
}

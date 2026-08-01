using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Hotkeys;
using FolderRewind.Services.KnotLink;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ResourceLoader = FolderRewind.Services.AppResourceLoader;

namespace FolderRewind.Services.Plugins
{
    /// <summary>
    /// 插件服务：安装/卸载、扫描、加载、启停与对外调用。
    ///
    /// 安全/鲁棒性约束：
    /// - 插件目录只读写在 AppData 下，避免污染安装目录；
    /// - 读取 manifest 与加载插件时尽量捕获异常，确保主程序不崩；
    /// - 插件被禁用时不调用其逻辑（但为了简单，仍可能已被加载）。
    /// </summary>
    public static partial class PluginService
    {
        private const string ManifestFileName = "manifest.json";
        private const string PendingDeleteFileName = ".pending_delete";
        private const string PendingUpdateFileName = ".pending_update";

        private static readonly ResourceLoader _rl = ResourceLoader.GetForViewIndependentUse();

        private static readonly object _lock = new();
        private static bool _initialized;

        private static readonly Dictionary<string, LoadedPlugin> _loaded = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ObservableCollection<InstalledPluginInfo> _installed = new();
        private static readonly SemaphoreSlim _configAugmentationLock = new(1, 1);

        public static ReadOnlyObservableCollection<InstalledPluginInfo> InstalledPlugins { get; } = new(_installed);

        public static string PluginRootDirectory => Path.Combine(AppRuntimeInfo.WritableAppDataBaseDirectory, "FolderRewind", "plugins");

        /// <summary>
        /// 获取当前 Host 版本号
        /// </summary>
        public static string GetHostVersion()
        {
            var version = AppRuntimeInfo.GetApplicationVersion();
            return version == null
                ? "1.0.0"
                : $"{version.Major}.{version.Minor}.{version.Build}";
        }

        /// <summary>
        /// 检查版本兼容性
        /// </summary>
        private static bool IsVersionCompatible(string? minHostVersion, out string? incompatibleReason)
        {
            incompatibleReason = null;
            if (string.IsNullOrWhiteSpace(minHostVersion)) return true;

            try
            {
                var hostVersion = Version.Parse(GetHostVersion());
                var minVersion = Version.Parse(minHostVersion);

                if (hostVersion < minVersion)
                {
                    incompatibleReason = string.Format(_rl.GetString("PluginService_IncompatibleHostVersion"), minHostVersion, GetHostVersion());
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                incompatibleReason = string.Format(_rl.GetString("PluginService_VersionParseFailed"), ex.Message);
                return false;
            }
        }

        public static void Initialize()
        {
            if (_initialized) return;

            lock (_lock)
            {
                if (_initialized) return;

                try
                {
                    Directory.CreateDirectory(PluginRootDirectory);

                    // 清理上次未能删除的插件
                    CleanPendingDeletions();

                    // 应用上次未完成的插件更新
                    ApplyPendingUpdates();
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_CreatePluginDirFailed", ex.Message), "PluginService", ex);
                }

                // 扫描安装清单（不一定加载插件）
                RefreshInstalledList();

                // 根据总开关/启用状态加载
                TryLoadEnabledPlugins();

                // 注册已启用插件的热键定义（窗口就绪后 HotkeyManager 会自动应用）
                TryRegisterPluginHotkeysForEnabled();

                _initialized = true;

                // 异步检查更新（不阻塞初始化）
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(3000); // 延迟检查，避免启动时网络请求过多
                        await CheckAllPluginUpdatesAsync();
                    }
                    catch { }
                });
            }
        }

        private static void TryRegisterPluginHotkeysForEnabled()
        {
            if (!IsPluginSystemEnabled()) return;

            lock (_lock)
            {
                foreach (var kv in _loaded)
                {
                    var loaded = kv.Value;
                    if (loaded?.Instance == null) continue;
                    if (!GetPluginEnabled(loaded.Manifest.Id)) continue;

                    HotkeyManager.RegisterPluginHotkeys(loaded.Manifest, loaded.Instance);
                }
            }
        }

        public static void RefreshInstalledList()
        {
            lock (_lock)
            {
                _installed.Clear();

                try
                {
                    if (!Directory.Exists(PluginRootDirectory))
                    {
                        Directory.CreateDirectory(PluginRootDirectory);
                    }

                    foreach (var dir in Directory.EnumerateDirectories(PluginRootDirectory))
                    {
                        var info = ReadInstalledPluginInfo(dir);
                        if (info != null)
                        {
                            _installed.Add(info);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_ScanPluginsFailed", ex.Message), "PluginService", ex);
                }
            }
        }

        /// <summary>
        /// 手动刷新：用于 UI 中点击“刷新插件列表”。
        /// </summary>
        public static void RefreshAndLoadEnabled()
        {
            RefreshInstalledList();
            TryLoadEnabledPlugins();
            TryRegisterPluginHotkeysForEnabled();

            try { HotkeyManager.ApplyBindingsToUiAndNative(); } catch { }
        }

        /// <summary>
        /// KnotLink v2 commands are offered to plugins before built-in handlers.
        /// </summary>
        public static async Task<(bool Handled, string Response)> TryHandleParameterizedKnotLinkCommandAsync(KnotLinkCommandContext context)
        {
            if (!IsPluginSystemEnabled()) return (false, string.Empty);
            if (context == null || string.IsNullOrWhiteSpace(context.Command)) return (false, string.Empty);

            var request = context.Request;
            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                if (plugin is not IFolderRewindParameterizedKnotLinkCommandHandler handler) continue;

                try
                {
                    var settings = GetPluginSettings(plugin.Manifest.Id);
                    var ctx = PluginHostContext.CreateForCurrentApp(plugin.Manifest.Id, plugin.Manifest.Name);
                    using var scope = KnotLinkService.PushCommandContext(context);
                    var result = await handler.TryHandleParameterizedKnotLinkCommandAsync(
                        request,
                        settings,
                        ctx).ConfigureAwait(false);

                    if (result?.Handled == true)
                    {
                        return (true, string.IsNullOrWhiteSpace(result.Response) ? "OK:" : result.Response!);
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_KnotLinkCommandFailed", plugin.Manifest.Id, request.Command, ex.Message), "PluginService", ex);
                }
            }

            return (false, string.Empty);
        }

        internal static IReadOnlyList<(string PluginId, PluginKnotLinkCapabilityContribution Contribution)>
            GetKnotLinkCapabilityContributions()
        {
            var result = new List<(string, PluginKnotLinkCapabilityContribution)>();
            if (!IsPluginSystemEnabled()) return result;

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot().OrderBy(item => item.Manifest.Id, StringComparer.OrdinalIgnoreCase))
            {
                if (plugin is not IFolderRewindKnotLinkCapabilityProvider provider) continue;

                try
                {
                    result.Add((plugin.Manifest.Id, provider.GetKnotLinkCapabilities() ?? new PluginKnotLinkCapabilityContribution()));
                }
                catch (Exception ex)
                {
                    LogService.LogError(
                        $"Plugin '{plugin.Manifest.Id}' failed to declare KnotLink capabilities: {ex.Message}",
                        "PluginService",
                        ex);
                }
            }

            return result;
        }

        public static bool IsPluginSystemEnabled()
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
            return settings?.Enabled == true;
        }

        public static void SetPluginSystemEnabled(bool enabled)
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
            if (settings == null) return;

            settings.Enabled = enabled;
            ConfigService.Save();

            if (enabled)
            {
                TryLoadEnabledPlugins();
            }
        }

        public static bool GetPluginEnabled(string pluginId)
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
            if (settings == null) return false;

            if (settings.PluginEnabled.TryGetValue(pluginId, out var enabled)) return enabled;
            return false;
        }

        public static void SetPluginEnabled(string pluginId, bool enabled)
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
            if (settings == null) return;

            settings.PluginEnabled[pluginId] = enabled;
            ConfigService.Save();

            // 设计选择：启用可尝试立即加载；禁用仅停止调用（不强制卸载）。
            if (enabled && IsPluginSystemEnabled())
            {
                TryLoadPlugin(pluginId);
            }

            // 热键：插件启停后刷新绑定
            try
            {
                if (enabled && _loaded.TryGetValue(pluginId, out var loaded) && loaded != null)
                {
                    HotkeyManager.RegisterPluginHotkeys(loaded.Manifest, loaded.Instance);
                }
                HotkeyManager.ApplyBindingsToUiAndNative();
            }
            catch
            {
            }

            // 同步 UI 信息
            var item = _installed.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
            if (item != null) item.IsEnabled = enabled;
        }

        public static IReadOnlyDictionary<string, string> GetPluginSettings(string pluginId)
        {
            var plugins = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
            if (plugins == null) return new Dictionary<string, string>();

            if (plugins.PluginSettings.TryGetValue(pluginId, out var dict) && dict != null)
            {
                return dict;
            }

            return new Dictionary<string, string>();
        }

        public static PluginSettingsSaveResult SavePluginSettings(string pluginId, IReadOnlyDictionary<string, string> values)
        {
            var plugins = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
            if (plugins == null)
            {
                return new PluginSettingsSaveResult();
            }

            var previous = new Dictionary<string, string>(GetPluginSettings(pluginId), StringComparer.OrdinalIgnoreCase);
            var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in values)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    continue;
                }

                next[pair.Key] = pair.Value ?? string.Empty;
            }

            plugins.PluginSettings[pluginId] = next;
            ConfigService.Save();

            return new PluginSettingsSaveResult
            {
                PreviousSettings = previous,
                CurrentSettings = new Dictionary<string, string>(next, StringComparer.OrdinalIgnoreCase)
            };
        }

        public static async Task TryRunConfigAugmentationForSettingsChangeAsync(
            string pluginId,
            IReadOnlyDictionary<string, string> previousSettings,
            IReadOnlyDictionary<string, string> currentSettings)
        {
            if (string.IsNullOrWhiteSpace(pluginId) || !IsPluginSystemEnabled())
            {
                return;
            }

            IFolderRewindConfigAugmenter? augmenter = null;

            lock (_lock)
            {
                if (_loaded.TryGetValue(pluginId, out var loaded) && loaded.Instance is IFolderRewindConfigAugmenter typedAugmenter)
                {
                    augmenter = typedAugmenter;
                }
            }

            if (augmenter == null)
            {
                return;
            }

            bool shouldAugment;
            try
            {
                shouldAugment = augmenter.ShouldAugmentAfterSettingsChange(previousSettings, currentSettings);
            }
            catch (Exception ex)
            {
                LogService.LogError(
                    $"[PluginService] Config augmentation settings trigger failed: {pluginId}: {ex.Message}",
                    "PluginService",
                    ex);
                return;
            }

            if (!shouldAugment)
            {
                return;
            }

            await RunConfigAugmentationAsync(
                PluginConfigAugmentationReason.SettingsEnabled,
                new[] { pluginId }).ConfigureAwait(false);
        }

        public static void SetPluginSetting(string pluginId, string key, string value)
        {
            var plugins = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
            if (plugins == null) return;

            if (!plugins.PluginSettings.TryGetValue(pluginId, out var dict) || dict == null)
            {
                dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                plugins.PluginSettings[pluginId] = dict;
            }

            dict[key] = value ?? string.Empty;
            ConfigService.Save();
        }

        /// <summary>
        /// 当用户在 UI 中修改插件设置时，尝试让已加载插件立即重新读取设置。
        /// </summary>
        public static void TryReinitialize(string pluginId)
        {
            if (string.IsNullOrWhiteSpace(pluginId)) return;
            if (!IsPluginSystemEnabled()) return;

            lock (_lock)
            {
                if (!_loaded.TryGetValue(pluginId, out var loaded) || loaded.Instance == null) return;
                try
                {
                    var settings = GetPluginSettings(pluginId);
                    loaded.Instance.Initialize(settings);
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_ReinitializeFailed", pluginId, ex.Message), "PluginService", ex);
                }
            }
        }

        public static IReadOnlyList<PluginSettingDefinition> GetSettingsDefinitions(string pluginId)
        {
            if (!IsPluginSystemEnabled()) return Array.Empty<PluginSettingDefinition>();

            if (_loaded.TryGetValue(pluginId, out var loaded) && loaded.Instance != null)
            {
                try
                {
                    // GetSettingsDefinitions 的返回类型已经是 IReadOnlyList，无需再 ToList。
                    return loaded.Instance.GetSettingsDefinitions() ?? Array.Empty<PluginSettingDefinition>();
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_GetSettingsDefinitionsFailed", pluginId, ex.Message), "PluginService", ex);
                }
            }

            return Array.Empty<PluginSettingDefinition>();
        }

    }
}

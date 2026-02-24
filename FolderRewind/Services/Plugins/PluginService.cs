using FolderRewind.Models;
using FolderRewind.Services.Hotkeys;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Resources;
using Windows.Storage;

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
    public static class PluginService
    {
        private const string ManifestFileName = "manifest.json";
        private const string PendingDeleteFileName = ".pending_delete";
        private const string PendingUpdateFileName = ".pending_update";

        private static readonly ResourceLoader _rl = ResourceLoader.GetForViewIndependentUse();

        private static readonly object _lock = new();
        private static bool _initialized;

        private static readonly Dictionary<string, LoadedPlugin> _loaded = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ObservableCollection<InstalledPluginInfo> _installed = new();

        public static ReadOnlyObservableCollection<InstalledPluginInfo> InstalledPlugins { get; } = new(_installed);

        public static string PluginRootDirectory => Path.Combine(GetWritableAppDataDir(), "FolderRewind", "plugins");

        /// <summary>
        /// 获取当前 Host 版本号
        /// </summary>
        public static string GetHostVersion()
        {
            try
            {
                var version = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"{version.Major}.{version.Minor}.{version.Build}";
            }
            catch
            {
                return "1.0.0";
            }
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
        /// KnotLink：尝试让已启用插件处理一条“非内置”的远程指令。
        /// 返回 (Handled=false, _) 表示没有插件处理该指令。
        /// </summary>
        public static async Task<(bool Handled, string Response)> TryHandleKnotLinkCommandAsync(
            string command,
            string args,
            string rawCommand)
        {
            if (!IsPluginSystemEnabled()) return (false, string.Empty);
            if (string.IsNullOrWhiteSpace(command)) return (false, string.Empty);

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                if (plugin is not IFolderRewindKnotLinkCommandHandler handler) continue;

                try
                {
                    var settings = GetPluginSettings(plugin.Manifest.Id);
                    var ctx = PluginHostContext.CreateForCurrentApp(plugin.Manifest.Id, plugin.Manifest.Name);
                    var resp = await handler.TryHandleKnotLinkCommandAsync(
                        command,
                        args,
                        rawCommand,
                        settings,
                        ctx).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(resp))
                    {
                        return (true, resp);
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_KnotLinkCommandFailed", plugin.Manifest.Id, command, ex.Message), "PluginService", ex);
                }
            }

            return (false, string.Empty);
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

        public static string? InvokeBeforeBackupFolder(BackupConfig config, ManagedFolder folder)
        {
            if (!IsPluginSystemEnabled()) return null;

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    var settings = GetPluginSettings(plugin.Manifest.Id);
                    var newPath = plugin.OnBeforeBackupFolder(config, folder, settings);
                    if (!string.IsNullOrWhiteSpace(newPath))
                    {
                        // 允许多个插件串联修改路径：使用最后一个返回的路径
                        folder = new ManagedFolder { Path = newPath, DisplayName = folder.DisplayName, Description = folder.Description, IsFavorite = folder.IsFavorite, CoverImagePath = folder.CoverImagePath };
                        return newPath;
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_BeforeBackupFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                }
            }

            return null;
        }

        public static void InvokeAfterBackupFolder(BackupConfig config, ManagedFolder folder, bool success, string? generatedArchiveFileName)
        {
            if (!IsPluginSystemEnabled()) return;

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    var settings = GetPluginSettings(plugin.Manifest.Id);
                    plugin.OnAfterBackupFolder(config, folder, success, generatedArchiveFileName, settings);
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_AfterBackupFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                }
            }
        }

        public static IReadOnlyList<ManagedFolder> InvokeDiscoverManagedFolders(string selectedRootPath)
        {
            if (!IsPluginSystemEnabled()) return Array.Empty<ManagedFolder>();
            if (string.IsNullOrWhiteSpace(selectedRootPath)) return Array.Empty<ManagedFolder>();

            var results = new List<ManagedFolder>();
            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    var settings = GetPluginSettings(plugin.Manifest.Id);
                    var discovered = plugin.TryDiscoverManagedFolders(selectedRootPath, settings);
                    if (discovered != null && discovered.Count > 0)
                    {
                        results.AddRange(discovered);
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_DiscoverManagedFoldersFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                }
            }

            return results;
        }

        /// <summary>
        /// 获取所有已加载插件支持的配置类型
        /// </summary>
        public static IReadOnlyList<string> GetAllSupportedConfigTypes()
        {
            if (!IsPluginSystemEnabled()) return Array.Empty<string>();

            var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Default" };

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    var pluginTypes = plugin.GetSupportedConfigTypes();
                    if (pluginTypes != null)
                    {
                        foreach (var t in pluginTypes)
                        {
                            if (!string.IsNullOrWhiteSpace(t))
                                types.Add(t);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_GetSupportedConfigTypesFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                }
            }

            return types.ToList();
        }

        /// <summary>
        /// 检查是否有插件希望接管指定配置的备份
        /// </summary>
        public static (bool ShouldHandle, IFolderRewindPlugin? Plugin) CheckPluginWantsToHandleBackup(BackupConfig config)
        {
            if (!IsPluginSystemEnabled()) return (false, null);

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    if (plugin.WantsToHandleBackup(config))
                    {
                        return (true, plugin);
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_WantsToHandleBackupFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                }
            }

            return (false, null);
        }

        /// <summary>
        /// 检查是否有插件希望接管指定配置的还原
        /// </summary>
        public static (bool ShouldHandle, IFolderRewindPlugin? Plugin) CheckPluginWantsToHandleRestore(BackupConfig config)
        {
            if (!IsPluginSystemEnabled()) return (false, null);

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    if (plugin.WantsToHandleRestore(config))
                    {
                        return (true, plugin);
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_WantsToHandleRestoreFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                }
            }

            return (false, null);
        }

        /// <summary>
        /// 调用插件执行备份
        /// </summary>
        public static async Task<PluginBackupResult> InvokePluginBackupAsync(
            IFolderRewindPlugin plugin,
            BackupConfig config,
            ManagedFolder folder,
            string comment,
            Action<double, string>? progressCallback = null)
        {
            try
            {
                var settings = GetPluginSettings(plugin.Manifest.Id);
                return await plugin.PerformBackupAsync(config, folder, comment, settings, progressCallback);
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("PluginService_PerformBackupFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                return new PluginBackupResult { Success = false, Message = ex.Message };
            }
        }

        /// <summary>
        /// 调用插件执行还原
        /// </summary>
        public static async Task<PluginRestoreResult> InvokePluginRestoreAsync(
            IFolderRewindPlugin plugin,
            BackupConfig config,
            ManagedFolder folder,
            string archiveFileName,
            Action<double, string>? progressCallback = null)
        {
            try
            {
                var settings = GetPluginSettings(plugin.Manifest.Id);
                return await plugin.PerformRestoreAsync(config, folder, archiveFileName, settings, progressCallback);
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("PluginService_PerformRestoreFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                return new PluginRestoreResult { Success = false, Message = ex.Message };
            }
        }

        /// <summary>
        /// 调用插件批量创建配置
        /// </summary>
        public static PluginCreateConfigResult InvokeCreateConfigs(string selectedRootPath, string? configType = null)
        {
            if (!IsPluginSystemEnabled()) return new PluginCreateConfigResult { Handled = false };
            if (string.IsNullOrWhiteSpace(selectedRootPath)) return new PluginCreateConfigResult { Handled = false };

            var typeFilter = string.IsNullOrWhiteSpace(configType) ? null : configType;
            if (string.Equals(typeFilter, "Default", StringComparison.OrdinalIgnoreCase))
            {
                // Default 不是插件类型，不走插件批量创建
                return new PluginCreateConfigResult { Handled = false };
            }

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(typeFilter))
                    {
                        bool canHandleType = false;
                        try
                        {
                            canHandleType = plugin.CanHandleConfigType(typeFilter);
                        }
                        catch
                        {
                            // ignore; fallback to supported types list
                        }

                        if (!canHandleType)
                        {
                            var supported = plugin.GetSupportedConfigTypes();
                            canHandleType = supported != null && supported.Any(t => string.Equals(t, typeFilter, StringComparison.OrdinalIgnoreCase));
                        }

                        if (!canHandleType)
                        {
                            continue;
                        }
                    }

                    var settings = GetPluginSettings(plugin.Manifest.Id);
                    var result = plugin.TryCreateConfigs(selectedRootPath, settings);
                    if (result.Handled)
                    {
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_TryCreateConfigsFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                }
            }

            return new PluginCreateConfigResult { Handled = false };
        }

        /// <summary>
        /// 从 zip 安装插件（zip 内需包含 manifest.json）。
        /// 目标目录：plugins/{pluginId}/...
        /// </summary>
        public static async Task<(bool Success, string Message)> InstallFromZipAsync(string zipFilePath, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(zipFilePath) || !File.Exists(zipFilePath))
            {
                return (false, _rl.GetString("PluginService_PackageNotFound"));
            }

            try
            {
                Directory.CreateDirectory(PluginRootDirectory);

                // 先读 manifest（防止解压一堆垃圾后才发现不合法）
                PluginInstallManifest? manifest;
                using (var zip = ZipFile.OpenRead(zipFilePath))
                {
                    var entry = zip.Entries.FirstOrDefault(e => string.Equals(NormalizeZipPath(e.FullName), ManifestFileName, StringComparison.OrdinalIgnoreCase));
                    if (entry == null) return (false, _rl.GetString("PluginService_MissingManifest"));

                    await using var s = entry.Open();
                    manifest = await JsonSerializer.DeserializeAsync(s, AppJsonContext.Default.PluginInstallManifest, ct);
                }

                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id))
                {
                    return (false, _rl.GetString("PluginService_InvalidManifestNoId"));
                }

                ApplyManifestLocalization(manifest);

                var targetDir = Path.Combine(PluginRootDirectory, SanitizeFolderName(manifest.Id));
                var wasEnabled = GetPluginEnabled(manifest.Id);

                // 覆盖安装：先删除原目录
                if (Directory.Exists(targetDir))
                {
                    var unloadSuccess = TryUnloadPlugin(manifest.Id);
                    if (!unloadSuccess)
                    {
                        var persistedPackage = PersistPendingUpdatePackage(zipFilePath, manifest.Id);
                        if (string.IsNullOrWhiteSpace(persistedPackage))
                        {
                            return (false, _rl.GetString("PluginService_QueueInstallPendingFailed"));
                        }

                        QueuePendingUpdate(manifest.Id, persistedPackage, wasEnabled);
                        return (true, _rl.GetString("PluginService_InstallPendingRestart"));
                    }

                    try
                    {
                        Directory.Delete(targetDir, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        LogService.LogWarning(I18n.Format("PluginService_DeleteFailed", manifest.Id, ex.Message), "PluginService");

                        var persistedPackage = PersistPendingUpdatePackage(zipFilePath, manifest.Id);
                        if (string.IsNullOrWhiteSpace(persistedPackage))
                        {
                            return (false, string.Format(_rl.GetString("PluginService_OverwriteFailed"), ex.Message));
                        }

                        QueuePendingUpdate(manifest.Id, persistedPackage, wasEnabled);
                        return (true, _rl.GetString("PluginService_InstallPendingRestart"));
                    }
                }

                Directory.CreateDirectory(targetDir);

                // 解压（防 ZipSlip）
                using (var zip = ZipFile.OpenRead(zipFilePath))
                {
                    foreach (var entry in zip.Entries)
                    {
                        ct.ThrowIfCancellationRequested();

                        var normalized = NormalizeZipPath(entry.FullName);
                        if (string.IsNullOrWhiteSpace(normalized)) continue;

                        // 目录项
                        if (normalized.EndsWith("/", StringComparison.Ordinal))
                        {
                            Directory.CreateDirectory(Path.Combine(targetDir, normalized.TrimEnd('/')));
                            continue;
                        }

                        var destPath = Path.GetFullPath(Path.Combine(targetDir, normalized));
                        var fullTargetDir = Path.GetFullPath(targetDir);
                        if (!destPath.StartsWith(fullTargetDir, StringComparison.OrdinalIgnoreCase))
                        {
                            return (false, _rl.GetString("PluginService_ZipSlip"));
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        entry.ExtractToFile(destPath, overwrite: true);
                    }
                }

                // 写回 UI/配置
                RefreshInstalledList();

                // 默认：新安装插件设为禁用，避免“装完立刻执行”带来的惊吓。
                var settings = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
                if (settings != null)
                {
                    if (!settings.PluginEnabled.ContainsKey(manifest.Id))
                    {
                        settings.PluginEnabled[manifest.Id] = false;
                        ConfigService.Save();
                    }
                }

                return (true, _rl.GetString("PluginService_InstallSuccessDisabled"));
            }
            catch (OperationCanceledException)
            {
                return (false, _rl.GetString("Common_Canceled"));
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("PluginService_InstallFailed_Log", ex.Message), "PluginService", ex);
                return (false, string.Format(_rl.GetString("PluginService_InstallFailed"), ex.Message));
            }
        }

        private static string? PersistPendingUpdatePackage(string sourceZipPath, string pluginId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sourceZipPath) || !File.Exists(sourceZipPath)) return null;

                var pendingDir = Path.Combine(PluginRootDirectory, "_pending_updates");
                Directory.CreateDirectory(pendingDir);

                var fileName = $"{SanitizeFolderName(pluginId)}-{Guid.NewGuid():N}.zip";
                var targetPath = Path.Combine(pendingDir, fileName);
                File.Copy(sourceZipPath, targetPath, overwrite: false);

                return targetPath;
            }
            catch (Exception ex)
            {
                LogService.LogWarning(I18n.Format("PluginService_PersistPendingPackageFailed_Log", pluginId, ex.Message), "PluginService");
                return null;
            }
        }

        public static (bool Success, string Message) Uninstall(string pluginId)
        {
            if (string.IsNullOrWhiteSpace(pluginId)) return (false, _rl.GetString("PluginService_EmptyPluginId"));

            try
            {
                var dir = Path.Combine(PluginRootDirectory, SanitizeFolderName(pluginId));
                if (!Directory.Exists(dir)) return (false, _rl.GetString("PluginService_NotInstalled"));

                // 尝试卸载已加载的插件
                bool unloadSuccess = TryUnloadPlugin(pluginId);
                if (!unloadSuccess)
                {
                    // 插件仍被占用，标记为待删除，下次启动时清理
                    MarkPluginForDeletion(pluginId);
                    return (true, _rl.GetString("PluginService_UninstallPendingRestart"));
                }

                // 尝试删除目录
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception deleteEx)
                {
                    // 文件被占用，标记为待删除
                    LogService.LogWarning(I18n.Format("PluginService_DeleteFailed", pluginId, deleteEx.Message), "PluginService");
                    MarkPluginForDeletion(pluginId);
                    return (true, _rl.GetString("PluginService_UninstallPendingRestart"));
                }

                RefreshInstalledList();

                var settings = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
                if (settings != null)
                {
                    settings.PluginEnabled.Remove(pluginId);
                    settings.PluginSettings.Remove(pluginId);
                    ConfigService.Save();
                }

                return (true, _rl.GetString("PluginService_UninstallSuccess"));
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("PluginService_UninstallFailed_Log", pluginId, ex.Message), "PluginService", ex);
                return (false, string.Format(_rl.GetString("PluginService_UninstallFailed"), ex.Message));
            }
        }

        /// <summary>
        /// 尝试卸载插件的 AssemblyLoadContext
        /// </summary>
        private static bool TryUnloadPlugin(string pluginId)
        {
            lock (_lock)
            {
                if (!_loaded.TryGetValue(pluginId, out var loaded)) return true;

                try
                {
                    // 取消热键注册
                    try
                    {
                        HotkeyManager.UnregisterPluginHotkeys(pluginId);
                    }
                    catch { }

                    // 尝试调用插件的 Dispose
                    try
                    {
                        if (loaded.Instance is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                    }
                    catch { }

                    // 获取弱引用以检查卸载是否成功
                    var weakRef = new WeakReference(loaded.LoadContext);

                    // 从已加载列表移除
                    _loaded.Remove(pluginId);

                    // 卸载 AssemblyLoadContext
                    loaded.LoadContext.Unload();

                    // 强制 GC 回收
                    for (int i = 0; i < 10 && weakRef.IsAlive; i++)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }

                    return !weakRef.IsAlive;
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_UnloadFailed", pluginId, ex.Message), "PluginService", ex);
                    return false;
                }
            }
        }

        /// <summary>
        /// 标记插件为待删除状态（下次启动时清理）
        /// </summary>
        private static void MarkPluginForDeletion(string pluginId)
        {
            try
            {
                var pendingFile = Path.Combine(PluginRootDirectory, PendingDeleteFileName);
                var lines = File.Exists(pendingFile)
                    ? File.ReadAllLines(pendingFile).ToList()
                    : new List<string>();

                if (!lines.Contains(pluginId, StringComparer.OrdinalIgnoreCase))
                {
                    lines.Add(pluginId);
                    File.WriteAllLines(pendingFile, lines);
                }
            }
            catch { }
        }

        /// <summary>
        /// 清理待删除的插件（在初始化时调用）
        /// </summary>
        private static void CleanPendingDeletions()
        {
            try
            {
                var pendingFile = Path.Combine(PluginRootDirectory, PendingDeleteFileName);
                if (!File.Exists(pendingFile)) return;

                var lines = File.ReadAllLines(pendingFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                var remaining = new List<string>();

                foreach (var pluginId in lines)
                {
                    var dir = Path.Combine(PluginRootDirectory, SanitizeFolderName(pluginId));
                    if (Directory.Exists(dir))
                    {
                        try
                        {
                            Directory.Delete(dir, recursive: true);
                            LogService.LogInfo(I18n.Format("PluginService_PendingDeleteSuccess", pluginId), "PluginService");
                        }
                        catch
                        {
                            remaining.Add(pluginId);
                        }
                    }
                }

                if (remaining.Count > 0)
                {
                    File.WriteAllLines(pendingFile, remaining);
                }
                else
                {
                    File.Delete(pendingFile);
                }
            }
            catch { }
        }

        private static void QueuePendingUpdate(string pluginId, string packagePath, bool restoreEnabled)
        {
            try
            {
                Directory.CreateDirectory(PluginRootDirectory);

                var pendingFile = Path.Combine(PluginRootDirectory, PendingUpdateFileName);
                var lines = File.Exists(pendingFile)
                    ? File.ReadAllLines(pendingFile)
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToList()
                    : new List<string>();

                // 同一个插件仅保留最后一次更新包
                lines.RemoveAll(l =>
                {
                    var parts = l.Split('|', 3);
                    return parts.Length == 3 && string.Equals(parts[0], pluginId, StringComparison.OrdinalIgnoreCase);
                });

                lines.Add($"{pluginId}|{packagePath}|{(restoreEnabled ? "1" : "0")}");
                File.WriteAllLines(pendingFile, lines);
            }
            catch (Exception ex)
            {
                LogService.LogWarning(I18n.Format("PluginService_QueueUpdateFailed_Log", pluginId, ex.Message), "PluginService");
            }
        }

        private static void ApplyPendingUpdates()
        {
            try
            {
                var pendingFile = Path.Combine(PluginRootDirectory, PendingUpdateFileName);
                if (!File.Exists(pendingFile)) return;

                var lines = File.ReadAllLines(pendingFile)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();

                var remaining = new List<string>();
                var saveSettings = false;
                var settings = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;

                foreach (var line in lines)
                {
                    var parts = line.Split('|', 3);
                    if (parts.Length != 3)
                    {
                        continue;
                    }

                    var pluginId = parts[0];
                    var packagePath = parts[1];
                    var restoreEnabled = string.Equals(parts[2], "1", StringComparison.OrdinalIgnoreCase);

                    if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
                    {
                        continue;
                    }

                    try
                    {
                        var result = InstallFromZipAsync(packagePath).GetAwaiter().GetResult();
                        if (result.Success)
                        {
                            if (settings != null)
                            {
                                settings.PluginEnabled[pluginId] = restoreEnabled;
                                saveSettings = true;
                            }

                            try { File.Delete(packagePath); } catch { }

                            LogService.LogInfo(I18n.Format("PluginService_PendingUpdateApplied_Log", pluginId), "PluginService");
                        }
                        else
                        {
                            remaining.Add(line);
                            LogService.LogWarning(I18n.Format("PluginService_PendingUpdateApplyFailed_Log", pluginId, result.Message), "PluginService");
                        }
                    }
                    catch (Exception ex)
                    {
                        remaining.Add(line);
                        LogService.LogWarning(I18n.Format("PluginService_PendingUpdateApplyFailed_Log", pluginId, ex.Message), "PluginService");
                    }
                }

                if (saveSettings)
                {
                    ConfigService.Save();
                }

                if (remaining.Count > 0)
                {
                    File.WriteAllLines(pendingFile, remaining);
                }
                else
                {
                    File.Delete(pendingFile);
                }
            }
            catch (Exception ex)
            {
                LogService.LogWarning(I18n.Format("PluginService_ApplyPendingUpdatesFailed_Log", ex.Message), "PluginService");
            }
        }

        /// <summary>
        /// 检查所有已安装插件的更新
        /// </summary>
        public static async Task CheckAllPluginUpdatesAsync(CancellationToken ct = default, bool respectAutoCheckSetting = true)
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
            if (settings == null) return;
            if (respectAutoCheckSetting && !settings.AutoCheckUpdates) return;

            foreach (var plugin in _installed.ToList())
            {
                if (ct.IsCancellationRequested) break;
                await CheckPluginUpdateAsync(plugin, ct);
            }
        }

        /// <summary>
        /// 检查单个插件的更新
        /// </summary>
        public static async Task CheckPluginUpdateAsync(InstalledPluginInfo plugin, CancellationToken ct = default)
        {
            if (plugin == null || string.IsNullOrWhiteSpace(plugin.Repository)) return;

            try
            {
                // 每次检查先清空旧状态，避免残留状态导致“误报有更新”
                plugin.HasUpdate = false;
                plugin.LatestVersion = null;
                plugin.UpdateDownloadUrl = null;

                var (hasUpdate, latestVersion, downloadUrl) = await CheckGitHubReleaseAsync(plugin.Repository, plugin.Version, ct);

                // 没有可下载链接时，不应视为可更新
                var actionable = hasUpdate && !string.IsNullOrWhiteSpace(downloadUrl);

                plugin.HasUpdate = actionable;
                plugin.LatestVersion = actionable ? latestVersion : null;
                plugin.UpdateDownloadUrl = actionable ? downloadUrl : null;
            }
            catch (Exception ex)
            {
                LogService.LogWarning(I18n.Format("PluginService_CheckUpdateFailed", plugin.Id, ex.Message), "PluginService");
            }
        }

        /// <summary>
        /// 检查 GitHub Release 获取最新版本
        /// </summary>
        private static async Task<(bool HasUpdate, string? LatestVersion, string? DownloadUrl)> CheckGitHubReleaseAsync(
            string repository, string currentVersion, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(repository)) return (false, null, null);

            // 格式: owner/repo
            var parts = repository.Split('/');
            if (parts.Length != 2) return (false, null, null);

            var url = $"https://api.github.com/repos/{parts[0]}/{parts[1]}/releases/latest";

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "FolderRewind-Plugin-Updater");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
            client.Timeout = TimeSpan.FromSeconds(15);

            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return (false, null, null);

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagElement)) return (false, null, null);

            var tagName = tagElement.GetString();
            if (string.IsNullOrWhiteSpace(tagName)) return (false, null, null);

            // 清理版本号（移除 v 前缀）
            var latestVersion = tagName.TrimStart('v', 'V');
            var current = currentVersion?.TrimStart('v', 'V') ?? "0.0.0";

            // 比较版本
            bool hasUpdate = false;
            try
            {
                var latestVer = Version.Parse(latestVersion);
                var currentVer = Version.Parse(current);
                hasUpdate = latestVer > currentVer;
            }
            catch
            {
                // 版本格式不标准，使用字符串比较
                hasUpdate = !string.Equals(latestVersion, current, StringComparison.OrdinalIgnoreCase);
            }

            // 获取下载链接（优先找 .zip 文件）
            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("browser_download_url", out var urlElement))
                    {
                        var assetUrl = urlElement.GetString();
                        if (!string.IsNullOrWhiteSpace(assetUrl) && assetUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = assetUrl;
                            break;
                        }
                    }
                }
            }

            // 如果没有 .zip 资源，使用 zipball_url
            if (string.IsNullOrWhiteSpace(downloadUrl) && root.TryGetProperty("zipball_url", out var zipballElement))
            {
                downloadUrl = zipballElement.GetString();
            }

            return (hasUpdate, latestVersion, downloadUrl);
        }

        /// <summary>
        /// 从 URL 更新插件
        /// </summary>
        public static async Task<(bool Success, string Message)> UpdatePluginFromUrlAsync(
            InstalledPluginInfo plugin, CancellationToken ct = default)
        {
            if (plugin == null || string.IsNullOrWhiteSpace(plugin.UpdateDownloadUrl))
            {
                return (false, _rl.GetString("PluginService_NoUpdateUrl"));
            }

            try
            {
                var wasEnabled = GetPluginEnabled(plugin.Id);

                // 下载文件
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "FolderRewind-Plugin-Updater");
                client.Timeout = TimeSpan.FromMinutes(5);

                var response = await client.GetAsync(plugin.UpdateDownloadUrl, ct);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, string.Format(_rl.GetString("PluginService_DownloadFailed"), response.StatusCode));
                }

                // 保存到临时文件
                var tempFile = Path.Combine(Path.GetTempPath(), $"FolderRewind_Plugin_{plugin.Id}_{Guid.NewGuid():N}.zip");
                await using (var fs = File.Create(tempFile))
                {
                    await response.Content.CopyToAsync(fs, ct);
                }

                // 先卸载旧版本
                var unloadSuccess = TryUnloadPlugin(plugin.Id);
                if (!unloadSuccess)
                {
                    QueuePendingUpdate(plugin.Id, tempFile, wasEnabled);
                    return (true, _rl.GetString("PluginService_UpdatePendingRestart"));
                }

                // 安装新版本
                var result = await InstallFromZipAsync(tempFile, ct);

                if (!result.Success)
                {
                    // 大概率是文件占用导致覆盖失败，回退为“下次启动应用更新”
                    QueuePendingUpdate(plugin.Id, tempFile, wasEnabled);
                    return (true, _rl.GetString("PluginService_UpdatePendingRestart"));
                }

                // 清理临时文件
                try { File.Delete(tempFile); } catch { }

                if (result.Success)
                {
                    // 重新启用插件
                    if (wasEnabled)
                    {
                        TryLoadPlugin(plugin.Id);
                    }

                    // 清除更新标记
                    plugin.HasUpdate = false;
                    plugin.LatestVersion = null;
                    plugin.UpdateDownloadUrl = null;
                }

                return result;
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("PluginService_UpdateFailed", plugin.Id, ex.Message), "PluginService", ex);
                return (false, string.Format(_rl.GetString("PluginService_UpdateFailed"), ex.Message));
            }
        }

        public static void OpenPluginFolder()
        {
            try
            {
                Directory.CreateDirectory(PluginRootDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = PluginRootDirectory,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("PluginService_OpenPluginFolderFailed", ex.Message), "PluginService", ex);
            }
        }

        private static void TryLoadEnabledPlugins()
        {
            if (!IsPluginSystemEnabled()) return;

            // 确保 installed 已刷新
            foreach (var plugin in _installed.ToList())
            {
                var enabled = GetPluginEnabled(plugin.Id);
                plugin.IsEnabled = enabled;

                if (enabled)
                {
                    TryLoadPlugin(plugin.Id);
                }
            }
        }

        private static bool TryLoadPlugin(string pluginId)
        {
            lock (_lock)
            {
                if (_loaded.ContainsKey(pluginId)) return true;

                var installed = _installed.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
                if (installed == null) return false;

                // 从安装目录读取 manifest
                var dir = installed.InstallPath;
                var manifestPath = Path.Combine(dir, ManifestFileName);
                if (!File.Exists(manifestPath))
                {
                    installed.LoadError = _rl.GetString("PluginService_Load_MissingManifest");
                    return false;
                }

                PluginInstallManifest? manifest;
                try
                {
                    var json = File.ReadAllText(manifestPath);
                    manifest = JsonSerializer.Deserialize(json, AppJsonContext.Default.PluginInstallManifest);
                }
                catch (Exception ex)
                {
                    installed.LoadError = _rl.GetString("PluginService_Load_ManifestParseFailed");
                    LogService.LogError(I18n.Format("PluginService_ManifestParseFailed", pluginId, ex.Message), "PluginService", ex);
                    return false;
                }

                if (manifest != null)
                {
                    ApplyManifestLocalization(manifest);
                }

                if (manifest == null || string.IsNullOrWhiteSpace(manifest.EntryAssembly) || string.IsNullOrWhiteSpace(manifest.EntryType))
                {
                    installed.LoadError = _rl.GetString("PluginService_Load_MissingEntry");
                    return false;
                }

                // 版本兼容性检查
                if (!IsVersionCompatible(manifest.MinHostVersion, out var incompatibleReason))
                {
                    installed.LoadError = incompatibleReason;
                    LogService.LogWarning(I18n.Format("PluginService_VersionIncompatible", pluginId, incompatibleReason ?? string.Empty), "PluginService");
                    return false;
                }

                var entryAssemblyPath = Path.Combine(dir, manifest.EntryAssembly);
                if (!File.Exists(entryAssemblyPath))
                {
                    installed.LoadError = _rl.GetString("PluginService_Load_EntryAssemblyMissing");
                    return false;
                }

                try
                {
                    var alc = new PluginLoadContext(dir);
                    var asm = alc.LoadFromAssemblyPath(entryAssemblyPath);
                    var type = asm.GetType(manifest.EntryType, throwOnError: true, ignoreCase: false);

                    if (type == null)
                    {
                        installed.LoadError = _rl.GetString("PluginService_Load_EntryTypeMissing");
                        return false;
                    }

                    if (!typeof(IFolderRewindPlugin).IsAssignableFrom(type))
                    {
                        installed.LoadError = _rl.GetString("PluginService_Load_EntryTypeNotPlugin");
                        return false;
                    }

                    var instance = (IFolderRewindPlugin?)Activator.CreateInstance(type);
                    if (instance == null)
                    {
                        installed.LoadError = _rl.GetString("PluginService_Load_CreateInstanceFailed");
                        return false;
                    }

                    // 初始化插件（仅在启用时）
                    var settings = GetPluginSettings(manifest.Id);
                    instance.Initialize(settings);

                    // 注入宿主上下文，供插件主动使用 KnotLink 等能力
                    try
                    {
                        var hostCtx = PluginHostContext.CreateForCurrentApp(manifest.Id, manifest.Name ?? string.Empty);
                        instance.SetHostContext(hostCtx);
                    }
                    catch (Exception ctxEx)
                    {
                        LogService.LogWarning($"Plugin {manifest.Id}: SetHostContext failed: {ctxEx.Message}", "PluginService");
                    }

                    _loaded[manifest.Id] = new LoadedPlugin(manifest, instance, alc);
                    installed.LoadError = null;

                    LogService.LogInfo(I18n.Format("PluginService_Loaded", manifest.Id, manifest.Name ?? string.Empty, manifest.Version ?? string.Empty), "PluginService");
                    return true;
                }
                catch (Exception ex)
                {
                    installed.LoadError = ex.Message;
                    LogService.LogError(I18n.Format("PluginService_LoadFailed", pluginId, ex.Message), "PluginService", ex);
                    return false;
                }
            }
        }

        private static InstalledPluginInfo? ReadInstalledPluginInfo(string pluginDir)
        {
            try
            {
                var manifestPath = Path.Combine(pluginDir, ManifestFileName);
                if (!File.Exists(manifestPath)) return null;

                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize(json, AppJsonContext.Default.PluginInstallManifest);
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id)) return null;

                ApplyManifestLocalization(manifest);

                var enabled = GetPluginEnabled(manifest.Id);
                return new InstalledPluginInfo
                {
                    Id = manifest.Id,
                    Name = string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name,
                    Version = manifest.Version ?? string.Empty,
                    Author = manifest.Author ?? string.Empty,
                    Description = manifest.Description ?? string.Empty,
                    InstallPath = pluginDir,
                    IsEnabled = enabled,
                    Repository = manifest.Repository,
                    Homepage = manifest.Homepage
                };
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("PluginService_ReadInfoFailed", pluginDir, ex.Message), "PluginService", ex);
                return null;
            }
        }

        private static void ApplyManifestLocalization(PluginInstallManifest manifest)
        {
            if (manifest == null) return;

            var name = I18n.PickBest(manifest.LocalizedName, manifest.Name);
            if (!string.IsNullOrWhiteSpace(name)) manifest.Name = name;

            var desc = I18n.PickBest(manifest.LocalizedDescription, manifest.Description);
            if (!string.IsNullOrWhiteSpace(desc)) manifest.Description = desc;
        }

        private static IReadOnlyList<IFolderRewindPlugin> GetEnabledLoadedPluginsSnapshot()
        {
            // 注意：这里尽量少锁，避免备份过程中 UI 卡顿。
            lock (_lock)
            {
                var settings = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
                if (settings == null) return Array.Empty<IFolderRewindPlugin>();

                return _loaded.Values
                    .Where(p => settings.PluginEnabled.TryGetValue(p.Manifest.Id, out var en) && en)
                    .Select(p => p.Instance)
                    .ToList();
            }
        }

        private static string NormalizeZipPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            // zip entry 使用 / 分隔
            var p = path.Replace('\\', '/');
            // 去掉开头的 /，防止 Path.Combine 变成绝对路径
            while (p.StartsWith("/", StringComparison.Ordinal)) p = p[1..];
            return p;
        }

        private static string SanitizeFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "unknown";

            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            return name.Trim();
        }

        private static string GetWritableAppDataDir()
        {
            // Packaged (MSIX) 下：LocalState
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                if (!string.IsNullOrWhiteSpace(localFolder?.Path)) return localFolder.Path;
            }
            catch
            {
            }

            // Unpackaged 下：常规 LocalAppData
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        private sealed record LoadedPlugin(PluginInstallManifest Manifest, IFolderRewindPlugin Instance, PluginLoadContext LoadContext);
    }
}

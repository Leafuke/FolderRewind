using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
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
                }
                catch (Exception ex)
                {
                    LogService.LogError($"[Plugin] 无法创建插件目录：{ex.Message}", "PluginService", ex);
                }

                // 扫描安装清单（不一定加载插件）
                RefreshInstalledList();

                // 根据总开关/启用状态加载
                TryLoadEnabledPlugins();

                _initialized = true;
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
                    LogService.LogError($"[Plugin] 扫描插件失败：{ex.Message}", "PluginService", ex);
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
                    LogService.LogError($"[Plugin] 重新初始化失败：{pluginId} - {ex.Message}", "PluginService", ex);
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
                    LogService.LogError($"[Plugin] 获取设置定义失败：{pluginId} - {ex.Message}", "PluginService", ex);
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
                    LogService.LogError($"[Plugin] BeforeBackup 失败：{plugin.Manifest.Id} - {ex.Message}", "PluginService", ex);
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
                    LogService.LogError($"[Plugin] AfterBackup 失败：{plugin.Manifest.Id} - {ex.Message}", "PluginService", ex);
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
                    LogService.LogError($"[Plugin] DiscoverManagedFolders 失败：{plugin.Manifest.Id} - {ex.Message}", "PluginService", ex);
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
                    LogService.LogError($"[Plugin] GetSupportedConfigTypes 失败：{plugin.Manifest.Id} - {ex.Message}", "PluginService", ex);
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
                    LogService.LogError($"[Plugin] WantsToHandleBackup 失败：{plugin.Manifest.Id} - {ex.Message}", "PluginService", ex);
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
                    LogService.LogError($"[Plugin] WantsToHandleRestore 失败：{plugin.Manifest.Id} - {ex.Message}", "PluginService", ex);
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
                LogService.LogError($"[Plugin] PerformBackupAsync 失败：{plugin.Manifest.Id} - {ex.Message}", "PluginService", ex);
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
                LogService.LogError($"[Plugin] PerformRestoreAsync 失败：{plugin.Manifest.Id} - {ex.Message}", "PluginService", ex);
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
                    LogService.LogError($"[Plugin] TryCreateConfigs 失败：{plugin.Manifest.Id} - {ex.Message}", "PluginService", ex);
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

                var targetDir = Path.Combine(PluginRootDirectory, SanitizeFolderName(manifest.Id));

                // 覆盖安装：先删除原目录
                if (Directory.Exists(targetDir))
                {
                    try { Directory.Delete(targetDir, recursive: true); }
                    catch (Exception ex) { return (false, string.Format(_rl.GetString("PluginService_OverwriteFailed"), ex.Message)); }
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
                LogService.LogError($"[Plugin] 安装失败：{ex.Message}", "PluginService", ex);
                return (false, string.Format(_rl.GetString("PluginService_InstallFailed"), ex.Message));
            }
        }

        public static (bool Success, string Message) Uninstall(string pluginId)
        {
            if (string.IsNullOrWhiteSpace(pluginId)) return (false, _rl.GetString("PluginService_EmptyPluginId"));

            try
            {
                var dir = Path.Combine(PluginRootDirectory, SanitizeFolderName(pluginId));
                if (!Directory.Exists(dir)) return (false, _rl.GetString("PluginService_NotInstalled"));

                // 注意：已加载的程序集无法真正卸载（本设计不做热卸载）。
                // 这里仍允许删除文件，但某些情况下可能失败（文件被占用）。
                Directory.Delete(dir, recursive: true);

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
                LogService.LogError($"[Plugin] 卸载失败：{pluginId} - {ex.Message}", "PluginService", ex);
                return (false, string.Format(_rl.GetString("PluginService_UninstallFailed"), ex.Message));
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
                LogService.LogError($"[Plugin] 打开插件目录失败：{ex.Message}", "PluginService", ex);
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
                    LogService.LogError($"[Plugin] manifest 解析失败：{pluginId} - {ex.Message}", "PluginService", ex);
                    return false;
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
                    LogService.LogWarning($"[Plugin] 版本不兼容：{pluginId} - {incompatibleReason}", "PluginService");
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

                    _loaded[manifest.Id] = new LoadedPlugin(manifest, instance, alc);
                    installed.LoadError = null;

                    LogService.LogInfo($"[Plugin] 已加载：{manifest.Id} ({manifest.Name} {manifest.Version})", "PluginService");
                    return true;
                }
                catch (Exception ex)
                {
                    installed.LoadError = ex.Message;
                    LogService.LogError($"[Plugin] 加载失败：{pluginId} - {ex.Message}", "PluginService", ex);
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

                var enabled = GetPluginEnabled(manifest.Id);
                return new InstalledPluginInfo
                {
                    Id = manifest.Id,
                    Name = string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name,
                    Version = manifest.Version ?? string.Empty,
                    Author = manifest.Author ?? string.Empty,
                    Description = manifest.Description ?? string.Empty,
                    InstallPath = pluginDir,
                    IsEnabled = enabled
                };
            }
            catch (Exception ex)
            {
                LogService.LogError($"[Plugin] 读取插件信息失败：{pluginDir} - {ex.Message}", "PluginService", ex);
                return null;
            }
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

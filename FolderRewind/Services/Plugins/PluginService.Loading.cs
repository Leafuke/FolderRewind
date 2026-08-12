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
    public static partial class PluginService
    {
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
            if (PluginRuntimeModeService.IsSafeMode) return false;

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

        private sealed record LoadedPlugin(PluginInstallManifest Manifest, IFolderRewindPlugin Instance, PluginLoadContext LoadContext);
    }
}

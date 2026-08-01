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
    }
}

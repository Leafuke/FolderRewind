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
        public static async Task<PluginConfigAugmentationRunResult> RunConfigAugmentationAsync(
            PluginConfigAugmentationReason reason,
            IEnumerable<string>? pluginIds = null)
        {
            if (!IsPluginSystemEnabled())
            {
                return new PluginConfigAugmentationRunResult();
            }

            await _configAugmentationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var currentConfig = ConfigService.CurrentConfig;
                var liveConfigs = currentConfig?.BackupConfigs?.ToList() ?? new List<BackupConfig>();
                var configSnapshot = liveConfigs
                    .Select(CloneConfigForAugmentationSnapshot)
                    .ToList();

                HashSet<string>? pluginFilter = pluginIds == null
                    ? null
                    : new HashSet<string>(pluginIds, StringComparer.OrdinalIgnoreCase);

                var pendingItems = new List<(string PluginId, PluginConfigAugmentationItem Item)>();
                var pendingConfigs = new List<(string PluginId, BackupConfig Config)>();

                foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
                {
                    if (plugin is not IFolderRewindConfigAugmenter augmenter)
                    {
                        continue;
                    }

                    if (pluginFilter != null && !pluginFilter.Contains(plugin.Manifest.Id))
                    {
                        continue;
                    }

                    try
                    {
                        var result = augmenter.AugmentConfigs(
                            new PluginConfigAugmentationRequest
                            {
                                Reason = reason,
                                Configs = configSnapshot
                            },
                            GetPluginSettings(plugin.Manifest.Id));

                        if (!result.Handled)
                        {
                            continue;
                        }

                        foreach (var item in (result.Items ?? Array.Empty<PluginConfigAugmentationItem>())
                                     .Where(static entry => entry != null && !string.IsNullOrWhiteSpace(entry.ConfigId)))
                        {
                            pendingItems.Add((plugin.Manifest.Id, item));
                        }

                        foreach (var config in (result.ConfigsToAdd ?? Array.Empty<BackupConfig>())
                                     .Where(static entry => entry != null))
                        {
                            pendingConfigs.Add((plugin.Manifest.Id, config));
                        }
                    }
                    catch (Exception ex)
                    {
                        LogService.LogError(
                            $"[PluginService] Config augmentation failed: {plugin.Manifest.Id}: {ex.Message}",
                            "PluginService",
                            ex);
                    }
                }

                if (pendingItems.Count == 0 && pendingConfigs.Count == 0)
                {
                    return new PluginConfigAugmentationRunResult();
                }

                var addedByPlugin = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var addedConfigsByPlugin = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var touchedConfigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                await UiDispatcherService.RunOnUiAsync(() =>
                {
                    foreach (var (pluginId, item) in pendingItems)
                    {
                        var targetConfig = currentConfig?.BackupConfigs?
                            .FirstOrDefault(config => string.Equals(config.Id, item.ConfigId, StringComparison.OrdinalIgnoreCase));
                        if (targetConfig == null)
                        {
                            continue;
                        }

                        int addedCount = TryApplyAugmentationItem(targetConfig, item);
                        if (addedCount <= 0)
                        {
                            continue;
                        }

                        touchedConfigs.Add(targetConfig.Id);
                        addedByPlugin[pluginId] = addedByPlugin.TryGetValue(pluginId, out var currentAdded)
                            ? currentAdded + addedCount
                            : addedCount;
                    }

                    var knownSourcePaths = new HashSet<string>(
                        currentConfig?.BackupConfigs?
                            .SelectMany(config => config.SourceFolders ?? Enumerable.Empty<ManagedFolder>())
                            .Select(folder => PluginConfigAdditionPolicy.NormalizePath(folder.Path))
                            .Where(static path => !string.IsNullOrWhiteSpace(path))
                        ?? Enumerable.Empty<string>(),
                        StringComparer.OrdinalIgnoreCase);
                    var usedNames = new HashSet<string>(
                        currentConfig?.BackupConfigs?
                            .Select(config => config.Name?.Trim() ?? string.Empty)
                            .Where(static name => !string.IsNullOrWhiteSpace(name))
                        ?? Enumerable.Empty<string>(),
                        StringComparer.OrdinalIgnoreCase);
                    var usedDestinationPaths = new HashSet<string>(
                        currentConfig?.BackupConfigs?
                            .Select(config => PluginConfigAdditionPolicy.NormalizePath(config.DestinationPath))
                            .Where(static path => !string.IsNullOrWhiteSpace(path))
                        ?? Enumerable.Empty<string>(),
                        StringComparer.OrdinalIgnoreCase);

                    foreach (var (pluginId, candidate) in pendingConfigs)
                    {
                        try
                        {
                            var prepared = TryPrepareAugmentedConfig(
                                candidate,
                                knownSourcePaths,
                                usedNames,
                                usedDestinationPaths);
                            if (prepared == null || currentConfig?.BackupConfigs == null)
                            {
                                continue;
                            }

                            currentConfig.BackupConfigs.Add(prepared);
                            foreach (var folder in prepared.SourceFolders)
                            {
                                string path = PluginConfigAdditionPolicy.NormalizePath(folder.Path);
                                if (!string.IsNullOrWhiteSpace(path))
                                {
                                    knownSourcePaths.Add(path);
                                }
                            }

                            usedNames.Add(prepared.Name);
                            usedDestinationPaths.Add(PluginConfigAdditionPolicy.NormalizePath(prepared.DestinationPath));
                            addedConfigsByPlugin[pluginId] = addedConfigsByPlugin.TryGetValue(pluginId, out int currentAdded)
                                ? currentAdded + 1
                                : 1;
                        }
                        catch (Exception ex)
                        {
                            LogService.LogError(
                                $"[PluginService] Failed to apply augmented config from '{pluginId}': {ex.Message}",
                                "PluginService",
                                ex);
                        }
                    }

                    if (touchedConfigs.Count > 0 || addedConfigsByPlugin.Count > 0)
                    {
                        ConfigService.Save();
                        NotifyConfigAugmentationAdded(addedByPlugin, addedConfigsByPlugin);
                    }
                }).ConfigureAwait(false);

                var touchedPluginIds = new HashSet<string>(addedByPlugin.Keys, StringComparer.OrdinalIgnoreCase);
                touchedPluginIds.UnionWith(addedConfigsByPlugin.Keys);

                return new PluginConfigAugmentationRunResult
                {
                    AddedFolderCount = addedByPlugin.Values.Sum(),
                    AddedConfigCount = addedConfigsByPlugin.Values.Sum(),
                    UpdatedConfigCount = touchedConfigs.Count,
                    TouchedPluginIds = touchedPluginIds.ToArray()
                };
            }
            finally
            {
                _configAugmentationLock.Release();
            }
        }

        private static BackupConfig CloneConfigForAugmentationSnapshot(BackupConfig source)
        {
            try
            {
                return BackupConfigCloneService.CloneForRuntimeMutation(
                    source,
                    "Failed to clone backup config for plugin augmentation snapshot.");
            }
            catch (Exception ex)
            {
                LogService.LogWarning(
                    $"[PluginService] Failed to clone config '{source?.Id}'; using a shallow augmentation snapshot: {ex.Message}",
                    "PluginService");

                var clone = new BackupConfig
                {
                    Id = source?.Id ?? string.Empty,
                    Name = source?.Name ?? string.Empty,
                    DestinationPath = source?.DestinationPath ?? string.Empty,
                    ConfigType = source?.ConfigType ?? "Default",
                    IconGlyph = source?.IconGlyph ?? string.Empty,
                    IsEncrypted = source?.IsEncrypted == true,
                    ExtendedProperties = source?.ExtendedProperties == null
                        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(source.ExtendedProperties, StringComparer.OrdinalIgnoreCase)
                };

                foreach (var folder in (source?.SourceFolders as IEnumerable<ManagedFolder>) ?? Array.Empty<ManagedFolder>())
                {
                    clone.SourceFolders.Add(CloneAugmentedFolder(folder));
                }

                return clone;
            }
        }

        private static int TryApplyAugmentationItem(BackupConfig config, PluginConfigAugmentationItem item)
        {
            if (config.SourceFolders == null)
            {
                config.SourceFolders = new ObservableCollection<ManagedFolder>();
            }

            var knownPaths = new HashSet<string>(
                config.SourceFolders.Select(folder => folder.Path ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);
            var knownDisplayNames = new HashSet<string>(
                config.SourceFolders.Select(FolderNameConflictService.ResolveDisplayName),
                StringComparer.OrdinalIgnoreCase);

            int added = 0;
            foreach (var folder in item.FoldersToAdd.Where(static folder => folder != null && !string.IsNullOrWhiteSpace(folder.Path)))
            {
                string candidatePath = folder.Path.Trim();
                string candidateName = FolderNameConflictService.ResolveDisplayName(folder);

                if (knownPaths.Contains(candidatePath) || knownDisplayNames.Contains(candidateName))
                {
                    continue;
                }

                config.SourceFolders.Add(CloneAugmentedFolder(folder));
                knownPaths.Add(candidatePath);
                knownDisplayNames.Add(candidateName);
                added++;
            }

            return added;
        }

        private static ManagedFolder CloneAugmentedFolder(ManagedFolder source)
        {
            return new ManagedFolder
            {
                Path = source.Path?.Trim() ?? string.Empty,
                DisplayName = source.DisplayName?.Trim() ?? string.Empty,
                Description = source.Description?.Trim() ?? string.Empty,
                CoverImagePath = source.CoverImagePath?.Trim() ?? string.Empty
            };
        }

        private static BackupConfig? TryPrepareAugmentedConfig(
            BackupConfig candidate,
            ISet<string> knownSourcePaths,
            ISet<string> usedNames,
            ISet<string> usedDestinationPaths)
        {
            var sourcePaths = (candidate.SourceFolders ?? Enumerable.Empty<ManagedFolder>())
                .Where(static folder => folder != null && !string.IsNullOrWhiteSpace(folder.Path))
                .Select(folder => PluginConfigAdditionPolicy.NormalizePath(folder.Path))
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .ToArray();

            if (sourcePaths.Length == 0
                || sourcePaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sourcePaths.Length
                || sourcePaths.Any(knownSourcePaths.Contains))
            {
                return null;
            }

            var clone = BackupConfigCloneService.CloneForRuntimeMutation(
                candidate,
                "Failed to clone plugin-created backup config.");
            clone.Id = Guid.NewGuid().ToString();
            clone.Name = PluginConfigAdditionPolicy.ResolveUniqueName(
                candidate.Name,
                usedNames,
                usedDestinationPaths,
                ConfigService.BuildDefaultDestinationPath,
                out string destinationPath);
            clone.DestinationPath = destinationPath;
            clone.IsEncrypted = false;
            clone.Automation = new AutomationSettings();
            clone.Cloud = new CloudSettings
            {
                RemoteBasePath = ConfigService.GetRecommendedDefaultCloudRemoteBasePath()
            };
            return clone;
        }

        private static void NotifyConfigAugmentationAdded(
            IReadOnlyDictionary<string, int> addedFoldersByPlugin,
            IReadOnlyDictionary<string, int> addedConfigsByPlugin)
        {
            var pluginIds = new HashSet<string>(addedFoldersByPlugin.Keys, StringComparer.OrdinalIgnoreCase);
            pluginIds.UnionWith(addedConfigsByPlugin.Keys);

            foreach (string pluginId in pluginIds)
            {
                string pluginName = _installed
                    .FirstOrDefault(item => string.Equals(item.Id, pluginId, StringComparison.OrdinalIgnoreCase))
                    ?.Name ?? pluginId;
                int folderCount = addedFoldersByPlugin.TryGetValue(pluginId, out int addedFolders) ? addedFolders : 0;
                int configCount = addedConfigsByPlugin.TryGetValue(pluginId, out int addedConfigs) ? addedConfigs : 0;

                string message = configCount > 0 && folderCount > 0
                    ? I18n.Format("PluginService_ConfigAugmentationAdded_CombinedMessage", pluginName, configCount, folderCount)
                    : configCount > 0
                        ? I18n.Format("PluginService_ConfigAugmentationAdded_ConfigMessage", pluginName, configCount)
                        : I18n.Format("PluginService_ConfigAugmentationAdded_Message", pluginName, folderCount);

                NotificationService.ShowInfo(
                    message,
                    I18n.GetString("PluginService_ConfigAugmentationAdded_Title"));
            }
        }
    }
}

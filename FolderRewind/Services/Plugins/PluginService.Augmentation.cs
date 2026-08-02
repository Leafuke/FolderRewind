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
                if (liveConfigs.Count == 0)
                {
                    return new PluginConfigAugmentationRunResult();
                }

                var configSnapshot = liveConfigs
                    .Select(CloneConfigForAugmentationSnapshot)
                    .ToList();

                HashSet<string>? pluginFilter = pluginIds == null
                    ? null
                    : new HashSet<string>(pluginIds, StringComparer.OrdinalIgnoreCase);

                var pendingItems = new List<(string PluginId, PluginConfigAugmentationItem Item)>();

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

                        if (!result.Handled || result.Items == null || result.Items.Count == 0)
                        {
                            continue;
                        }

                        foreach (var item in result.Items.Where(static entry => entry != null && !string.IsNullOrWhiteSpace(entry.ConfigId)))
                        {
                            pendingItems.Add((plugin.Manifest.Id, item));
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

                if (pendingItems.Count == 0)
                {
                    return new PluginConfigAugmentationRunResult();
                }

                var addedByPlugin = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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

                    if (touchedConfigs.Count > 0)
                    {
                        ConfigService.Save();
                        NotifyConfigAugmentationAdded(addedByPlugin);
                    }
                }).ConfigureAwait(false);

                return new PluginConfigAugmentationRunResult
                {
                    AddedFolderCount = addedByPlugin.Values.Sum(),
                    UpdatedConfigCount = touchedConfigs.Count,
                    TouchedPluginIds = addedByPlugin.Keys.ToArray()
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

        private static void NotifyConfigAugmentationAdded(IReadOnlyDictionary<string, int> addedByPlugin)
        {
            foreach (var pair in addedByPlugin.Where(static entry => entry.Value > 0))
            {
                string pluginName = _installed
                    .FirstOrDefault(item => string.Equals(item.Id, pair.Key, StringComparison.OrdinalIgnoreCase))
                    ?.Name ?? pair.Key;

                NotificationService.ShowInfo(
                    I18n.Format("PluginService_ConfigAugmentationAdded_Message", pluginName, pair.Value),
                    I18n.GetString("PluginService_ConfigAugmentationAdded_Title"));
            }
        }
    }
}

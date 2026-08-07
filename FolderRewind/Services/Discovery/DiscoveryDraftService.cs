using FolderRewind.Models;
using FolderRewind.Services.Plugins;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Discovery;

public static class DiscoveryDraftService
{
    public static BackupConfigDraft CreateDraft(
        DiscoveredGameCandidate game,
        BackupSetCandidate backupSet,
        IEnumerable<BackupPreset>? availablePresets,
        IEnumerable<BackupConfig>? existingConfigs,
        string manifestRevision,
        IReadOnlySet<string>? selectedResourceIds = null,
        BackupPreset? selectedPreset = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(backupSet);

        var issues = new List<BackupConfigDraftIssue>();
        var matchingPresets = FindMatchingPresets(game, availablePresets).ToList();
        var recommended = matchingPresets.Where(preset => preset.IsRecommended).ToList();
        var preset = selectedPreset;
        if (preset == null)
        {
            preset = recommended.Count == 1
                ? recommended[0]
                : BackupPresetService.CreateStandardGamePreset();
            if (recommended.Count > 1)
            {
                issues.Add(new BackupConfigDraftIssue
                {
                    Code = "multiple-recommended-presets",
                    Message = "More than one recommended preset matched. The standard preset was selected instead."
                });
            }
        }

        var unavailablePlugins = BackupPresetService.GetMissingRequiredPluginIds(preset)
            .Concat((preset.RequiredPluginIds ?? new ObservableCollection<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id) && !PluginService.GetPluginEnabled(id)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unavailablePlugins.Count > 0)
        {
            issues.Add(new BackupConfigDraftIssue
            {
                Code = "missing-required-plugins",
                Message = $"Required plugins are missing or disabled: {string.Join(", ", unavailablePlugins)}.",
                IsBlocking = true
            });
        }

        string? configTypeOverride = preset.IsBuiltIn
                                     && !string.IsNullOrWhiteSpace(backupSet.SuggestedConfigType)
                                     && !string.Equals(backupSet.SuggestedConfigType, "Default", StringComparison.OrdinalIgnoreCase)
            ? backupSet.SuggestedConfigType
            : null;
        var presetResult = BackupPresetService.CreateConfigFromTemplate(
            preset,
            backupSet.DisplayName,
            configTypeOverride);
        BackupConfig config;
        if (!presetResult.Success || presetResult.Config == null)
        {
            issues.Add(new BackupConfigDraftIssue
            {
                Code = "preset-unavailable",
                Message = string.IsNullOrWhiteSpace(presetResult.Message)
                    ? "The selected preset cannot be applied on this host."
                    : presetResult.Message,
                IsBlocking = true
            });
            config = BackupPresetService.CreateConfigFromTemplate(
                BackupPresetService.CreateStandardGamePreset(),
                backupSet.DisplayName).Config ?? new BackupConfig { Name = backupSet.DisplayName };
        }
        else
        {
            config = presetResult.Config;
        }

        config.HistoryMode = BackupHistoryMode.GroupedRun;
        var plans = DiscoveryResourcePlanner.CreatePlans(backupSet.Resources, selectedResourceIds);
        var selectedResources = backupSet.Resources
            .Where(resource => plans.Any(plan => plan.ResourceIds.Contains(resource.ResourceId, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        if (plans.Count == 0)
        {
            issues.Add(new BackupConfigDraftIssue
            {
                Code = "no-supported-resources",
                Message = "No supported filesystem resources are selected.",
                IsBlocking = true
            });
        }

        foreach (var folder in CreateManagedFolders(plans))
        {
            config.SourceFolders.Add(folder);
        }
        config.DiscoveryOrigin = new DiscoveryOrigin
        {
            ProviderId = game.Definition.ProviderId,
            DefinitionId = game.Definition.DefinitionId,
            ExternalIds = new Dictionary<string, string>(game.Definition.ExternalIds, StringComparer.OrdinalIgnoreCase),
            ResourceIds = new ObservableCollection<string>(plans.SelectMany(plan => plan.ResourceIds).Distinct(StringComparer.OrdinalIgnoreCase)),
            PresetShareId = preset.ShareId,
            PresetVersion = preset.Version,
            ManifestRevision = manifestRevision ?? string.Empty
        };

        if (config.IsEncrypted && !EncryptionService.HasStoredPassword(config.Id))
        {
            issues.Add(new BackupConfigDraftIssue
            {
                Code = "encryption-password-required",
                Message = "An encryption password must be provided before this draft can be committed.",
                IsBlocking = true
            });
        }

        var existing = FindExistingConfiguration(game, existingConfigs);
        var foldersToAdd = new List<ManagedFolder>();
        var updates = new List<BackupManagedFolderUpdate>();
        var reconciliation = BackupConfigDraftReconciliation.NewConfiguration;
        if (existing != null)
        {
            BuildExistingConfigurationChanges(existing, config.SourceFolders, foldersToAdd, updates);
            reconciliation = foldersToAdd.Count == 0 && updates.Count == 0
                ? BackupConfigDraftReconciliation.UpToDate
                : BackupConfigDraftReconciliation.NewResources;
        }
        else
        {
            foldersToAdd.AddRange(config.SourceFolders);
        }

        return new BackupConfigDraft
        {
            Game = game,
            BackupSet = backupSet,
            AppliedPreset = preset,
            ProposedConfig = config,
            ExistingConfig = existing,
            Reconciliation = reconciliation,
            SelectedResources = selectedResources,
            FoldersToAdd = foldersToAdd,
            FolderUpdates = updates,
            Issues = issues
        };
    }

    public static async Task<GameDiscoveryResult> DiscoverPresetTargetsAsync(
        BackupPreset preset,
        GameDiscoveryService discoveryService,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(discoveryService);
        var definitions = preset.DiscoverySources
            .Where(source => source.Kind == BackupPresetDiscoverySourceKind.ProviderReference)
            .Where(source => !string.IsNullOrWhiteSpace(source.ProviderId)
                             && !string.IsNullOrWhiteSpace(source.DefinitionId))
            .Select(source => new DiscoveryDefinitionReference
            {
                ProviderId = source.ProviderId,
                DefinitionId = source.DefinitionId,
                ExternalIds = source.ExternalIds
            })
            .ToList();
        return await discoveryService.DiscoverAsync(
            new DiscoveryRequest
            {
                Mode = DiscoveryRequestMode.PresetTargeted,
                Definitions = definitions
            },
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    public static BackupConfigDraftCommitResult Commit(IEnumerable<BackupConfigDraft> drafts)
    {
        var selected = (drafts ?? Array.Empty<BackupConfigDraft>())
            .Where(draft => draft.IsSelected)
            .ToList();
        var blocking = selected.SelectMany(draft => draft.Issues).FirstOrDefault(issue => issue.IsBlocking);
        if (blocking != null)
        {
            return new BackupConfigDraftCommitResult { ErrorMessage = blocking.Message };
        }
        if (!selected.Any(draft => draft.IsCommittable))
        {
            return new BackupConfigDraftCommitResult { Success = true };
        }

        var newConfigs = selected
            .Where(draft => draft.Reconciliation == BackupConfigDraftReconciliation.NewConfiguration)
            .Select(draft => draft.ProposedConfig)
            .ToList();
        var validationError = ValidateCommit(newConfigs);
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            return new BackupConfigDraftCommitResult { ErrorMessage = validationError };
        }

        var addedConfigs = new List<BackupConfig>();
        var addedFolders = new List<(BackupConfig Config, ManagedFolder Folder)>();
        var updateSnapshots = new List<(ManagedFolder Folder, BackupSourceSelection Selection)>();
        var originSnapshots = new List<(BackupConfig Config, DiscoveryOrigin? Origin)>();
        var transaction = DiscoveryDraftTransaction.Execute(
            apply: () =>
            {
                foreach (var draft in selected.Where(draft => draft.IsCommittable))
                {
                    if (draft.Reconciliation == BackupConfigDraftReconciliation.NewConfiguration)
                    {
                        ConfigService.CurrentConfig.BackupConfigs.Add(draft.ProposedConfig);
                        addedConfigs.Add(draft.ProposedConfig);
                        continue;
                    }
                    if (draft.ExistingConfig == null)
                    {
                        continue;
                    }

                    foreach (var folder in draft.FoldersToAdd)
                    {
                        draft.ExistingConfig.SourceFolders.Add(folder);
                        addedFolders.Add((draft.ExistingConfig, folder));
                    }
                    foreach (var update in draft.FolderUpdates)
                    {
                        updateSnapshots.Add((update.ExistingFolder, CloneSelection(update.ExistingFolder.Selection)));
                        ApplyFolderUpdate(update);
                    }
                    originSnapshots.Add((draft.ExistingConfig, CloneOrigin(draft.ExistingConfig.DiscoveryOrigin)));
                    UpdateDiscoveryOrigin(draft.ExistingConfig, draft.ProposedConfig.DiscoveryOrigin, draft.SelectedResources);
                }
            },
            rollback: () => Rollback(addedConfigs, addedFolders, updateSnapshots, originSnapshots),
            save: () =>
            {
                var result = ConfigService.SaveWithResult(publishSavedEvent: false);
                return new DiscoveryDraftTransactionResult(result.Success, result.ErrorMessage);
            });
        if (!transaction.Success)
        {
            return new BackupConfigDraftCommitResult { ErrorMessage = transaction.ErrorMessage };
        }

        ConfigService.PublishSaved();
        return new BackupConfigDraftCommitResult
        {
            Success = true,
            AddedConfigurationCount = addedConfigs.Count,
            AddedSourceCount = addedFolders.Count + addedConfigs.Sum(config => config.SourceFolders.Count)
        };
    }

    private static IEnumerable<BackupPreset> FindMatchingPresets(
        DiscoveredGameCandidate game,
        IEnumerable<BackupPreset>? presets)
    {
        return (presets ?? Array.Empty<BackupPreset>()).Where(preset =>
            preset.DiscoverySources.Any(source =>
                source.Kind == BackupPresetDiscoverySourceKind.ProviderReference
                && string.Equals(source.ProviderId, game.Definition.ProviderId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(source.DefinitionId, game.Definition.DefinitionId, StringComparison.OrdinalIgnoreCase)));
    }

    private static IReadOnlyList<ManagedFolder> CreateManagedFolders(IReadOnlyList<DiscoveredSourcePlan> plans)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ManagedFolder>();
        foreach (var plan in plans)
        {
            var baseName = FolderNameConflictService.ResolveDisplayName(plan.DisplayName, plan.FixedRoot);
            var name = string.IsNullOrWhiteSpace(baseName) ? "Game data" : baseName;
            var suffix = 2;
            while (!names.Add(name))
            {
                name = $"{baseName} ({suffix++})";
            }
            result.Add(new ManagedFolder
            {
                Path = plan.FixedRoot,
                DisplayName = name,
                Selection = new BackupSourceSelection
                {
                    Mode = plan.SelectionMode,
                    IncludePatterns = new ObservableCollection<string>(plan.IncludePatterns),
                    ResourceIds = new ObservableCollection<string>(plan.ResourceIds)
                }
            });
        }
        return result;
    }

    private static BackupConfig? FindExistingConfiguration(
        DiscoveredGameCandidate game,
        IEnumerable<BackupConfig>? configs)
    {
        return (configs ?? Array.Empty<BackupConfig>()).FirstOrDefault(config =>
            config.DiscoveryOrigin != null
            && string.Equals(config.DiscoveryOrigin.ProviderId, game.Definition.ProviderId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(config.DiscoveryOrigin.DefinitionId, game.Definition.DefinitionId, StringComparison.OrdinalIgnoreCase));
    }

    private static void BuildExistingConfigurationChanges(
        BackupConfig existing,
        IEnumerable<ManagedFolder> proposedFolders,
        ICollection<ManagedFolder> foldersToAdd,
        ICollection<BackupManagedFolderUpdate> updates)
    {
        foreach (var proposed in proposedFolders)
        {
            var current = existing.SourceFolders.FirstOrDefault(folder =>
                string.Equals(
                    DiscoveryResourcePlanner.NormalizePath(folder.Path),
                    DiscoveryResourcePlanner.NormalizePath(proposed.Path),
                    StringComparison.OrdinalIgnoreCase));
            if (current == null)
            {
                foldersToAdd.Add(proposed);
                continue;
            }

            var currentSelection = current.Selection ?? new BackupSourceSelection();
            var resourceIds = proposed.Selection.ResourceIds
                .Except(currentSelection.ResourceIds ?? new ObservableCollection<string>(), StringComparer.OrdinalIgnoreCase)
                .ToList();
            var patterns = proposed.Selection.IncludePatterns
                .Except(currentSelection.IncludePatterns ?? new ObservableCollection<string>(), StringComparer.OrdinalIgnoreCase)
                .ToList();
            var setAll = proposed.Selection.Mode == BackupSourceSelectionMode.All
                         && currentSelection.Mode != BackupSourceSelectionMode.All;
            if (resourceIds.Count > 0 || patterns.Count > 0 || setAll)
            {
                updates.Add(new BackupManagedFolderUpdate
                {
                    ExistingFolder = current,
                    SetSelectionToAll = setAll,
                    IncludePatternsToAdd = patterns,
                    ResourceIdsToAdd = resourceIds
                });
            }
        }
    }

    private static string ValidateCommit(IReadOnlyList<BackupConfig> newConfigs)
    {
        if (newConfigs.Any(config => string.IsNullOrWhiteSpace(config.Name)))
        {
            return "Every configuration requires a name.";
        }
        if (newConfigs.Any(config => string.IsNullOrWhiteSpace(config.DestinationPath)))
        {
            return "Every configuration requires a destination directory.";
        }

        var existingNames = ConfigService.CurrentConfig.BackupConfigs
            .Select(config => config.Name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (newConfigs.GroupBy(config => config.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
            || newConfigs.Any(config => existingNames.Contains(config.Name.Trim())))
        {
            return "Configuration names must be unique.";
        }
        var existingDestinations = ConfigService.CurrentConfig.BackupConfigs
            .Select(config => FolderNameConflictService.NormalizeDestinationPath(config.DestinationPath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newDestinations = newConfigs
            .Select(config => FolderNameConflictService.NormalizeDestinationPath(config.DestinationPath))
            .ToList();
        if (newDestinations.GroupBy(path => path, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
            || newDestinations.Any(existingDestinations.Contains))
        {
            return "Configuration destination directories must be unique.";
        }
        return string.Empty;
    }

    private static void ApplyFolderUpdate(BackupManagedFolderUpdate update)
    {
        update.ExistingFolder.Selection ??= new BackupSourceSelection();
        if (update.SetSelectionToAll)
        {
            update.ExistingFolder.Selection.Mode = BackupSourceSelectionMode.All;
            update.ExistingFolder.Selection.IncludePatterns.Clear();
        }
        foreach (var pattern in update.IncludePatternsToAdd.Where(pattern =>
                     !update.ExistingFolder.Selection.IncludePatterns.Contains(pattern, StringComparer.OrdinalIgnoreCase)))
        {
            update.ExistingFolder.Selection.IncludePatterns.Add(pattern);
        }
        foreach (var id in update.ResourceIdsToAdd.Where(id =>
                     !update.ExistingFolder.Selection.ResourceIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
        {
            update.ExistingFolder.Selection.ResourceIds.Add(id);
        }
    }

    private static BackupSourceSelection CloneSelection(BackupSourceSelection source) => new()
    {
        Mode = source?.Mode ?? BackupSourceSelectionMode.All,
        IncludePatterns = new ObservableCollection<string>(source?.IncludePatterns ?? new ObservableCollection<string>()),
        ResourceIds = new ObservableCollection<string>(source?.ResourceIds ?? new ObservableCollection<string>())
    };

    private static DiscoveryOrigin? CloneOrigin(DiscoveryOrigin? source)
    {
        if (source == null)
        {
            return null;
        }
        return new DiscoveryOrigin
        {
            ProviderId = source.ProviderId,
            DefinitionId = source.DefinitionId,
            ExternalIds = new Dictionary<string, string>(source.ExternalIds, StringComparer.OrdinalIgnoreCase),
            ResourceIds = new ObservableCollection<string>(source.ResourceIds ?? new ObservableCollection<string>()),
            PresetShareId = source.PresetShareId,
            PresetVersion = source.PresetVersion,
            ManifestRevision = source.ManifestRevision
        };
    }

    private static void UpdateDiscoveryOrigin(
        BackupConfig config,
        DiscoveryOrigin? proposedOrigin,
        IEnumerable<BackupResourceCandidate> selectedResources)
    {
        config.DiscoveryOrigin ??= CloneOrigin(proposedOrigin) ?? new DiscoveryOrigin();
        config.DiscoveryOrigin.ResourceIds ??= new ObservableCollection<string>();
        foreach (var resourceId in selectedResources.Select(resource => resource.ResourceId)
                     .Where(id => !string.IsNullOrWhiteSpace(id))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!config.DiscoveryOrigin.ResourceIds.Contains(resourceId, StringComparer.OrdinalIgnoreCase))
            {
                config.DiscoveryOrigin.ResourceIds.Add(resourceId);
            }
        }
        if (proposedOrigin != null)
        {
            config.DiscoveryOrigin.ManifestRevision = proposedOrigin.ManifestRevision;
        }
    }

    private static void Rollback(
        IEnumerable<BackupConfig> addedConfigs,
        IEnumerable<(BackupConfig Config, ManagedFolder Folder)> addedFolders,
        IEnumerable<(ManagedFolder Folder, BackupSourceSelection Selection)> updateSnapshots,
        IEnumerable<(BackupConfig Config, DiscoveryOrigin? Origin)> originSnapshots)
    {
        foreach (var snapshot in originSnapshots.Reverse())
        {
            snapshot.Config.DiscoveryOrigin = snapshot.Origin;
        }
        foreach (var snapshot in updateSnapshots.Reverse())
        {
            snapshot.Folder.Selection = snapshot.Selection;
        }
        foreach (var added in addedFolders.Reverse())
        {
            added.Config.SourceFolders.Remove(added.Folder);
        }
        foreach (var config in addedConfigs.Reverse())
        {
            ConfigService.CurrentConfig.BackupConfigs.Remove(config);
        }
    }
}

using FolderRewind.Models;
using FolderRewind.Services.Plugins;
using FolderRewind.Services.Plugins.V3;
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
        var existing = FindExistingConfiguration(backupSet, existingConfigs);
        var matchingPresets = BackupPresetDiscoveryMatcher.FindMatches(game.Definition, availablePresets).ToList();
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
        if (existing == null && unavailablePlugins.Count > 0)
        {
            issues.Add(new BackupConfigDraftIssue
            {
                Code = "missing-required-plugins",
                Message = $"Required plugins are missing or disabled: {string.Join(", ", unavailablePlugins)}.",
                IsBlocking = true
            });
        }

        BackupConfig config;
        if (existing != null)
        {
            // Existing configurations are user-owned. A discovery review never reapplies a preset,
            // changes the display name, or changes the destination directory.
            config = new BackupConfig
            {
                Name = existing.Name,
                DestinationPath = existing.DestinationPath,
                Kind = new ConfigKindReference
                {
                    OwnerId = existing.Kind.OwnerId,
                    KindId = existing.Kind.KindId
                },
                RequiredPluginId = existing.RequiredPluginId,
                IsEncrypted = existing.IsEncrypted,
                ProviderStates = CloneProviderStates(existing.ProviderStates),
                HostOrigin = new HostConfigOrigin
                {
                    TemplateId = existing.HostOrigin.TemplateId,
                    TemplateName = existing.HostOrigin.TemplateName,
                    DiscoveryProviderId = existing.HostOrigin.DiscoveryProviderId,
                    DiscoveryCandidateId = existing.HostOrigin.DiscoveryCandidateId
                }
            };
        }
        else
        {
            var presetResult = BackupPresetService.CreateConfigFromTemplate(
                preset,
                backupSet.DisplayName);
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
        }

        ApplyPluginDraftContext(config, existing, backupSet, issues);

        var plans = DiscoveryResourcePlanner.CreatePlans(backupSet.Resources, selectedResourceIds);
        var selectedResources = backupSet.Resources
            .Where(resource => plans.Any(plan => plan.ResourceIds.Contains(resource.ResourceId, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        if (existing == null && plans.Count == 0)
        {
            issues.Add(new BackupConfigDraftIssue
            {
                Code = "no-supported-resources",
                Message = "No supported filesystem resources are selected.",
                IsBlocking = true
            });
        }

        foreach (var folder in CreateManagedFolders(plans, backupSet.PluginDraftContext))
        {
            config.SourceFolders.Add(folder);
        }
        var discoveredBaseline = BuildReviewedBaseline(backupSet.Resources);
        config.DiscoveryOrigin = new DiscoveryOrigin
        {
            Identity = CloneIdentity(backupSet.Identity),
            ReviewedBaseline = discoveredBaseline,
            PresetShareId = preset.ShareId,
            PresetVersion = preset.Version,
            ManifestRevision = string.IsNullOrWhiteSpace(backupSet.DiscoveryRevision)
                ? manifestRevision ?? string.Empty
                : backupSet.DiscoveryRevision
        };

        if (existing == null && config.IsEncrypted && !EncryptionService.HasStoredPassword(config.Id))
        {
            issues.Add(new BackupConfigDraftIssue
            {
                Code = "encryption-password-required",
                Message = "An encryption password must be provided before this draft can be committed.",
                IsBlocking = true
            });
        }

        IReadOnlyList<DiscoverySourceChange> discoveryChanges = Array.Empty<DiscoverySourceChange>();
        var reconciliation = BackupConfigDraftReconciliation.NewConfiguration;
        if (existing != null)
        {
            var defaultSelectedRoots = plans
                .Select(plan => DiscoveryResourcePlanner.NormalizePath(plan.FixedRoot))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            discoveryChanges = DiscoveryThreeWayReviewService.Review(
                existing.DiscoveryOrigin?.ReviewedBaseline,
                BuildCurrentSources(existing),
                discoveredBaseline,
                defaultSelectedRoots);
            reconciliation = discoveryChanges.Count == 0
                ? BackupConfigDraftReconciliation.UpToDate
                : BackupConfigDraftReconciliation.NewResources;
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
            DiscoveryChanges = discoveryChanges,
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
        if (definitions.Count == 0)
        {
            return TargetedFailure(
                "invalid-provider-preset",
                "The preset does not contain a valid provider definition reference.",
                string.Empty);
        }
        var providerId = definitions.FirstOrDefault()?.ProviderId ?? string.Empty;
        var missingPlugins = BackupPresetService.GetMissingRequiredPluginIds(preset);
        if (missingPlugins.Count > 0)
        {
            return TargetedFailure(
                "provider-unavailable",
                $"Required plugin(s) are not installed: {string.Join(", ", missingPlugins)}.",
                providerId);
        }
        var installedV3 = GetInstalledV3PluginIds();
        var inactivePlugins = (preset.RequiredPluginIds ?? new ObservableCollection<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id)
                && (!PluginService.GetPluginEnabled(id)
                    || (installedV3.Contains(id) && !IsV3PluginActive(id))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (inactivePlugins.Count > 0)
        {
            return TargetedFailure(
                "provider-inactive",
                $"Required plugin(s) are disabled or inactive: {string.Join(", ", inactivePlugins)}.",
                providerId);
        }
        var result = await discoveryService.DiscoverAsync(
            new DiscoveryRequest
            {
                Mode = DiscoveryRequestMode.PresetTargeted,
                Definitions = definitions
            },
            progress,
            cancellationToken).ConfigureAwait(false);
        if (result.Candidates.Count != 0
            || result.Diagnostics.Any(value => value.Severity == DiscoveryDiagnosticSeverity.Error))
        {
            return result;
        }
        return new GameDiscoveryResult
        {
            Candidates = result.Candidates,
            Diagnostics = result.Diagnostics.Concat(
            [
                new DiscoveryDiagnostic
                {
                    Severity = DiscoveryDiagnosticSeverity.Error,
                    Code = "no-candidates",
                    Message = "The selected provider found no matching backup targets.",
                    ProviderId = providerId,
                    Category = "preset-targeted"
                }
            ]).ToList()
        };
    }

    private static GameDiscoveryResult TargetedFailure(string code, string message, string providerId) => new()
    {
        Diagnostics =
        [
            new DiscoveryDiagnostic
            {
                Severity = DiscoveryDiagnosticSeverity.Error,
                Code = code,
                Message = message,
                ProviderId = providerId,
                Category = "preset-targeted"
            }
        ]
    };

    private static HashSet<string> GetInstalledV3PluginIds()
    {
        try
        {
            return PluginV3PackageService.GetInstalledPluginInfos()
                .Select(plugin => plugin.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LogService.LogWarning($"Installed Plugin V3 inventory could not be read for targeted discovery: {ex.Message}", "GameDiscovery");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool IsV3PluginActive(string pluginId)
    {
        try { return PluginV3RuntimeService.IsActive(new FolderRewind.Plugin.Abstractions.PluginId(pluginId)); }
        catch { return false; }
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
        var validationError = ValidateCommit(selected, newConfigs);
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            return new BackupConfigDraftCommitResult { ErrorMessage = validationError };
        }

        var addedConfigs = new List<BackupConfig>();
        var addedFolders = new List<(BackupConfig Config, ManagedFolder Folder)>();
        var removedFolders = new List<(BackupConfig Config, ManagedFolder Folder, int Index)>();
        var updateSnapshots = new List<(ManagedFolder Folder, BackupSourceScope SourceScope)>();
        var originSnapshots = new List<(BackupConfig Config, DiscoveryOrigin? Origin)>();
        var transaction = DiscoveryDraftTransaction.Execute(
            apply: () =>
            {
                foreach (var draft in selected.Where(draft => draft.IsCommittable))
                {
                    if (draft.Reconciliation == BackupConfigDraftReconciliation.NewConfiguration)
                    {
                        CompleteProposedOriginReview(draft.ProposedConfig, draft.ProposedConfig.DiscoveryOrigin, Array.Empty<string>());
                        ConfigService.CurrentConfig.BackupConfigs.Add(draft.ProposedConfig);
                        addedConfigs.Add(draft.ProposedConfig);
                        continue;
                    }
                    if (draft.ExistingConfig == null)
                    {
                        continue;
                    }

                    originSnapshots.Add((draft.ExistingConfig, CloneOrigin(draft.ExistingConfig.DiscoveryOrigin)));
                    ApplyDiscoveryChanges(draft, addedFolders, removedFolders, updateSnapshots);
                    var trackedRoots = draft.ExistingConfig.DiscoveryOrigin?.ReviewedBaseline.Sources
                        .Select(source => source.NormalizedRootPath)
                        .Concat(draft.ExistingConfig.DiscoveryOrigin.ReviewedBaseline.UserOverrides
                            .Select(item => item.NormalizedRootPath))
                        .ToList() ?? new List<string>();
                    CompleteProposedOriginReview(
                        draft.ExistingConfig,
                        draft.ProposedConfig.DiscoveryOrigin,
                        trackedRoots);
                }
            },
            rollback: () => Rollback(addedConfigs, addedFolders, removedFolders, updateSnapshots, originSnapshots),
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
            AddedSourceCount = addedFolders.Count + addedConfigs.Sum(config => config.SourceFolders.Count),
            AddedConfigurationIds = addedConfigs.Select(config => config.Id).ToArray()
        };
    }

    private static IReadOnlyList<ManagedFolder> CreateManagedFolders(
        IReadOnlyList<DiscoveredSourcePlan> plans,
        PluginDiscoveryDraftContext? pluginContext)
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
            var folder = new ManagedFolder
            {
                Path = plan.FixedRoot,
                DisplayName = name,
                CoverImagePath = ResolveDefaultCoverImagePath(plan.FixedRoot),
                SourceScope = new BackupSourceScope
                {
                    Mode = plan.ScopeMode,
                    IncludePatterns = new ObservableCollection<string>(plan.IncludePatterns)
                }
            };
            folder.ProviderStates = MergeFolderProviderStates(pluginContext, plan.ResourceIds);
            result.Add(folder);
        }
        return result;
    }

    private static string ResolveDefaultCoverImagePath(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return string.Empty;
        }
        try
        {
            var iconPath = System.IO.Path.Combine(folderPath, "icon.png");
            return System.IO.File.Exists(iconPath) ? iconPath : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static BackupConfig? FindExistingConfiguration(
        BackupSetCandidate backupSet,
        IEnumerable<BackupConfig>? configs)
    {
        return DiscoverySetIdentityMatcher.FindUnique(
            backupSet.Identity,
            configs,
            config => config.DiscoveryOrigin);
    }

    private static string ValidateCommit(
        IReadOnlyList<BackupConfigDraft> selectedDrafts,
        IReadOnlyList<BackupConfig> newConfigs)
    {
        if (newConfigs.Any(config => string.IsNullOrWhiteSpace(config.Name)))
        {
            return "Every configuration requires a name.";
        }
        if (newConfigs.Any(config => string.IsNullOrWhiteSpace(config.DestinationPath)))
        {
            return "Every configuration requires a destination directory.";
        }
        if (newConfigs.Any(config => config.SourceFolders.Count == 0))
        {
            return "A newly discovered configuration must contain at least one source folder.";
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

        foreach (var draft in selectedDrafts.Where(draft => draft.IsCommittable))
        {
            var config = draft.Reconciliation == BackupConfigDraftReconciliation.NewConfiguration
                ? draft.ProposedConfig
                : draft.ExistingConfig;
            if (config == null)
            {
                continue;
            }

            var folders = draft.Reconciliation == BackupConfigDraftReconciliation.NewConfiguration
                ? draft.ProposedConfig.SourceFolders.AsEnumerable()
                : draft.DiscoveryChanges
                    .Where(change => change.IsSelected && change.IsActionable && change.Discovered != null)
                    .Select(change => CreateManagedFolder(
                        change.Discovered!,
                        draft.ProposedConfig.SourceFolders.FirstOrDefault(folder => PathsEqual(folder.Path, change.NormalizedRootPath))?.DisplayName))
                    .ToList();
            foreach (var folder in folders)
            {
                if (!BackupStoragePathService.TryResolveBackupStoragePaths(
                        config.DestinationPath,
                        folder.DisplayName,
                        folder.Path,
                        out _,
                        out var backupSubDir,
                        out var metadataDir))
                {
                    return $"Cannot resolve a safe backup storage path for source '{folder.DisplayName}'.";
                }

                var overlap = BackupPathOverlapPolicy.Validate(folder.Path, backupSubDir, metadataDir);
                if (!overlap.IsSafe)
                {
                    return $"Source '{folder.DisplayName}' overlaps its backup storage path: "
                           + $"{overlap.SourcePath} ↔ {overlap.TargetPath}";
                }
            }
        }
        return string.Empty;
    }

    private static void ApplyDiscoveryChanges(
        BackupConfigDraft draft,
        ICollection<(BackupConfig Config, ManagedFolder Folder)> addedFolders,
        ICollection<(BackupConfig Config, ManagedFolder Folder, int Index)> removedFolders,
        ICollection<(ManagedFolder Folder, BackupSourceScope SourceScope)> updateSnapshots)
    {
        var config = draft.ExistingConfig!;
        foreach (var change in draft.DiscoveryChanges.Where(change => change.IsActionable && change.IsSelected))
        {
            var existingFolder = config.SourceFolders.FirstOrDefault(folder => PathsEqual(folder.Path, change.NormalizedRootPath));
            if (change.Discovered == null)
            {
                if (existingFolder != null)
                {
                    var index = config.SourceFolders.IndexOf(existingFolder);
                    config.SourceFolders.Remove(existingFolder);
                    removedFolders.Add((config, existingFolder, index));
                }
                continue;
            }

            if (existingFolder == null)
            {
                var proposed = draft.ProposedConfig.SourceFolders
                    .FirstOrDefault(folder => PathsEqual(folder.Path, change.NormalizedRootPath));
                var proposedName = proposed?.DisplayName;
                var folder = CreateManagedFolder(change.Discovered, proposedName);
                if (proposed != null)
                {
                    folder.ProviderStates = CloneProviderStates(proposed.ProviderStates);
                }
                folder.DisplayName = UniqueFolderName(config, folder.DisplayName);
                config.SourceFolders.Add(folder);
                addedFolders.Add((config, folder));
                continue;
            }

            updateSnapshots.Add((existingFolder, CloneSourceScope(existingFolder.SourceScope)));
            existingFolder.SourceScope = ScopeFrom(change.Discovered);
        }
    }

    private static void CompleteProposedOriginReview(
        BackupConfig config,
        DiscoveryOrigin? proposedOrigin,
        IEnumerable<string> previouslyTrackedRoots)
    {
        if (proposedOrigin == null)
        {
            return;
        }
        var completed = CloneOrigin(proposedOrigin)!;
        completed.ReviewedBaseline = DiscoveryThreeWayReviewService.CompleteReview(
            completed.ReviewedBaseline,
            BuildCurrentSources(config),
            previouslyTrackedRoots);
        config.DiscoveryOrigin = completed;
    }

    private static IReadOnlyList<ReviewedDiscoverySource> BuildCurrentSources(BackupConfig config) =>
        config.SourceFolders.Select(folder => new ReviewedDiscoverySource
        {
            NormalizedRootPath = DiscoveryResourcePlanner.NormalizePath(folder.Path),
            Mode = folder.SourceScope?.Mode ?? BackupSourceScopeMode.All,
            IncludePatterns = new ObservableCollection<string>(folder.SourceScope?.IncludePatterns
                ?? new ObservableCollection<string>())
        }).ToList();

    private static ManagedFolder CreateManagedFolder(ReviewedDiscoverySource source, string? displayName = null) => new()
    {
        Path = source.NormalizedRootPath,
        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? FolderNameConflictService.ResolveDisplayName(string.Empty, source.NormalizedRootPath)
            : displayName,
        CoverImagePath = ResolveDefaultCoverImagePath(source.NormalizedRootPath),
        SourceScope = ScopeFrom(source)
    };

    private static BackupSourceScope ScopeFrom(ReviewedDiscoverySource source) => new()
    {
        Mode = source.Mode,
        IncludePatterns = new ObservableCollection<string>(source.IncludePatterns)
    };

    private static string UniqueFolderName(BackupConfig config, string requested)
    {
        var baseName = string.IsNullOrWhiteSpace(requested) ? "Game data" : requested;
        var candidate = baseName;
        var suffix = 2;
        while (config.SourceFolders.Any(folder =>
                   string.Equals(folder.DisplayName, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName} ({suffix++})";
        }
        return candidate;
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        DiscoveryResourcePlanner.NormalizePath(left),
        DiscoveryResourcePlanner.NormalizePath(right),
        StringComparison.OrdinalIgnoreCase);

    private static BackupSourceScope CloneSourceScope(BackupSourceScope source) => new()
    {
        Mode = source?.Mode ?? BackupSourceScopeMode.All,
        IncludePatterns = new ObservableCollection<string>(source?.IncludePatterns ?? new ObservableCollection<string>())
    };

    private static void ApplyPluginDraftContext(
        BackupConfig config,
        BackupConfig? existing,
        BackupSetCandidate backupSet,
        ICollection<BackupConfigDraftIssue> issues)
    {
        var context = backupSet.PluginDraftContext;
        if (context == null)
        {
            return;
        }
        var targetKind = existing?.Kind ?? config.Kind;
        if (!string.Equals(targetKind.OwnerId, context.Kind.OwnerId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(targetKind.KindId, context.Kind.KindId, StringComparison.Ordinal))
        {
            issues.Add(new BackupConfigDraftIssue
            {
                Code = "plugin-config-kind-mismatch",
                Message = $"The selected preset kind '{targetKind.OwnerId}/{targetKind.KindId}' does not match the plugin draft kind '{context.Kind.OwnerId}/{context.Kind.KindId}'.",
                IsBlocking = true
            });
            return;
        }

        foreach (var pair in context.ConfigProviderStates)
        {
            config.ProviderStates[pair.Key] = CloneProviderState(pair.Value);
        }
        config.HostOrigin.DiscoveryProviderId = context.ProviderId;
        config.HostOrigin.DiscoveryCandidateId = context.CandidateId;
        if (existing == null)
        {
            config.RequiredPluginId = context.PluginId;
        }
    }

    private static Dictionary<string, ProviderStatePayload> MergeFolderProviderStates(
        PluginDiscoveryDraftContext? context,
        IEnumerable<string> resourceIds)
    {
        var states = new Dictionary<string, ProviderStatePayload>(StringComparer.OrdinalIgnoreCase);
        if (context == null)
        {
            return states;
        }
        foreach (var resourceId in resourceIds)
        {
            if (!context.FolderProviderStatesByResourceId.TryGetValue(resourceId, out var resourceStates))
            {
                continue;
            }
            foreach (var pair in resourceStates)
            {
                states.TryAdd(pair.Key, CloneProviderState(pair.Value));
            }
        }
        return states;
    }

    private static Dictionary<string, ProviderStatePayload> CloneProviderStates(
        IReadOnlyDictionary<string, ProviderStatePayload>? states)
    {
        return (states ?? new Dictionary<string, ProviderStatePayload>()).ToDictionary(
            pair => pair.Key,
            pair => CloneProviderState(pair.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    private static ProviderStatePayload CloneProviderState(ProviderStatePayload state) => new()
    {
        SchemaVersion = state.SchemaVersion,
        Data = state.Data.Clone()
    };

    private static DiscoverySetIdentity CloneIdentity(DiscoverySetIdentity source) => new()
    {
        ProviderId = source?.ProviderId ?? string.Empty,
        DefinitionId = source?.DefinitionId ?? string.Empty,
        SetId = source?.SetId ?? string.Empty,
        ExternalIds = new Dictionary<string, string>(
            source?.ExternalIds ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase)
    };

    private static ReviewedDiscoveryBaseline BuildReviewedBaseline(
        IEnumerable<BackupResourceCandidate> resources)
    {
        var materialized = (resources ?? Array.Empty<BackupResourceCandidate>()).ToList();
        var allResourceIds = materialized
            .Where(DiscoveryPresentationService.CanSelect)
            .Select(resource => resource.ResourceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ReviewedDiscoveryBaseline
        {
            Sources = new ObservableCollection<ReviewedDiscoverySource>(
                DiscoveryResourcePlanner.CreatePlans(materialized, allResourceIds).Select(plan =>
                    new ReviewedDiscoverySource
                    {
                        NormalizedRootPath = plan.FixedRoot,
                        Mode = plan.ScopeMode,
                        IncludePatterns = new ObservableCollection<string>(plan.IncludePatterns),
                        ResourceIds = new ObservableCollection<string>(plan.ResourceIds)
                    }))
        };
    }

    private static ReviewedDiscoveryBaseline CloneBaseline(ReviewedDiscoveryBaseline source) =>
        DiscoveryThreeWayReviewService.Clone(source);

    private static DiscoveryOrigin? CloneOrigin(DiscoveryOrigin? source)
    {
        if (source == null)
        {
            return null;
        }
        return new DiscoveryOrigin
        {
            Identity = CloneIdentity(source.Identity),
            ReviewedBaseline = CloneBaseline(source.ReviewedBaseline),
            PresetShareId = source.PresetShareId,
            PresetVersion = source.PresetVersion,
            ManifestRevision = source.ManifestRevision
        };
    }

    private static void Rollback(
        IEnumerable<BackupConfig> addedConfigs,
        IEnumerable<(BackupConfig Config, ManagedFolder Folder)> addedFolders,
        IEnumerable<(BackupConfig Config, ManagedFolder Folder, int Index)> removedFolders,
        IEnumerable<(ManagedFolder Folder, BackupSourceScope SourceScope)> updateSnapshots,
        IEnumerable<(BackupConfig Config, DiscoveryOrigin? Origin)> originSnapshots)
    {
        foreach (var snapshot in originSnapshots.Reverse())
        {
            snapshot.Config.DiscoveryOrigin = snapshot.Origin;
        }
        foreach (var snapshot in updateSnapshots.Reverse())
        {
            snapshot.Folder.SourceScope = snapshot.SourceScope;
        }
        foreach (var removed in removedFolders.Reverse())
        {
            removed.Config.SourceFolders.Insert(removed.Index, removed.Folder);
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

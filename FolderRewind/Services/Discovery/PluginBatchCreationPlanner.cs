using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services.Discovery;

public sealed class PluginBatchCreationSummaryItem
{
    public string ConfigName { get; init; } = string.Empty;
    public string DestinationPath { get; init; } = string.Empty;
    public string SourceSummary { get; init; } = string.Empty;
}

public sealed class PluginBatchCreationPlan
{
    public IReadOnlyList<BackupConfigDraft> Drafts { get; init; }
        = Array.Empty<BackupConfigDraft>();
    public IReadOnlyList<PluginBatchCreationSummaryItem> Items { get; init; }
        = Array.Empty<PluginBatchCreationSummaryItem>();
    public IReadOnlyList<BackupResourceCandidate> BroadRootResources { get; init; }
        = Array.Empty<BackupResourceCandidate>();
    public IReadOnlyList<string> SkippedMessages { get; init; }
        = Array.Empty<string>();
    public int ExistingCount { get; init; }
    public int UnavailableCount { get; init; }
    public string FatalMessage { get; init; } = string.Empty;
}

public static class PluginBatchCreationPlanner
{
    public static PluginBatchCreationPlan Build(
        GameDiscoveryResult result,
        string pluginId,
        ConfigKindReference configKind,
        IEnumerable<BackupPreset>? presets,
        IEnumerable<BackupConfig>? existingConfigs)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(configKind);

        var existing = (existingConfigs ?? Array.Empty<BackupConfig>()).ToList();
        var skippedMessages = new List<string>();
        var drafts = new List<BackupConfigDraft>();
        var existingCount = 0;
        var unavailableCount = 0;
        var matchingSetCount = 0;

        var sets = result.Candidates
            .OrderBy(game => game.Definition.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(game => game.Definition.DefinitionId, StringComparer.Ordinal)
            .SelectMany(game => game.BackupSets
                .OrderBy(set => set.Identity.SetId, StringComparer.Ordinal)
                .Select(set => (Game: game, Set: set)));

        foreach (var pair in sets)
        {
            var context = pair.Set.PluginDraftContext;
            if (context == null
                || !string.Equals(context.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)
                || !KindsEqual(context.Kind, configKind))
            {
                continue;
            }

            matchingSetCount++;
            if (existing.Any(config =>
                    config.DiscoveryOrigin?.Identity?.HasSameStableIdentity(pair.Set.Identity) == true))
            {
                existingCount++;
                continue;
            }

            var selectedResourceIds = pair.Set.Resources
                .Where(DiscoveryPresentationService.IsSelectedByDefault)
                .Select(resource => resource.ResourceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selectedResourceIds.Count == 0)
            {
                unavailableCount++;
                skippedMessages.Add(I18n.Format(
                    "GameDiscovery_PluginBatch_SkippedNoResources",
                    pair.Set.DisplayName));
                continue;
            }

            var draft = DiscoveryDraftService.CreateDraft(
                pair.Game,
                pair.Set,
                presets,
                existing,
                string.Empty,
                selectedResourceIds);
            if (draft.ExistingConfig != null)
            {
                existingCount++;
                continue;
            }

            var blocking = draft.Issues.FirstOrDefault(issue => issue.IsBlocking);
            if (blocking != null || draft.SelectedResources.Count == 0)
            {
                unavailableCount++;
                skippedMessages.Add(I18n.Format(
                    "GameDiscovery_PluginBatch_SkippedBlocking",
                    pair.Set.DisplayName,
                    blocking?.Message ?? I18n.GetString("GameDiscovery_PluginBatch_NoSupportedResources")));
                continue;
            }

            draft.IsSelected = true;
            drafts.Add(draft);
        }

        if (matchingSetCount == 0
            && !result.Diagnostics.Any(diagnostic => diagnostic.Severity == DiscoveryDiagnosticSeverity.Error))
        {
            skippedMessages.Add(I18n.GetString("GameDiscovery_PluginBatch_NoMatchingKind"));
        }

        AllocateUniqueNamesAndDestinations(drafts, existing);
        var broadRoots = drafts
            .SelectMany(draft => draft.SelectedResources)
            .Where(resource => resource.RequiresExplicitConfirmation)
            .GroupBy(resource => resource.FixedRoot, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var fatalMessage = string.Join(
            Environment.NewLine,
            result.Diagnostics
                .Where(diagnostic => diagnostic.Severity == DiscoveryDiagnosticSeverity.Error)
                .Select(diagnostic => diagnostic.Message)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.CurrentCulture));

        return new PluginBatchCreationPlan
        {
            Drafts = drafts,
            Items = drafts.Select(draft => new PluginBatchCreationSummaryItem
            {
                ConfigName = draft.ProposedConfig.Name,
                DestinationPath = draft.ProposedConfig.DestinationPath,
                SourceSummary = string.Join(
                    Environment.NewLine,
                    draft.SelectedResources
                        .Select(resource => resource.FixedRoot)
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Distinct(StringComparer.OrdinalIgnoreCase))
            }).ToList(),
            BroadRootResources = broadRoots,
            SkippedMessages = skippedMessages,
            ExistingCount = existingCount,
            UnavailableCount = unavailableCount,
            FatalMessage = fatalMessage
        };
    }

    private static void AllocateUniqueNamesAndDestinations(
        IEnumerable<BackupConfigDraft> drafts,
        IReadOnlyList<BackupConfig> existingConfigs)
    {
        var usedNames = existingConfigs
            .Select(config => config.Name?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedDestinations = existingConfigs
            .Select(config => FolderNameConflictService.NormalizeDestinationPath(config.DestinationPath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var draft in drafts)
        {
            var baseName = draft.ProposedConfig.Name?.Trim();
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = draft.BackupSet.DisplayName?.Trim();
            }
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = I18n.GetString("Config_DefaultBackupName");
            }

            var suffix = 1;
            while (true)
            {
                var candidateName = suffix == 1 ? baseName : $"{baseName} ({suffix})";
                var candidateDestination = ConfigService.BuildDefaultDestinationPath(candidateName);
                var normalizedDestination = FolderNameConflictService.NormalizeDestinationPath(candidateDestination);
                if (!usedNames.Contains(candidateName)
                    && !usedDestinations.Contains(normalizedDestination))
                {
                    draft.ProposedConfig.Name = candidateName;
                    draft.ProposedConfig.DestinationPath = candidateDestination;
                    usedNames.Add(candidateName);
                    usedDestinations.Add(normalizedDestination);
                    break;
                }

                suffix++;
            }
        }
    }

    private static bool KindsEqual(ConfigKindReference left, ConfigKindReference right) =>
        string.Equals(left.OwnerId, right.OwnerId, StringComparison.Ordinal)
        && string.Equals(left.KindId, right.KindId, StringComparison.Ordinal);
}

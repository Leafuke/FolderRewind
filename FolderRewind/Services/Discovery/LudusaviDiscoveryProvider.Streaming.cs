using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Discovery;

public sealed partial class LudusaviDiscoveryProvider
{
    private async Task<DiscoveryProviderResult> DiscoverStreamingAsync(DiscoveryRequest request,
        IProgress<DiscoveryProgress>? progress, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var ownedGeneration = _generation is null
            ? await _cacheService!.PrepareStoredGenerationAsync(progress, token).ConfigureAwait(false) : null;
        var generation = _generation ?? ownedGeneration;
        if (generation is null)
            return new DiscoveryProviderResult { ProviderId = ProviderId, Diagnostics = [Diagnostic(
                DiscoveryDiagnosticSeverity.Information, "manifest-unavailable", "No valid Ludusavi manifest cache is available.")] };

        progress?.Report(new DiscoveryProgress { ProviderId = ProviderId, Phase = "installations",
            Message = "Scanning Steam, GOG, and Epic installations" });
        var installationScan = _installationDiscovery.Scan(request.StoreRoots, request.DisabledAutoRoots, token);
        var installations = installationScan.Installations;
        var requested = request.Mode == DiscoveryRequestMode.PresetTargeted
            ? request.Definitions.Where(value => string.Equals(value.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase))
                .Select(value => value.DefinitionId).ToHashSet(StringComparer.Ordinal) : null;
        var matched = new Dictionary<LudusaviCompiledGame, List<InstallationMatch>>();
        var strongInstallations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var installationNames = installations.GroupBy(InstallationKey, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => InstallationNames(group.First()), StringComparer.OrdinalIgnoreCase);
        var namesPerInstallation = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        await foreach (var game in generation.ReadGamesAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            if (requested is not null && !requested.Contains(game.DefinitionId)) continue;
            var names = new[] { game.DisplayName }.Concat(game.Aliases).Concat(game.InstallDirectoryHints)
                .Select(NormalizeName).Where(value => value.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var installation in installations)
            {
                var key = InstallationKey(installation);
                var storeKey = StoreKey(installation.Store);
                var strong = installation.Store is GameStore.Steam or GameStore.Gog
                    && !string.IsNullOrWhiteSpace(installation.StoreGameId)
                    && new[] { storeKey, storeKey + "Extra" }.Any(idKey => game.ExternalIds.TryGetValue(idKey, out var ids)
                        && SplitIds(ids).Contains(installation.StoreGameId, StringComparer.OrdinalIgnoreCase));
                if (strong)
                {
                    strongInstallations.Add(key);
                    AddMatch(matched, game, new InstallationMatch(installation, DiscoveryConfidence.High,
                        "store-id", $"Matched {installation.Store} ID {installation.StoreGameId}."));
                }
                else if (installationNames[key].Any(names.Contains))
                {
                    namesPerInstallation[key] = namesPerInstallation.GetValueOrDefault(key) + 1;
                    AddMatch(matched, game, new InstallationMatch(installation, DiscoveryConfidence.Medium,
                        "name-and-install-dir", "Matched the normalized display name, alias, or install directory hint."));
                }
            }
        }

        var diagnostics = generation.Diagnostics.Select(item => Diagnostic(DiscoveryDiagnosticSeverity.Warning,
            item.Code, $"{item.EntryName}: {item.Message}")).ToList();
        diagnostics.AddRange(installationScan.Diagnostics);
        var candidates = new List<DiscoveredGameCandidate>();
        foreach (var pair in matched)
        {
            token.ThrowIfCancellationRequested();
            var matches = new List<InstallationMatch>();
            foreach (var match in pair.Value)
            {
                var key = InstallationKey(match.Installation);
                if (match.Confidence != DiscoveryConfidence.High && strongInstallations.Contains(key)) continue;
                var ambiguous = match.Confidence != DiscoveryConfidence.High && (namesPerInstallation.GetValueOrDefault(key) > 1
                    || installations.Count(other => other.Store == match.Installation.Store
                        && string.Equals(NormalizeName(other.DisplayName), NormalizeName(match.Installation.DisplayName),
                            StringComparison.OrdinalIgnoreCase)) > 1);
                matches.Add(ambiguous ? new InstallationMatch(match.Installation, DiscoveryConfidence.Low,
                    "ambiguous-name", "Only an ambiguous normalized name matched; resources are not selected by default.") : match);
            }
            if (matches.Count == 0) continue;
            candidates.Add(CreateCandidate(pair.Key, matches.GroupBy(match => InstallationKey(match.Installation), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(match => match.Confidence).First()).ToList(), diagnostics,
                generation.Metadata.SourceSha256, token));
        }
        return new DiscoveryProviderResult
        {
            ProviderId = ProviderId, Candidates = candidates, Diagnostics = diagnostics,
            Statistics = new DiscoveryScanStatistics { DefinitionsConsidered = candidates.Count,
                InstallationsFound = candidates.Sum(value => value.Installations.Count),
                ResourcesFound = candidates.Sum(value => value.BackupSets.Sum(set => set.Resources.Count)), Duration = stopwatch.Elapsed }
        };
    }
}

using FolderRewind.Models;
using FolderRewind.Services.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Discovery;

public sealed class LudusaviDiscoveryProvider : IFolderRewindDiscoveryProvider
{
    public const string ProviderId = "ludusavi";

    private readonly Func<CancellationToken, Task<(
        LudusaviManifestCacheMetadata Metadata,
        LudusaviCompiledIndex Index)?>> _indexLoader;
    private readonly ILauncherInstallationDiscoveryService _installationDiscovery;
    private readonly LudusaviPathExpressionResolver _resolver;

    public LudusaviDiscoveryProvider(
        LudusaviManifestCacheService cacheService,
        ILauncherInstallationDiscoveryService? installationDiscovery = null,
        LudusaviPathExpressionResolver? resolver = null)
        : this(
            cacheService.LoadCurrentAsync,
            installationDiscovery,
            resolver)
    {
    }

    public LudusaviDiscoveryProvider(
        Func<CancellationToken, Task<(
            LudusaviManifestCacheMetadata Metadata,
            LudusaviCompiledIndex Index)?>> indexLoader,
        ILauncherInstallationDiscoveryService? installationDiscovery = null,
        LudusaviPathExpressionResolver? resolver = null)
    {
        _indexLoader = indexLoader ?? throw new ArgumentNullException(nameof(indexLoader));
        _installationDiscovery = installationDiscovery ?? new LauncherInstallationDiscoveryService();
        _resolver = resolver ?? new LudusaviPathExpressionResolver();
    }

    public DiscoveryProviderDescriptor Descriptor { get; } = new()
    {
        Id = ProviderId,
        DisplayName = "Ludusavi",
        Priority = 10,
        IsSpecialized = false
    };

    public async Task<DiscoveryProviderResult> DiscoverAsync(
        DiscoveryRequest request,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        var current = await _indexLoader(cancellationToken).ConfigureAwait(false);
        if (current == null)
        {
            return new DiscoveryProviderResult
            {
                ProviderId = ProviderId,
                Diagnostics = new[]
                {
                    Diagnostic(
                        DiscoveryDiagnosticSeverity.Information,
                        "manifest-unavailable",
                        "No valid Ludusavi manifest cache is available. Download or import a manifest first.")
                },
                Statistics = new DiscoveryScanStatistics { Duration = stopwatch.Elapsed }
            };
        }

        progress?.Report(new DiscoveryProgress
        {
            ProviderId = ProviderId,
            Phase = "installations",
            Message = "Scanning Steam, GOG, and Epic installations"
        });
        var installationScan = _installationDiscovery.Scan(
            request.StoreRoots,
            request.DisabledAutoRoots,
            cancellationToken);

        var definitions = SelectDefinitions(current.Value.Index.Games, request);
        var matchedDefinitions = MatchInstallationsToDefinitions(
            definitions,
            installationScan.Installations,
            cancellationToken);
        var candidates = new List<DiscoveredGameCandidate>();
        var diagnostics = current.Value.Index.Diagnostics
            .Select(item => Diagnostic(
                DiscoveryDiagnosticSeverity.Warning,
                item.Code,
                $"{item.EntryName}: {item.Message}"))
            .ToList();
        diagnostics.AddRange(installationScan.Diagnostics);
        for (var definitionIndex = 0; definitionIndex < matchedDefinitions.Count; definitionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matchedDefinition = matchedDefinitions[definitionIndex];
            progress?.Report(new DiscoveryProgress
            {
                ProviderId = ProviderId,
                Phase = "resources",
                Message = matchedDefinition.Definition.DisplayName,
                Completed = definitionIndex,
                Total = matchedDefinitions.Count
            });
            candidates.Add(CreateCandidate(
                matchedDefinition.Definition,
                matchedDefinition.Matches,
                diagnostics,
                cancellationToken));
        }

        stopwatch.Stop();
        return new DiscoveryProviderResult
        {
            ProviderId = ProviderId,
            Candidates = candidates,
            Diagnostics = diagnostics,
            Statistics = new DiscoveryScanStatistics
            {
                DefinitionsConsidered = matchedDefinitions.Count,
                InstallationsFound = candidates.Sum(candidate => candidate.Installations.Count),
                ResourcesFound = candidates.Sum(candidate => candidate.BackupSets.Sum(set => set.Resources.Count)),
                Duration = stopwatch.Elapsed
            }
        };
    }

    private DiscoveredGameCandidate CreateCandidate(
        LudusaviCompiledGame definition,
        IReadOnlyList<InstallationMatch> matches,
        ICollection<DiscoveryDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var resources = new List<BackupResourceCandidate>();
        foreach (var match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var resource in definition.Files.Where(resource => !resource.IsDisabled))
            {
                if (!MatchesConstraint(resource, match.Installation.Store, diagnostics, definition.DefinitionId))
                {
                    continue;
                }

                if (!UsesStoreUserId(resource.Expression))
                {
                    AddResolvedResource(
                        resources,
                        diagnostics,
                        definition,
                        resource,
                        match,
                        string.Empty,
                        allowDefaultSelection: true,
                        accountWarning: null);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(match.Installation.ActiveStoreUserId))
                {
                    AddResolvedResource(
                        resources,
                        diagnostics,
                        definition,
                        resource,
                        match,
                        string.Empty,
                        allowDefaultSelection: false,
                        accountWarning: "No active store account could be identified. This wildcard may include multiple local accounts and requires manual selection.");
                    continue;
                }

                var activeUserId = match.Installation.ActiveStoreUserId;
                foreach (var userId in match.Installation.StoreUserIds
                             .Append(activeUserId)
                             .Where(value => !string.IsNullOrWhiteSpace(value))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var isActive = string.Equals(userId, activeUserId, StringComparison.OrdinalIgnoreCase);
                    AddResolvedResource(
                        resources,
                        diagnostics,
                        definition,
                        resource,
                        match,
                        userId,
                        allowDefaultSelection: isActive,
                        accountWarning: isActive
                            ? null
                            : $"Store account {userId} is not the active account and is not selected by default.");
                }
            }

            foreach (var resource in definition.Registry.Where(resource => !resource.IsDisabled))
            {
                if (!MatchesConstraint(resource, match.Installation.Store, diagnostics, definition.DefinitionId))
                {
                    continue;
                }
                resources.Add(CreateRegistryCandidate(definition, resource, match.Confidence));
            }
        }

        return new DiscoveredGameCandidate
        {
            StableKey = $"{ProviderId}:{definition.DefinitionId}",
            Definition = CreateDefinition(definition),
            Installations = matches
                .GroupBy(match => InstallationKey(match.Installation), StringComparer.OrdinalIgnoreCase)
                .Select(group => CreateInstallation(group.First()))
                .ToList(),
            BackupSets = new List<BackupSetCandidate>
            {
                new()
                {
                    StableKey = $"{ProviderId}:{definition.DefinitionId}:main",
                    Identity = new DiscoverySetIdentity
                    {
                        ProviderId = ProviderId,
                        DefinitionId = definition.DefinitionId,
                        SetId = "main",
                        ExternalIds = new Dictionary<string, string>(definition.ExternalIds, StringComparer.OrdinalIgnoreCase)
                    },
                    DisplayName = definition.DisplayName,
                    Resources = DeduplicateResources(resources)
                }
            }
        };
    }

    private BackupResourceCandidate CreateResourceCandidate(
        LudusaviCompiledGame definition,
        LudusaviCompiledResource resource,
        ResolvedLudusaviResource resolved,
        DiscoveryConfidence confidence,
        string evidenceKind,
        string evidenceDescription,
        bool allowDefaultSelection,
        string? accountWarning)
    {
        var support = string.IsNullOrWhiteSpace(resolved.FixedRoot)
            ? BackupResourceSupportState.InvalidPath
            : resolved.Safety == LudusaviPathSafety.Blocked
                ? BackupResourceSupportState.UnsafeRoot
                : BackupResourceSupportState.Supported;
        var fixedRootExists = support == BackupResourceSupportState.Supported
                              && Directory.Exists(resolved.FixedRoot);
        return new BackupResourceCandidate
        {
            ResourceId = CreateResolvedResourceId(resource.ResourceId, resolved.FixedRoot, resolved.IncludePatterns),
            ProviderId = ProviderId,
            ProviderPriority = Descriptor.Priority,
            DisplayName = CreateResourceDisplayName(definition.DisplayName, resource.Tags),
            Kind = resolved.Kind,
            SupportState = support,
            FixedRoot = resolved.FixedRoot,
            IncludePatterns = resolved.IncludePatterns,
            OriginalExpression = resource.Expression,
            Tags = resource.Tags,
            Constraints = FlattenConstraints(resource.Constraints),
            FixedRootExists = fixedRootExists,
            RequiresExplicitConfirmation = resolved.Safety == LudusaviPathSafety.RequiresConfirmation,
            SafetyWarning = resolved.SafetyWarning,
            IsSelectedByDefault = support == BackupResourceSupportState.Supported
                                  && fixedRootExists
                                  && resolved.Safety == LudusaviPathSafety.Normal
                                  && allowDefaultSelection
                                  && confidence >= DiscoveryConfidence.Medium,
            Evidence = new[]
            {
                new DiscoveryEvidence
                {
                    Confidence = confidence,
                    Kind = evidenceKind,
                    Description = BuildEvidenceDescription(
                        evidenceDescription,
                        resolved.UsesStoreUserIdWildcard,
                        accountWarning),
                    Source = ProviderId
                }
            }
        };
    }

    private void AddResolvedResource(
        ICollection<BackupResourceCandidate> resources,
        ICollection<DiscoveryDiagnostic> diagnostics,
        LudusaviCompiledGame definition,
        LudusaviCompiledResource resource,
        InstallationMatch match,
        string storeUserId,
        bool allowDefaultSelection,
        string? accountWarning)
    {
        var resolved = _resolver.Resolve(resource, match.Installation, storeUserId);
        if (resolved == null)
        {
            diagnostics.Add(new DiscoveryDiagnostic
            {
                Severity = DiscoveryDiagnosticSeverity.Warning,
                Code = "path-resolution-failed",
                ProviderId = ProviderId,
                DefinitionId = definition.DefinitionId,
                RootPath = match.Installation.BasePath,
                Category = "path",
                Message = $"{definition.DisplayName}: could not safely resolve '{resource.Expression}'."
            });
            return;
        }

        resources.Add(CreateResourceCandidate(
            definition,
            resource,
            resolved,
            match.Confidence,
            match.EvidenceKind,
            match.EvidenceDescription,
            allowDefaultSelection,
            accountWarning));
    }

    private static bool UsesStoreUserId(string expression) =>
        expression.Contains("<storeUserId>", StringComparison.OrdinalIgnoreCase);

    private static string BuildEvidenceDescription(
        string evidenceDescription,
        bool usesStoreUserIdWildcard,
        string? accountWarning)
    {
        var parts = new List<string> { evidenceDescription };
        if (usesStoreUserIdWildcard)
        {
            parts.Add("The unknown store user ID is preserved as a single path-segment wildcard.");
        }
        if (!string.IsNullOrWhiteSpace(accountWarning))
        {
            parts.Add(accountWarning);
        }
        return string.Join(" ", parts);
    }

    private BackupResourceCandidate CreateRegistryCandidate(
        LudusaviCompiledGame definition,
        LudusaviCompiledResource resource,
        DiscoveryConfidence confidence)
    {
        return new BackupResourceCandidate
        {
            ResourceId = resource.ResourceId,
            ProviderId = ProviderId,
            ProviderPriority = Descriptor.Priority,
            DisplayName = $"{definition.DisplayName} Registry",
            Kind = BackupResourceKind.Registry,
            SupportState = BackupResourceSupportState.UnsupportedRegistry,
            OriginalExpression = resource.Expression,
            Tags = resource.Tags,
            Constraints = FlattenConstraints(resource.Constraints),
            IsSelectedByDefault = false,
            Evidence = new[]
            {
                new DiscoveryEvidence
                {
                    Confidence = confidence,
                    Kind = "registry-rule",
                    Description = "The manifest contains Registry data, which this FolderRewind version cannot back up.",
                    Source = ProviderId
                }
            }
        };
    }

    private static IReadOnlyList<LudusaviCompiledGame> SelectDefinitions(
        IReadOnlyList<LudusaviCompiledGame> definitions,
        DiscoveryRequest request)
    {
        if (request.Mode != DiscoveryRequestMode.PresetTargeted)
        {
            return definitions;
        }

        var requested = request.Definitions
            .Where(reference => string.Equals(reference.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase))
            .Select(reference => reference.DefinitionId)
            .ToHashSet(StringComparer.Ordinal);
        return definitions.Where(definition => requested.Contains(definition.DefinitionId)).ToList();
    }

    private static IReadOnlyList<DefinitionInstallationMatches> MatchInstallationsToDefinitions(
        IReadOnlyList<LudusaviCompiledGame> definitions,
        IReadOnlyList<DetectedGameInstallation> installations,
        CancellationToken cancellationToken)
    {
        if (installations.Count == 0 || definitions.Count == 0)
        {
            return Array.Empty<DefinitionInstallationMatches>();
        }

        var matchesByDefinition = new Dictionary<LudusaviCompiledGame, List<InstallationMatch>>();
        var strongIdIndex = new Dictionary<string, List<LudusaviCompiledGame>>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var store in new[] { GameStore.Steam, GameStore.Gog })
            {
                var storeKey = StoreKey(store);
                foreach (var externalIdKey in new[] { storeKey, $"{storeKey}Extra" })
                {
                    if (!definition.ExternalIds.TryGetValue(externalIdKey, out var ids))
                    {
                        continue;
                    }
                    foreach (var id in SplitIds(ids))
                    {
                        AddLookup(strongIdIndex, StrongIdKey(store, id), definition);
                    }
                }
            }
        }

        var needsNameMatch = new List<DetectedGameInstallation>();
        foreach (var installation in installations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (installation.Store is GameStore.Steam or GameStore.Gog
                && !string.IsNullOrWhiteSpace(installation.StoreGameId)
                && strongIdIndex.TryGetValue(
                    StrongIdKey(installation.Store, installation.StoreGameId),
                    out var strongDefinitions))
            {
                foreach (var definition in strongDefinitions)
                {
                    AddMatch(matchesByDefinition, definition, new InstallationMatch(
                        installation,
                        DiscoveryConfidence.High,
                        "store-id",
                        $"Matched {installation.Store} ID {installation.StoreGameId}."));
                }
            }
            else
            {
                needsNameMatch.Add(installation);
            }
        }

        if (needsNameMatch.Count > 0)
        {
            var nameIndex = BuildNameIndex(definitions, cancellationToken);
            foreach (var installation in needsNameMatch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var installationNames = InstallationNames(installation);
                var nameDefinitions = installationNames
                    .Where(nameIndex.ContainsKey)
                    .SelectMany(name => nameIndex[name])
                    .Distinct()
                    .ToList();
                if (nameDefinitions.Count == 0)
                {
                    continue;
                }
                var installationAmbiguous = installations.Count(other =>
                    other.Store == installation.Store
                    && string.Equals(
                        NormalizeName(other.DisplayName),
                        NormalizeName(installation.DisplayName),
                        StringComparison.OrdinalIgnoreCase)) > 1;
                var ambiguous = installationAmbiguous || nameDefinitions.Count > 1;
                foreach (var definition in nameDefinitions)
                {
                    AddMatch(matchesByDefinition, definition, new InstallationMatch(
                        installation,
                        ambiguous ? DiscoveryConfidence.Low : DiscoveryConfidence.Medium,
                        ambiguous ? "ambiguous-name" : "name-and-install-dir",
                        ambiguous
                            ? "Only an ambiguous normalized name matched; resources are not selected by default."
                            : "Matched the normalized display name, alias, or install directory hint."));
                }
            }
        }

        return definitions
            .Where(matchesByDefinition.ContainsKey)
            .Select(definition => new DefinitionInstallationMatches(
                definition,
                matchesByDefinition[definition]
                    .GroupBy(match => InstallationKey(match.Installation), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(match => match.Confidence).First())
                    .ToList()))
            .ToList();
    }

    private static Dictionary<string, List<LudusaviCompiledGame>> BuildNameIndex(
        IReadOnlyList<LudusaviCompiledGame> definitions,
        CancellationToken cancellationToken)
    {
        var index = new Dictionary<string, List<LudusaviCompiledGame>>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var name in new[] { definition.DisplayName }
                         .Concat(definition.Aliases)
                         .Concat(definition.InstallDirectoryHints)
                         .Select(NormalizeName)
                         .Where(value => value.Length > 0)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                AddLookup(index, name, definition);
            }
        }
        return index;
    }

    private static IReadOnlyList<string> InstallationNames(DetectedGameInstallation installation) =>
        new[]
        {
            installation.DisplayName,
            installation.InstalledGameName,
            Path.GetFileName(installation.BasePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        }
        .Select(NormalizeName)
        .Where(value => value.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static void AddLookup(
        IDictionary<string, List<LudusaviCompiledGame>> lookup,
        string key,
        LudusaviCompiledGame definition)
    {
        if (!lookup.TryGetValue(key, out var values))
        {
            values = new List<LudusaviCompiledGame>();
            lookup[key] = values;
        }
        values.Add(definition);
    }

    private static void AddMatch(
        IDictionary<LudusaviCompiledGame, List<InstallationMatch>> matches,
        LudusaviCompiledGame definition,
        InstallationMatch match)
    {
        if (!matches.TryGetValue(definition, out var values))
        {
            values = new List<InstallationMatch>();
            matches[definition] = values;
        }
        values.Add(match);
    }

    private static string StrongIdKey(GameStore store, string id) => $"{store}:{id.Trim()}";

    private static bool MatchesConstraint(
        LudusaviCompiledResource resource,
        GameStore store,
        ICollection<DiscoveryDiagnostic> diagnostics,
        string definitionId)
    {
        if (resource.Constraints.Count == 0)
        {
            return true;
        }

        var storeKey = StoreKey(store);
        var knownStores = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "steam", "gog", "epic", "other", "microsoft", "origin", "uplay"
        };
        foreach (var constraint in resource.Constraints)
        {
            var osMatches = constraint.OperatingSystems.Count == 0
                            || constraint.OperatingSystems.Contains("windows", StringComparer.OrdinalIgnoreCase);
            if (!osMatches)
            {
                continue;
            }
            if (constraint.Stores.Count == 0)
            {
                return true;
            }
            var unknownStores = constraint.Stores.Where(item => !knownStores.Contains(item)).ToList();
            if (unknownStores.Count > 0)
            {
                diagnostics.Add(new DiscoveryDiagnostic
                {
                    Severity = DiscoveryDiagnosticSeverity.Warning,
                    Code = "unknown-store-constraint",
                    Message = $"Unknown Ludusavi store constraint: {string.Join(", ", unknownStores)}.",
                    ProviderId = ProviderId,
                    DefinitionId = definitionId
                });
            }
            if (store != GameStore.Unknown
                && constraint.Stores.Contains(storeKey, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IList<BackupResourceCandidate> DeduplicateResources(
        IEnumerable<BackupResourceCandidate> resources)
    {
        return resources
            .GroupBy(resource => resource.ResourceId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(resource => resource.Confidence)
                .ThenByDescending(resource => resource.FixedRootExists)
                .First())
            .ToList();
    }

    private static GameDefinition CreateDefinition(LudusaviCompiledGame definition) => new()
    {
        ProviderId = ProviderId,
        DefinitionId = definition.DefinitionId,
        DisplayName = definition.DisplayName,
        Aliases = definition.Aliases,
        ExternalIds = definition.ExternalIds,
        Notes = definition.Notes,
        NativeCloud = definition.NativeCloud
    };

    private static GameInstallation CreateInstallation(InstallationMatch match) => new()
    {
        InstallationId = InstallationKey(match.Installation),
        Store = match.Installation.Store,
        StoreGameId = match.Installation.StoreGameId,
        RootPath = match.Installation.RootPath,
        BasePath = match.Installation.BasePath,
        InstalledGameName = match.Installation.InstalledGameName,
        StoreUserIds = match.Installation.StoreUserIds,
        ActiveStoreUserId = match.Installation.ActiveStoreUserId,
        Evidence = new[]
        {
            new DiscoveryEvidence
            {
                Confidence = match.Confidence,
                Kind = match.EvidenceKind,
                Description = match.EvidenceDescription,
                Source = ProviderId
            }
        }
    };

    private static string InstallationKey(DetectedGameInstallation installation) =>
        $"{ProviderId}:{installation.Store}:{installation.StoreGameId}:{NormalizePath(installation.BasePath)}";

    private static string CreateResolvedResourceId(
        string resourceId,
        string root,
        IReadOnlyList<string> patterns)
    {
        var payload = $"{resourceId}|{NormalizePath(root)}|{string.Join("|", patterns.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static IReadOnlyDictionary<string, string> FlattenConstraints(
        IReadOnlyList<LudusaviCompiledConstraint> constraints)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var os = constraints.SelectMany(item => item.OperatingSystems).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var stores = constraints.SelectMany(item => item.Stores).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (os.Count > 0)
        {
            result["os"] = string.Join(",", os);
        }
        if (stores.Count > 0)
        {
            result["store"] = string.Join(",", stores);
        }
        return result;
    }

    private static string CreateResourceDisplayName(string gameName, IReadOnlyList<string> tags)
    {
        var suffix = tags.Count == 0 ? "Data" : string.Join(" + ", tags.Select(tag => tag.ToUpperInvariant()));
        return $"{gameName} {suffix}";
    }

    private static string StoreKey(GameStore store) => store switch
    {
        GameStore.Steam => "steam",
        GameStore.Gog => "gog",
        GameStore.Epic => "epic",
        GameStore.Standalone => "other",
        _ => string.Empty
    };

    private static IEnumerable<string> SplitIds(string ids) =>
        ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string NormalizeName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string NormalizePath(string value)
    {
        try
        {
            return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return value.Trim();
        }
    }

    private static DiscoveryDiagnostic Diagnostic(
        DiscoveryDiagnosticSeverity severity,
        string code,
        string message) => new()
    {
        Severity = severity,
        Code = code,
        Message = message,
        ProviderId = ProviderId
    };

    private sealed record InstallationMatch(
        DetectedGameInstallation Installation,
        DiscoveryConfidence Confidence,
        string EvidenceKind,
        string EvidenceDescription);

    private sealed record DefinitionInstallationMatches(
        LudusaviCompiledGame Definition,
        IReadOnlyList<InstallationMatch> Matches);
}

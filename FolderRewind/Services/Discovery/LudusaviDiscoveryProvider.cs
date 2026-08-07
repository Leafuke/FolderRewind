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
    private const int MaximumFallbackProbes = 5000;

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
        var detectedInstallations = _installationDiscovery.Scan(
            request.StoreRoots,
            request.DisabledAutoRoots);

        var definitions = SelectDefinitions(current.Value.Index.Games, request);
        var candidates = new List<DiscoveredGameCandidate>();
        var diagnostics = new List<DiscoveryDiagnostic>();
        var fallbackProbes = 0;
        for (var definitionIndex = 0; definitionIndex < definitions.Count; definitionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definition = definitions[definitionIndex];
            progress?.Report(new DiscoveryProgress
            {
                ProviderId = ProviderId,
                Phase = "resources",
                Message = definition.DisplayName,
                Completed = definitionIndex,
                Total = definitions.Count
            });

            var matches = detectedInstallations
                .Select(installation => MatchInstallation(definition, installation, detectedInstallations))
                .Where(match => match != null)
                .Cast<InstallationMatch>()
                .ToList();
            if (matches.Count > 0)
            {
                candidates.Add(CreateCandidate(definition, matches, diagnostics, cancellationToken));
                continue;
            }

            if (request.Mode == DiscoveryRequestMode.PresetTargeted)
            {
                continue;
            }

            var fallback = CreateFallbackCandidate(
                definition,
                ref fallbackProbes,
                diagnostics,
                cancellationToken);
            if (fallback != null)
            {
                candidates.Add(fallback);
            }
            if (fallbackProbes >= MaximumFallbackProbes)
            {
                diagnostics.Add(Diagnostic(
                    DiscoveryDiagnosticSeverity.Warning,
                    "fallback-probe-limit",
                    $"Fallback probing stopped after {MaximumFallbackProbes} stable path expressions."));
                break;
            }
        }

        stopwatch.Stop();
        return new DiscoveryProviderResult
        {
            ProviderId = ProviderId,
            Candidates = candidates,
            Diagnostics = diagnostics,
            Statistics = new DiscoveryScanStatistics
            {
                DefinitionsConsidered = definitions.Count,
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
            var userIds = match.Installation.StoreUserIds.Count == 0
                ? new[] { string.Empty }
                : match.Installation.StoreUserIds;
            foreach (var resource in definition.Files.Where(resource => !resource.IsDisabled))
            {
                if (!MatchesConstraint(resource, match.Installation.Store, diagnostics, definition.DefinitionId))
                {
                    continue;
                }

                foreach (var userId in userIds)
                {
                    var resolved = _resolver.Resolve(resource, match.Installation, userId);
                    if (resolved == null)
                    {
                        continue;
                    }
                    resources.Add(CreateResourceCandidate(
                        definition,
                        resource,
                        resolved,
                        match.Confidence,
                        match.EvidenceKind,
                        match.EvidenceDescription));
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
                    DisplayName = definition.DisplayName,
                    Resources = DeduplicateResources(resources)
                }
            }
        };
    }

    private DiscoveredGameCandidate? CreateFallbackCandidate(
        LudusaviCompiledGame definition,
        ref int probeCount,
        ICollection<DiscoveryDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var resources = new List<BackupResourceCandidate>();
        foreach (var resource in definition.Files.Where(resource => !resource.IsDisabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (probeCount >= MaximumFallbackProbes)
            {
                break;
            }
            if (!_resolver.CanProbeWithoutInstallation(resource.Expression, definition.DisplayName)
                || !MatchesConstraint(resource, GameStore.Unknown, diagnostics, definition.DefinitionId))
            {
                continue;
            }

            probeCount++;
            var resolved = _resolver.Resolve(resource, installation: null, storeUserId: string.Empty);
            if (resolved == null || resolved.CurrentMatchCount == 0)
            {
                continue;
            }
            resources.Add(CreateResourceCandidate(
                definition,
                resource,
                resolved,
                DiscoveryConfidence.Medium,
                "stable-path",
                "Matched a game-specific stable Windows path without launcher installation evidence."));
        }

        if (resources.Count == 0)
        {
            return null;
        }

        return new DiscoveredGameCandidate
        {
            StableKey = $"{ProviderId}:{definition.DefinitionId}",
            Definition = CreateDefinition(definition),
            BackupSets = new List<BackupSetCandidate>
            {
                new()
                {
                    StableKey = $"{ProviderId}:{definition.DefinitionId}:main",
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
        string evidenceDescription)
    {
        var support = string.IsNullOrWhiteSpace(resolved.FixedRoot)
            ? BackupResourceSupportState.InvalidPath
            : BackupResourceSupportState.Supported;
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
            CurrentMatchCount = resolved.CurrentMatchCount,
            CurrentSizeBytes = resolved.CurrentSizeBytes,
            IsSelectedByDefault = support == BackupResourceSupportState.Supported
                                  && resolved.CurrentMatchCount > 0
                                  && confidence >= DiscoveryConfidence.Medium,
            Evidence = new[]
            {
                new DiscoveryEvidence
                {
                    Confidence = confidence,
                    Kind = evidenceKind,
                    Description = evidenceDescription,
                    Source = ProviderId
                }
            }
        };
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
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return definitions.Where(definition => requested.Contains(definition.DefinitionId)).ToList();
    }

    private static InstallationMatch? MatchInstallation(
        LudusaviCompiledGame definition,
        DetectedGameInstallation installation,
        IReadOnlyList<DetectedGameInstallation> allInstallations)
    {
        var storeKey = StoreKey(installation.Store);
        if (installation.Store is GameStore.Steam or GameStore.Gog
            && definition.ExternalIds.TryGetValue(storeKey, out var ids)
            && SplitIds(ids).Contains(installation.StoreGameId, StringComparer.OrdinalIgnoreCase))
        {
            return new InstallationMatch(
                installation,
                DiscoveryConfidence.High,
                "store-id",
                $"Matched {installation.Store} ID {installation.StoreGameId}.");
        }

        var names = new[] { definition.DisplayName }
            .Concat(definition.Aliases)
            .Concat(definition.InstallDirectoryHints)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeName)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installationNames = new[]
        {
            installation.DisplayName,
            Path.GetFileName(installation.InstallPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        }.Select(NormalizeName).Where(value => value.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Overlaps(installationNames))
        {
            return null;
        }

        var ambiguous = allInstallations.Count(other =>
            other.Store == installation.Store
            && string.Equals(NormalizeName(other.DisplayName), NormalizeName(installation.DisplayName), StringComparison.OrdinalIgnoreCase)) > 1;
        var confidence = ambiguous ? DiscoveryConfidence.Low : DiscoveryConfidence.Medium;
        return new InstallationMatch(
            installation,
            confidence,
            ambiguous ? "ambiguous-name" : "name-and-install-dir",
            ambiguous
                ? "Only an ambiguous normalized name matched; resources are not selected by default."
                : "Matched the normalized display name, alias, or install directory hint.");
    }

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
                .ThenByDescending(resource => resource.CurrentMatchCount)
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
        InstallPath = match.Installation.InstallPath,
        LibraryRoot = match.Installation.LibraryRoot,
        StoreUserIds = match.Installation.StoreUserIds,
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
        $"{ProviderId}:{installation.Store}:{installation.StoreGameId}:{NormalizePath(installation.InstallPath)}";

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
}

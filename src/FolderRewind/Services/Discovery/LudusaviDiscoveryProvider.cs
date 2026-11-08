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

public sealed partial class LudusaviDiscoveryProvider : IGameDiscoveryProvider
{
    public const string ProviderId = "ludusavi";

    private readonly ILauncherInstallationDiscoveryService _installationDiscovery;
    private readonly LudusaviPathExpressionResolver _resolver;
    private readonly LudusaviManifestCacheService _cacheService;
    private readonly LudusaviGeneration? _generation;

    public LudusaviDiscoveryProvider(
        LudusaviManifestCacheService cacheService,
        ILauncherInstallationDiscoveryService? installationDiscovery = null,
        LudusaviPathExpressionResolver? resolver = null,
        LudusaviGeneration? generation = null)
    {
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _installationDiscovery = installationDiscovery ?? new LauncherInstallationDiscoveryService();
        _resolver = resolver ?? new LudusaviPathExpressionResolver();
        _generation = generation;
    }

    public DiscoveryProviderDescriptor Descriptor { get; } = new()
    {
        Id = ProviderId,
        DisplayName = "Ludusavi",
        Priority = 10,
        IsSpecialized = false
    };

    public Task<DiscoveryProviderResult> DiscoverAsync(DiscoveryRequest request,
        IProgress<DiscoveryProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => DiscoverStreamingAsync(request, progress, cancellationToken), cancellationToken);
    }

    private DiscoveredGameCandidate CreateCandidate(
        LudusaviCompiledGame definition,
        IReadOnlyList<InstallationMatch> matches,
        ICollection<DiscoveryDiagnostic> diagnostics,
        string discoveryRevision,
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
                        accountWarning: I18n.GetString("GameDiscovery_Evidence_NoActiveAccount"));
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
                            : string.Format(I18n.GetString("GameDiscovery_Evidence_InactiveAccount"), userId));
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
                    DiscoveryRevision = discoveryRevision,
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
                Message = string.Format(I18n.GetString("GameDiscovery_Diagnostic_PathResolutionFailed"), definition.DisplayName, resource.Expression)
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
            parts.Add(I18n.GetString("GameDiscovery_Evidence_AccountWildcard"));
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
            DisplayName = string.Format(I18n.GetString("GameDiscovery_RegistryResourceName"), definition.DisplayName),
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
                    Description = I18n.GetString("GameDiscovery_Evidence_RegistryUnsupported"),
                    Source = ProviderId
                }
            }
        };
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
                    Message = string.Format(I18n.GetString("GameDiscovery_Diagnostic_UnknownStoreConstraint"), string.Join(", ", unknownStores)),
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
        $"{ProviderId}:{installation.Store}:{installation.StoreGameId}:{DiscoveryIdentityPathV1.Encode(installation.BasePath)}";

    private static string CreateResolvedResourceId(
        string resourceId,
        string root,
        IReadOnlyList<string> patterns)
    {
        var payload = $"{resourceId}|{DiscoveryIdentityPathV1.Encode(root)}|{string.Join("|", patterns.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))}";
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
        var suffix = tags.Count == 0 ? I18n.GetString("GameDiscovery_ResourceTag_Data")
            : string.Join(" + ", tags.Select(tag => tag.ToLowerInvariant() switch
            {
                "save" => I18n.GetString("GameDiscovery_ResourceTag_Save"),
                "config" => I18n.GetString("GameDiscovery_ResourceTag_Config"),
                "other" => I18n.GetString("GameDiscovery_ResourceTag_Other"),
                _ => tag.ToUpperInvariant()
            }));
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

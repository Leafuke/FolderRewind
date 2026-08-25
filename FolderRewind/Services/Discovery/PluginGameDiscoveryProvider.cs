using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PluginDiscoveryCandidate = FolderRewind.Plugin.Abstractions.DiscoveryCandidate;
using PluginDiscoveryRequest = FolderRewind.Plugin.Abstractions.DiscoveryRequest;
using PluginDiagnostic = FolderRewind.Plugin.Abstractions.PluginDiagnostic;

namespace FolderRewind.Services.Discovery;

internal sealed class PluginGameDiscoveryProvider : IGameDiscoveryProvider
{
    public const int SpecializedProviderPriority = PluginDiscoveryCandidateMapper.SpecializedProviderPriority;

    private readonly PluginId _pluginId;
    private readonly DiscoveryProviderId _providerId;
    private readonly string _discoveryRevision;

    public PluginGameDiscoveryProvider(
        PluginId pluginId,
        DiscoveryProviderId providerId,
        string displayName,
        string pluginVersion)
    {
        _pluginId = pluginId;
        _providerId = providerId;
        _discoveryRevision = $"plugin:{pluginId.Value}@{pluginVersion}";
        Descriptor = new DiscoveryProviderDescriptor
        {
            Id = providerId.Value,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? pluginId.Value : displayName.Trim(),
            Priority = SpecializedProviderPriority,
            IsSpecialized = true
        };
    }

    public DiscoveryProviderDescriptor Descriptor { get; }

    public async Task<DiscoveryProviderResult> DiscoverAsync(
        Models.DiscoveryRequest request,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        using var lease = PluginV3RuntimeService.Runtime.TryAcquire<IDiscoveryCapability>(
            _pluginId,
            cancellationToken);
        if (lease == null)
        {
            return Result(
                diagnostics: [Diagnostic(DiscoveryDiagnosticSeverity.Error, "provider-inactive", "The plugin discovery provider is not active.")],
                duration: stopwatch.Elapsed);
        }
        if (lease.Capability.ProviderId != _providerId)
        {
            return Result(
                diagnostics: [Diagnostic(DiscoveryDiagnosticSeverity.Error, "provider-identity-changed", "The active plugin discovery identity no longer matches the registered provider.")],
                duration: stopwatch.Elapsed);
        }
        if (lease.Capability is not IDiscoveryDefinitionCatalog catalog)
        {
            return Result(
                diagnostics: [Diagnostic(DiscoveryDiagnosticSeverity.Error, "definition-catalog-unavailable", "The plugin must be updated before it can participate in game discovery.")],
                duration: stopwatch.Elapsed);
        }

        var diagnostics = new List<DiscoveryDiagnostic>();
        var definitions = PluginDiscoveryDefinitionPolicy.ValidateDefinitions(
            _providerId,
            catalog.Definitions,
            diagnostics);
        if (definitions.Count == 0)
        {
            return Result(diagnostics: diagnostics, duration: stopwatch.Elapsed);
        }

        var selection = PluginDiscoveryDefinitionPolicy.SelectTargets(
            _providerId,
            request,
            definitions,
            diagnostics);
        if (!selection.ShouldScan)
        {
            return Result(diagnostics: diagnostics, duration: stopwatch.Elapsed);
        }

        var roots = request.Mode == DiscoveryRequestMode.UserRoots
            ? PluginV3DiscoveryRootService.NormalizeUserRoots(request.UserRoots)
            : await PluginV3DiscoveryRootService.BuildDefaultRootsAsync(_pluginId).ConfigureAwait(false);
        progress?.Report(new DiscoveryProgress
        {
            ProviderId = _providerId.Value,
            Phase = "plugin-discovery",
            Message = Descriptor.DisplayName
        });

        var discovered = await lease.Capability.DiscoverAsync(
            new PluginDiscoveryRequest(roots),
            lease.Context).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        diagnostics.AddRange((discovered.Diagnostics ?? Array.Empty<PluginDiagnostic>()).Select(MapDiagnostic));

        var candidates = new List<DiscoveredGameCandidate>();
        foreach (var candidate in discovered.Candidates ?? Array.Empty<PluginDiscoveryCandidate>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PluginDiscoveryDefinitionPolicy.TryResolve(
                    _providerId,
                    catalog,
                    candidate,
                    definitions,
                    selection.RequestedDefinitions,
                    diagnostics,
                    out var definition))
            {
                continue;
            }

            try
            {
                var mapped = PluginDiscoveryCandidateMapper.Map(
                    _pluginId,
                    _providerId,
                    _discoveryRevision,
                    candidate,
                    definition!,
                    diagnostics);
                if (mapped != null)
                {
                    candidates.Add(mapped);
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add(Diagnostic(
                    DiscoveryDiagnosticSeverity.Warning,
                    "plugin-candidate-mapping-failed",
                    $"Candidate '{candidate?.CandidateId}' could not be mapped: {ex.Message}"));
            }
        }

        stopwatch.Stop();
        return Result(candidates, diagnostics, stopwatch.Elapsed);
    }

    private DiscoveryProviderResult Result(
        IReadOnlyList<DiscoveredGameCandidate>? candidates = null,
        IReadOnlyList<DiscoveryDiagnostic>? diagnostics = null,
        TimeSpan? duration = null)
    {
        candidates ??= Array.Empty<DiscoveredGameCandidate>();
        return new DiscoveryProviderResult
        {
            ProviderId = _providerId.Value,
            Candidates = candidates,
            Diagnostics = diagnostics ?? Array.Empty<DiscoveryDiagnostic>(),
            Statistics = new DiscoveryScanStatistics
            {
                DefinitionsConsidered = candidates.Select(value => value.Definition.DefinitionId).Distinct(StringComparer.Ordinal).Count(),
                ResourcesFound = candidates.Sum(value => value.BackupSets.Sum(set => set.Resources.Count)),
                Duration = duration ?? TimeSpan.Zero
            }
        };
    }

    private DiscoveryDiagnostic MapDiagnostic(PluginDiagnostic diagnostic)
    {
        var arguments = diagnostic.Arguments == null
            ? string.Empty
            : string.Join(", ", diagnostic.Arguments.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
        return new DiscoveryDiagnostic
        {
            Severity = diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => DiscoveryDiagnosticSeverity.Error,
                DiagnosticSeverity.Warning => DiscoveryDiagnosticSeverity.Warning,
                _ => DiscoveryDiagnosticSeverity.Information
            },
            Code = diagnostic.Code,
            Message = string.IsNullOrWhiteSpace(arguments) ? diagnostic.Code : $"{diagnostic.Code}: {arguments}",
            ProviderId = _providerId.Value,
            Category = diagnostic.Capability
        };
    }

    private DiscoveryDiagnostic Diagnostic(DiscoveryDiagnosticSeverity severity, string code, string message) => new()
    {
        Severity = severity,
        Code = code,
        Message = message,
        ProviderId = _providerId.Value,
        Category = "plugin-discovery"
    };

}

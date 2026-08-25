using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using PluginDiscoveryCandidate = FolderRewind.Plugin.Abstractions.DiscoveryCandidate;

namespace FolderRewind.Services.Discovery;

internal sealed class PluginDiscoveryTargetSelection
{
    public bool ShouldScan { get; init; }
    public IReadOnlySet<string>? RequestedDefinitions { get; init; }
}

internal static class PluginDiscoveryDefinitionPolicy
{
    public static Dictionary<string, DiscoveryDefinitionDescriptor> ValidateDefinitions(
        DiscoveryProviderId providerId,
        IReadOnlyList<DiscoveryDefinitionDescriptor>? declared,
        ICollection<DiscoveryDiagnostic> diagnostics)
    {
        var result = new Dictionary<string, DiscoveryDefinitionDescriptor>(StringComparer.Ordinal);
        foreach (var definition in declared ?? Array.Empty<DiscoveryDefinitionDescriptor>())
        {
            if (definition == null
                || string.IsNullOrWhiteSpace(definition.DefinitionId)
                || string.IsNullOrWhiteSpace(definition.DisplayName)
                || definition.Aliases == null
                || definition.ExternalIds == null
                || !result.TryAdd(definition.DefinitionId, definition))
            {
                diagnostics.Add(Diagnostic(
                    providerId,
                    DiscoveryDiagnosticSeverity.Error,
                    "definition-catalog-invalid",
                    "The plugin definition catalog contains an invalid or duplicate definition."));
            }
        }
        return result;
    }

    public static PluginDiscoveryTargetSelection SelectTargets(
        DiscoveryProviderId providerId,
        Models.DiscoveryRequest request,
        IReadOnlyDictionary<string, DiscoveryDefinitionDescriptor> definitions,
        ICollection<DiscoveryDiagnostic> diagnostics)
    {
        if (request.Mode != DiscoveryRequestMode.PresetTargeted)
        {
            return new PluginDiscoveryTargetSelection { ShouldScan = true };
        }

        var references = request.Definitions
            .Where(reference => reference != null
                && string.Equals(reference.ProviderId, providerId.Value, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (references.Count == 0)
        {
            return new PluginDiscoveryTargetSelection { ShouldScan = false };
        }

        var requested = references.Select(reference => reference.DefinitionId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
        var unknown = requested.Where(value => !definitions.ContainsKey(value)).ToList();
        if (unknown.Count > 0)
        {
            diagnostics.Add(Diagnostic(
                providerId,
                DiscoveryDiagnosticSeverity.Error,
                "definition-unavailable",
                $"The provider does not declare the requested definition(s): {string.Join(", ", unknown)}."));
        }
        requested.IntersectWith(definitions.Keys);
        return new PluginDiscoveryTargetSelection
        {
            ShouldScan = requested.Count > 0,
            RequestedDefinitions = requested
        };
    }

    public static bool TryResolve(
        DiscoveryProviderId providerId,
        IDiscoveryDefinitionCatalog catalog,
        PluginDiscoveryCandidate? candidate,
        IReadOnlyDictionary<string, DiscoveryDefinitionDescriptor> definitions,
        IReadOnlySet<string>? requestedDefinitions,
        ICollection<DiscoveryDiagnostic> diagnostics,
        out DiscoveryDefinitionDescriptor? definition)
    {
        definition = null;
        if (candidate == null)
        {
            diagnostics.Add(Diagnostic(
                providerId,
                DiscoveryDiagnosticSeverity.Warning,
                "plugin-candidate-invalid",
                "A plugin candidate is null."));
            return false;
        }

        string? definitionId;
        try
        {
            definitionId = catalog.ResolveDefinitionId(candidate);
        }
        catch (Exception ex)
        {
            diagnostics.Add(Diagnostic(
                providerId,
                DiscoveryDiagnosticSeverity.Warning,
                "definition-resolution-failed",
                $"Candidate '{candidate.CandidateId}' could not be resolved: {ex.Message}"));
            return false;
        }

        if (string.IsNullOrWhiteSpace(definitionId))
        {
            diagnostics.Add(Diagnostic(
                providerId,
                DiscoveryDiagnosticSeverity.Warning,
                "definition-unresolved",
                $"Candidate '{candidate.CandidateId}' was not assigned to a game definition."));
            return false;
        }
        if (!definitions.TryGetValue(definitionId, out definition))
        {
            diagnostics.Add(Diagnostic(
                providerId,
                DiscoveryDiagnosticSeverity.Warning,
                "definition-resolution-unknown",
                $"Candidate '{candidate.CandidateId}' resolved to undeclared definition '{definitionId}'."));
            return false;
        }
        if (requestedDefinitions != null && !requestedDefinitions.Contains(definitionId))
        {
            definition = null;
            return false;
        }
        return true;
    }

    private static DiscoveryDiagnostic Diagnostic(
        DiscoveryProviderId providerId,
        DiscoveryDiagnosticSeverity severity,
        string code,
        string message) => new()
    {
        Severity = severity,
        Code = code,
        Message = message,
        ProviderId = providerId.Value,
        Category = "plugin-discovery"
    };
}

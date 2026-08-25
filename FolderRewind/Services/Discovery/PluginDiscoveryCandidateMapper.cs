using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PluginDiscoveryCandidate = FolderRewind.Plugin.Abstractions.DiscoveryCandidate;

namespace FolderRewind.Services.Discovery;

internal static class PluginDiscoveryCandidateMapper
{
    public const int SpecializedProviderPriority = 100;

    public static DiscoveredGameCandidate? Map(
        PluginId pluginId,
        DiscoveryProviderId providerId,
        string discoveryRevision,
        PluginDiscoveryCandidate? candidate,
        DiscoveryDefinitionDescriptor definition,
        ICollection<DiscoveryDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (candidate == null || string.IsNullOrWhiteSpace(candidate.CandidateId))
        {
            diagnostics.Add(Diagnostic(providerId, "plugin-candidate-invalid", "A plugin candidate has no stable CandidateId."));
            return null;
        }
        if (candidate.ConfigDrafts == null || candidate.ConfigDrafts.Count != 1)
        {
            diagnostics.Add(Diagnostic(
                providerId,
                "plugin-candidate-shape-unsupported",
                $"Candidate '{candidate.CandidateId}' must contain exactly one ConfigDraft for game discovery."));
            return null;
        }

        var draft = candidate.ConfigDrafts[0];
        if (draft == null || draft.Folders == null)
        {
            diagnostics.Add(Diagnostic(
                providerId,
                "plugin-candidate-shape-unsupported",
                $"Candidate '{candidate.CandidateId}' contains an invalid ConfigDraft."));
            return null;
        }

        var resources = new List<BackupResourceCandidate>();
        var folderStates = new Dictionary<string, IReadOnlyDictionary<string, ProviderStatePayload>>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in draft.Folders)
        {
            if (folder == null)
            {
                diagnostics.Add(Diagnostic(
                    providerId,
                    "plugin-folder-invalid",
                    $"Candidate '{candidate.CandidateId}' contains an invalid FolderDraft."));
                continue;
            }
            try
            {
                var resource = MapFolder(pluginId, providerId, candidate.CandidateId, definition.DefinitionId, draft.Kind, folder);
                if (resources.Any(existing => string.Equals(existing.ResourceId, resource.ResourceId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                var providerStates = CloneStates(folder.ProviderStates);
                resources.Add(resource);
                folderStates[resource.ResourceId] = providerStates;
            }
            catch (Exception ex)
            {
                diagnostics.Add(Diagnostic(
                    providerId,
                    "plugin-folder-mapping-failed",
                    $"Candidate '{candidate.CandidateId}' contains a FolderDraft that could not be mapped: {ex.Message}"));
            }
        }

        var context = new PluginDiscoveryDraftContext
        {
            PluginId = pluginId.Value,
            ProviderId = providerId.Value,
            CandidateId = candidate.CandidateId,
            Kind = new ConfigKindReference
            {
                OwnerId = draft.Kind.OwnerId.Value,
                KindId = draft.Kind.KindId
            },
            ConfigProviderStates = CloneStates(draft.ProviderStates),
            FolderProviderStatesByResourceId = folderStates
        };
        var set = new BackupSetCandidate
        {
            StableKey = StableKey("plugin-set:v1", providerId.Value, definition.DefinitionId, candidate.CandidateId),
            Identity = new DiscoverySetIdentity
            {
                ProviderId = providerId.Value,
                DefinitionId = definition.DefinitionId,
                SetId = candidate.CandidateId,
                ExternalIds = new Dictionary<string, string>(definition.ExternalIds, StringComparer.OrdinalIgnoreCase)
            },
            DisplayName = string.IsNullOrWhiteSpace(candidate.DisplayName) ? draft.SuggestedName : candidate.DisplayName,
            DiscoveryRevision = discoveryRevision,
            PluginDraftContext = context,
            Resources = resources
        };
        return new DiscoveredGameCandidate
        {
            StableKey = $"plugin-game:v1:{providerId.Value}:{definition.DefinitionId}",
            Definition = new GameDefinition
            {
                ProviderId = providerId.Value,
                DefinitionId = definition.DefinitionId,
                DisplayName = definition.DisplayName,
                Aliases = definition.Aliases.ToArray(),
                ExternalIds = new Dictionary<string, string>(definition.ExternalIds, StringComparer.OrdinalIgnoreCase)
            },
            BackupSets = [set]
        };
    }

    private static BackupResourceCandidate MapFolder(
        PluginId pluginId,
        DiscoveryProviderId providerId,
        string candidateId,
        string definitionId,
        ConfigKindRef kind,
        FolderDraft folder)
    {
        var path = NormalizeAbsolutePath(folder.Path, out var valid);
        var unsafeRoot = valid && BackupSourceRootSafetyPolicy.IsBroadRoot(path);
        var support = !valid
            ? BackupResourceSupportState.InvalidPath
            : unsafeRoot
                ? BackupResourceSupportState.UnsafeRoot
                : BackupResourceSupportState.Supported;
        var exists = support == BackupResourceSupportState.Supported && Directory.Exists(path);
        var resourceId = StableKey(
            "plugin-resource:v1",
            providerId.Value,
            definitionId,
            candidateId,
            kind.OwnerId.Value,
            kind.KindId,
            path.ToUpperInvariant());
        return new BackupResourceCandidate
        {
            ResourceId = resourceId,
            ProviderId = providerId.Value,
            ProviderPriority = SpecializedProviderPriority,
            IsSpecializedProvider = true,
            DisplayName = string.IsNullOrWhiteSpace(folder.DisplayName) ? Path.GetFileName(path) : folder.DisplayName,
            Kind = BackupResourceKind.Directory,
            SupportState = support,
            FixedRoot = path,
            OriginalExpression = folder.Path,
            FixedRootExists = exists,
            IsSelectedByDefault = exists,
            SafetyWarning = unsafeRoot ? "The plugin returned an unsafe broad source root." : string.Empty,
            Evidence =
            [
                new DiscoveryEvidence
                {
                    Confidence = DiscoveryConfidence.High,
                    Kind = "plugin-discovery",
                    Description = $"Discovered directly by specialized plugin '{pluginId.Value}'.",
                    Source = providerId.Value
                }
            ]
        };
    }

    private static IReadOnlyDictionary<string, ProviderStatePayload> CloneStates(
        IReadOnlyDictionary<StateOwnerId, ProviderStateDraft>? states)
    {
        return (states ?? new Dictionary<StateOwnerId, ProviderStateDraft>()).ToDictionary(
            pair => pair.Key.Value,
            pair => new ProviderStatePayload
            {
                SchemaVersion = pair.Value.SchemaVersion,
                Data = pair.Value.Data.Clone()
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeAbsolutePath(string? path, out bool valid)
    {
        valid = false;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return path?.Trim() ?? string.Empty;
        }
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            valid = true;
            if (!string.IsNullOrEmpty(root)
                && string.Equals(
                    fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }
            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }

    private static string StableKey(string prefix, params string[] values)
    {
        var payload = string.Join("\u001f", new[] { prefix }.Concat(values.Select(value => value ?? string.Empty)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static DiscoveryDiagnostic Diagnostic(
        DiscoveryProviderId providerId,
        string code,
        string message) => new()
    {
        Severity = DiscoveryDiagnosticSeverity.Warning,
        Code = code,
        Message = message,
        ProviderId = providerId.Value,
        Category = "plugin-discovery"
    };
}

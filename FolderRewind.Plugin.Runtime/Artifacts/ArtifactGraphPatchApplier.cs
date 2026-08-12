using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Artifacts;

public static class ArtifactGraphPatchApplier
{
    public static ArtifactLedgerDocument Apply(
        ArtifactLedgerDocument current,
        ArtifactGraphPatch patch,
        ArtifactPatchScope scope,
        IReadOnlyDictionary<ArtifactStagingHandle, StagedArtifactFacts> stagedFacts)
    {
        ArtifactLedgerValidator.Validate(current);
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(stagedFacts);
        if (patch.ExpectedRevision != current.Revision)
        {
            throw new InvalidOperationException("Artifact graph patch is stale.");
        }
        if (scope.CommittedRevision == current.Revision)
        {
            throw new InvalidOperationException("Host must issue a new Artifact graph revision.");
        }
        ArgumentNullException.ThrowIfNull(patch.AddedArtifacts);
        ArgumentNullException.ThrowIfNull(patch.RootReplacements);

        var artifacts = current.Artifacts.ToDictionary(artifact => artifact.ArtifactId);
        var addedIds = patch.AddedArtifacts.Select(node => node.ArtifactId).ToHashSet();
        if (addedIds.Count != patch.AddedArtifacts.Count)
        {
            throw new InvalidOperationException("Artifact graph patch contains duplicate ArtifactIds.");
        }

        foreach (var node in patch.AddedArtifacts)
        {
            if (artifacts.ContainsKey(node.ArtifactId)
                || !stagedFacts.TryGetValue(node.Staging, out var facts)
                || facts.ArtifactId != node.ArtifactId)
            {
                throw new InvalidOperationException("Artifact graph patch references an invalid staging allocation.");
            }
            if (!StringComparer.Ordinal.Equals(node.Format.OwnerId.Value, scope.PluginId.Value)
                || node.RestoreStrategyId.PluginId != scope.PluginId
                || node.FormatVersion < 0
                || string.IsNullOrWhiteSpace(node.HistoryItemId)
                || !scope.ReplaceableHistoryRoots.ContainsKey(node.HistoryItemId))
            {
                throw new InvalidOperationException("Plugins may only emit their own Artifact formats and restore strategies.");
            }
            foreach (var dependency in node.Dependencies)
            {
                if (!scope.ReadableArtifacts.Contains(dependency) && !addedIds.Contains(dependency))
                {
                    throw new InvalidOperationException("Artifact graph patch references an Artifact outside the request scope.");
                }
            }

            artifacts.Add(node.ArtifactId, new ArtifactLedgerEntry(
                node.ArtifactId,
                node.Format,
                node.FormatVersion,
                node.RestoreStrategyId,
                scope.ConfigId,
                scope.FolderId,
                node.HistoryItemId,
                facts.ContentRelativePath,
                facts.LogicalSha256,
                facts.LogicalSize,
                facts.StorageSha256,
                facts.StorageSize,
                node.Completeness,
                node.CoreCaptureMode,
                node.Dependencies.ToArray(),
                scope.TransactionId,
                ArtifactAvailability.Available,
                ArtifactAvailability.Pending));
        }

        var roots = current.HistoryRoots.ToDictionary(root => root.HistoryItemId, StringComparer.Ordinal);
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var replacement in patch.RootReplacements)
        {
            if (!replaced.Add(replacement.HistoryItemId)
                || !scope.ReplaceableHistoryRoots.TryGetValue(replacement.HistoryItemId, out var allowedRoot)
                || allowedRoot != replacement.ExpectedRootArtifactId
                || !roots.TryGetValue(replacement.HistoryItemId, out var currentRoot)
                || currentRoot.RootArtifactId != replacement.ExpectedRootArtifactId
                || !artifacts.ContainsKey(replacement.NewRootArtifactId))
            {
                throw new InvalidOperationException("Artifact root replacement is not authorized by the transform request.");
            }
            roots[replacement.HistoryItemId] = currentRoot with { RootArtifactId = replacement.NewRootArtifactId };
        }

        var candidate = new ArtifactLedgerDocument(
            ArtifactLedgerValidator.CurrentSchemaVersion,
            scope.CommittedRevision,
            artifacts.Values.ToArray(),
            roots.Values.ToArray());
        ArtifactLedgerValidator.Validate(candidate);
        return candidate;
    }
}

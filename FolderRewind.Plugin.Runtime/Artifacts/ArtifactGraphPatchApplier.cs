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
                || node.FormatVersion < 0)
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

        if ((!addedIds.Contains(patch.ResultRootArtifactId)
                && !scope.ReadableArtifacts.Contains(patch.ResultRootArtifactId))
            || !artifacts.TryGetValue(patch.ResultRootArtifactId, out var resultRoot)
            || !StringComparer.Ordinal.Equals(resultRoot.ConfigId, scope.ConfigId)
            || resultRoot.FolderId != scope.FolderId)
        {
            throw new InvalidOperationException("Artifact transform result root is outside the request scope.");
        }

        var candidate = new ArtifactLedgerDocument(
            ArtifactLedgerValidator.CurrentSchemaVersion,
            scope.CommittedRevision,
            artifacts.Values.ToArray());
        ArtifactLedgerValidator.Validate(candidate);
        return candidate;
    }
}

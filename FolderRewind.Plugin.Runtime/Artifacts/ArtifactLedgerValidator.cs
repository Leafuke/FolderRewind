using System.Security.Cryptography;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Artifacts;

public static class ArtifactLedgerValidator
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumArtifacts = 4096;
    public const int MaximumHistoryRoots = 4096;
    public const int MaximumDependencyDepth = 256;

    private static readonly ArtifactFormatRef CoreFormat = new(new OwnerId("folderrewind.core"), "archive-set");
    private static readonly RestoreStrategyId CoreStrategy = new(new PluginId("folderrewind.core"), "archive-materializer");

    public static void Validate(ArtifactLedgerDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(document.Artifacts);
        ArgumentNullException.ThrowIfNull(document.HistoryRoots);
        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported Artifact Ledger schema {document.SchemaVersion}.");
        }
        if (string.IsNullOrWhiteSpace(document.Revision.Value))
        {
            throw new InvalidDataException("Artifact Ledger revision is required.");
        }
        if (document.Artifacts.Count > MaximumArtifacts || document.HistoryRoots.Count > MaximumHistoryRoots)
        {
            throw new InvalidDataException("Artifact Ledger exceeds its bounded graph limits.");
        }

        var artifacts = new Dictionary<ArtifactId, ArtifactLedgerEntry>();
        var contentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in document.Artifacts)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            if (!artifacts.TryAdd(artifact.ArtifactId, artifact))
            {
                throw new InvalidDataException($"Duplicate ArtifactId '{artifact.ArtifactId}'.");
            }
            ValidateArtifact(artifact);
            var canonicalPath = ArtifactPathRules.NormalizeRelativePath(artifact.ContentRelativePath);
            if (!StringComparer.Ordinal.Equals(canonicalPath, artifact.ContentRelativePath)
                || !contentPaths.Add(canonicalPath))
            {
                throw new InvalidDataException("Artifact content paths must be canonical and collision-free.");
            }
        }

        var historyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in document.HistoryRoots)
        {
            ArgumentNullException.ThrowIfNull(root);
            if (string.IsNullOrWhiteSpace(root.HistoryItemId) || !historyIds.Add(root.HistoryItemId))
            {
                throw new InvalidDataException("History root identities must be unique and non-empty.");
            }
            if (string.IsNullOrWhiteSpace(root.ConfigId) || root.FolderId == Guid.Empty
                || !artifacts.TryGetValue(root.RootArtifactId, out var rootArtifact))
            {
                throw new InvalidDataException("History root targets an invalid Artifact.");
            }
            if (!StringComparer.Ordinal.Equals(root.ConfigId, rootArtifact.ConfigId)
                || root.FolderId != rootArtifact.FolderId
                || !StringComparer.Ordinal.Equals(root.HistoryItemId, rootArtifact.HistoryItemId))
            {
                throw new InvalidDataException("History root and root Artifact ownership do not match.");
            }
        }

        ValidateDependencies(artifacts);
    }

    public static IReadOnlySet<ArtifactId> ComputeReachable(
        ArtifactLedgerDocument document,
        IEnumerable<string>? historyItemIds = null)
        => ComputeReachableFromRoots(
            document,
            LegacyHistoryRootAdapter.GetArtifactRoots(document, historyItemIds));

    public static IReadOnlySet<ArtifactId> ComputeReachableFromRoots(
        ArtifactLedgerDocument document,
        IEnumerable<ArtifactId> artifactRootIds)
        => BuildClosure(document, artifactRootIds).Reachable;

    public static ArtifactClosurePlan BuildClosure(
        ArtifactLedgerDocument document,
        IEnumerable<ArtifactId> artifactRootIds)
    {
        Validate(document);
        ArgumentNullException.ThrowIfNull(artifactRootIds);
        var roots = artifactRootIds.Distinct().ToArray();
        var byId = document.Artifacts.ToDictionary(artifact => artifact.ArtifactId);
        var reachable = new HashSet<ArtifactId>();
        var ordered = new List<ArtifactId>();
        foreach (var root in roots)
        {
            if (!byId.ContainsKey(root))
            {
                throw new KeyNotFoundException($"Artifact root '{root}' is missing from the ledger.");
            }
            Visit(root);
        }

        return new ArtifactClosurePlan(roots, reachable, ordered);

        void Visit(ArtifactId artifactId)
        {
            if (!reachable.Add(artifactId)) return;
            foreach (var dependency in byId[artifactId].Dependencies)
            {
                Visit(dependency);
            }
            ordered.Add(artifactId);
        }
    }

    private static void ValidateArtifact(ArtifactLedgerEntry artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact.ConfigId)
            || artifact.FolderId == Guid.Empty
            || string.IsNullOrWhiteSpace(artifact.TransactionId)
            || artifact.FormatVersion < 0
            || artifact.LogicalSize < 0
            || artifact.StorageSize < 0
            || !IsSha256(artifact.LogicalSha256)
            || !IsSha256(artifact.StorageSha256))
        {
            throw new InvalidDataException($"Artifact '{artifact.ArtifactId}' contains invalid facts.");
        }

        var isCore = artifact.Format.OwnerId.Value == "folderrewind.core";
        if (isCore)
        {
            if (artifact.Format != CoreFormat || artifact.FormatVersion != 1 || artifact.RestoreStrategyId != CoreStrategy)
            {
                throw new InvalidDataException("Core Artifacts must use archive-set v1 and the Core materializer.");
            }
        }
        else if (!StringComparer.Ordinal.Equals(
                     artifact.Format.OwnerId.Value,
                     artifact.RestoreStrategyId.PluginId.Value))
        {
            throw new InvalidDataException("Plugin Artifact format and Restore Strategy owners must match.");
        }

        ArgumentNullException.ThrowIfNull(artifact.Dependencies);
        if (artifact.Dependencies.Count != artifact.Dependencies.Distinct().Count())
        {
            throw new InvalidDataException($"Artifact '{artifact.ArtifactId}' contains duplicate dependencies.");
        }
    }

    private static void ValidateDependencies(IReadOnlyDictionary<ArtifactId, ArtifactLedgerEntry> artifacts)
    {
        foreach (var artifact in artifacts.Values)
        {
            foreach (var dependencyId in artifact.Dependencies)
            {
                if (dependencyId == artifact.ArtifactId || !artifacts.TryGetValue(dependencyId, out var dependency))
                {
                    throw new InvalidDataException($"Artifact '{artifact.ArtifactId}' has a missing or self dependency.");
                }
                if (!StringComparer.Ordinal.Equals(artifact.ConfigId, dependency.ConfigId)
                    || artifact.FolderId != dependency.FolderId)
                {
                    throw new InvalidDataException("Artifact dependencies cannot cross ConfigId or FolderId.");
                }
            }
        }

        var visiting = new HashSet<ArtifactId>();
        var depths = new Dictionary<ArtifactId, int>();
        foreach (var artifactId in artifacts.Keys) Depth(artifactId);

        int Depth(ArtifactId artifactId)
        {
            if (depths.TryGetValue(artifactId, out var known)) return known;
            if (!visiting.Add(artifactId))
            {
                throw new InvalidDataException("Artifact dependency graph contains a cycle.");
            }
            var depth = artifacts[artifactId].Dependencies.Count == 0
                ? 1
                : checked(1 + artifacts[artifactId].Dependencies.Max(Depth));
            visiting.Remove(artifactId);
            if (depth > MaximumDependencyDepth)
            {
                throw new InvalidDataException("Artifact dependency graph exceeds its maximum depth.");
            }
            depths.Add(artifactId, depth);
            return depth;
        }
    }

    private static bool IsSha256(string value)
    {
        if (value?.Length != SHA256.HashSizeInBytes * 2) return false;
        return value.All(static character => char.IsAsciiHexDigit(character));
    }
}

public static class ArtifactPathRules
{
    public static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathFullyQualified(relativePath)
            || relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidDataException("Artifact path must be relative and cannot contain an alternate data stream.");
        }

        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".." || string.IsNullOrWhiteSpace(segment)))
        {
            throw new InvalidDataException("Artifact path contains an invalid segment.");
        }
        return string.Join('/', segments);
    }

    public static string ResolveUnderRoot(string root, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Artifact path escapes its Host-owned root.");
        }
        return candidate;
    }
}

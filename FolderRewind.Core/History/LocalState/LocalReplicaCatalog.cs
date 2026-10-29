using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json.Serialization;

namespace FolderRewind.History.LocalState;

public enum LocalReplicaLocatorKind
{
    ControlledAbsolutePath = 0,
    StableRootRelativePath = 1
}

public sealed record LocalReplicaLocator
{
    [JsonConstructor]
    public LocalReplicaLocator(
        LocalReplicaLocatorKind kind,
        string absolutePath,
        string stableRootId,
        string relativePath)
    {
        absolutePath ??= string.Empty;
        stableRootId ??= string.Empty;
        relativePath ??= string.Empty;
        if (kind == LocalReplicaLocatorKind.ControlledAbsolutePath
            && (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathFullyQualified(absolutePath)))
        {
            throw new ArgumentException("Controlled absolute locator is invalid.", nameof(absolutePath));
        }
        if (kind == LocalReplicaLocatorKind.StableRootRelativePath
            && (string.IsNullOrWhiteSpace(stableRootId)
                || !HistoryRepositoryPaths.IsSafeRepositoryRelativePath(relativePath)))
        {
            throw new ArgumentException("Stable-root locator is invalid.", nameof(relativePath));
        }

        Kind = kind;
        AbsolutePath = kind == LocalReplicaLocatorKind.ControlledAbsolutePath
            ? Path.GetFullPath(absolutePath)
            : string.Empty;
        StableRootId = kind == LocalReplicaLocatorKind.StableRootRelativePath ? stableRootId.Trim() : string.Empty;
        RelativePath = kind == LocalReplicaLocatorKind.StableRootRelativePath
            ? relativePath.Replace('\\', '/')
            : string.Empty;
    }

    public LocalReplicaLocatorKind Kind { get; }
    public string AbsolutePath { get; }
    public string StableRootId { get; }
    public string RelativePath { get; }

    public static LocalReplicaLocator ControlledAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Local replica path must be fully qualified.", nameof(path));
        }

        return new LocalReplicaLocator(
            LocalReplicaLocatorKind.ControlledAbsolutePath,
            Path.GetFullPath(path),
            string.Empty,
            string.Empty);
    }

    public static LocalReplicaLocator StableRootRelative(string stableRootId, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(stableRootId))
        {
            throw new ArgumentException("Stable root identity cannot be empty.", nameof(stableRootId));
        }

        var normalized = relativePath?.Replace('\\', '/') ?? string.Empty;
        if (!HistoryRepositoryPaths.IsSafeRepositoryRelativePath(normalized))
        {
            throw new ArgumentException("Local replica relative path is unsafe.", nameof(relativePath));
        }

        return new LocalReplicaLocator(
            LocalReplicaLocatorKind.StableRootRelativePath,
            string.Empty,
            stableRootId.Trim(),
            normalized);
    }

    public string Resolve(IReadOnlyDictionary<string, string>? stableRoots = null)
    {
        if (Kind == LocalReplicaLocatorKind.ControlledAbsolutePath)
        {
            return AbsolutePath;
        }

        if (stableRoots is null || !stableRoots.TryGetValue(StableRootId, out var root))
        {
            throw new KeyNotFoundException($"Stable local root '{StableRootId}' is unavailable.");
        }

        var fullRoot = Path.GetFullPath(root);
        var resolved = Path.GetFullPath(Path.Combine(fullRoot, RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(fullRoot, resolved);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Local replica locator escapes its stable root.");
        }

        return resolved;
    }
}

public sealed record LocalReplicaCatalogEntry(
    RepresentationId RepresentationId,
    LocalReplicaId LocalReplicaId,
    LocalReplicaLocator Locator,
    DateTimeOffset RegisteredAtUtc);

public sealed record LocalReplicaCatalog
{
    public const int CurrentFormatVersion = 1;

    public LocalReplicaCatalog(
        HistoryConfigId configId,
        long catalogRevision,
        IEnumerable<LocalReplicaCatalogEntry>? entries)
        : this(configId, catalogRevision, entries is null ? [] : [.. entries])
    {
    }

    [JsonConstructor]
    public LocalReplicaCatalog(
        HistoryConfigId configId,
        long catalogRevision,
        ImmutableArray<LocalReplicaCatalogEntry> entries)
    {
        if (catalogRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(catalogRevision));
        }

        ConfigId = configId;
        CatalogRevision = catalogRevision;
        Entries = entries.IsDefault ? [] : entries;
    }

    public int FormatVersion => CurrentFormatVersion;
    public HistoryConfigId ConfigId { get; }
    public long CatalogRevision { get; }
    public ImmutableArray<LocalReplicaCatalogEntry> Entries { get; }
}

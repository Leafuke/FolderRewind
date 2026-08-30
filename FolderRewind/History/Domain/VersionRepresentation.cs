using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace FolderRewind.History.Domain;

public enum RepresentationKind
{
    CoreFull = 0,
    CoreRolling = 1,
    CoreSmartDelta = 2,
    PluginArtifact = 3,
    LegacyArchive = 4
}

public enum MaterializationFidelity
{
    Exact = 0,
    Partial = 1,
    Unknown = 2
}

public sealed record VersionRepresentation
{
    public VersionRepresentation(
        RepresentationId representationId,
        VersionId versionId,
        RepresentationKind kind,
        string format,
        IEnumerable<RepresentationId>? dependencyRepresentationIds,
        MaterializationFidelity fidelity,
        string? logicalSha256,
        string? stateFingerprint,
        IEnumerable<KeyValuePair<string, string>>? representationSpecificMetadata)
        : this(
            representationId,
            versionId,
            kind,
            format,
            DomainCollections.Freeze(dependencyRepresentationIds),
            fidelity,
            logicalSha256,
            stateFingerprint,
            DomainCollections.FreezeMetadata(representationSpecificMetadata))
    {
    }

    [JsonConstructor]
    public VersionRepresentation(
        RepresentationId representationId,
        VersionId versionId,
        RepresentationKind kind,
        string format,
        ImmutableArray<RepresentationId> dependencyRepresentationIds,
        MaterializationFidelity fidelity,
        string? logicalSha256,
        string? stateFingerprint,
        ImmutableSortedDictionary<string, string>? representationSpecificMetadata)
    {
        RepresentationId = representationId;
        VersionId = versionId;
        Kind = kind;
        Format = string.IsNullOrWhiteSpace(format)
            ? throw new ArgumentException("Representation format cannot be empty.", nameof(format))
            : format.Trim();
        DependencyRepresentationIds = dependencyRepresentationIds.IsDefault
            ? ImmutableArray<RepresentationId>.Empty
            : dependencyRepresentationIds;
        Fidelity = fidelity;
        LogicalSha256 = string.IsNullOrWhiteSpace(logicalSha256) ? null : logicalSha256.Trim().ToLowerInvariant();
        StateFingerprint = string.IsNullOrWhiteSpace(stateFingerprint) ? null : stateFingerprint.Trim();
        RepresentationSpecificMetadata = representationSpecificMetadata
            ?? ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
    }

    public RepresentationId RepresentationId { get; }
    public VersionId VersionId { get; }
    public RepresentationKind Kind { get; }
    public string Format { get; }
    public ImmutableArray<RepresentationId> DependencyRepresentationIds { get; }
    public MaterializationFidelity Fidelity { get; }
    public string? LogicalSha256 { get; }
    public string? StateFingerprint { get; }
    public ImmutableSortedDictionary<string, string> RepresentationSpecificMetadata { get; }
}

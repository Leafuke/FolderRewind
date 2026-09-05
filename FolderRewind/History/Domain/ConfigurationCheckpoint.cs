using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Serialization;

namespace FolderRewind.History.Domain;

public enum CheckpointSourceDisposition
{
    Captured = 0,
    Reused = 1,
    CarriedForward = 2,
    Unavailable = 3,
    Failed = 4
}

public enum CheckpointCreationKind
{
    Capture = 0,
    Merge = 1,
    SafetySnapshot = 2,
    Aggregate = 3,
    Import = 4
}

public sealed record CheckpointSource
{
    public CheckpointSource(
        SourceId sourceId,
        SourceDescriptorSnapshot sourceDescriptorSnapshot,
        VersionId? versionId,
        CheckpointSourceDisposition disposition,
        EffectiveSourceBoundarySnapshot? effectiveSourceBoundary = null)
    {
        SourceId = sourceId;
        SourceDescriptorSnapshot = sourceDescriptorSnapshot;
        VersionId = versionId;
        Disposition = disposition;
        EffectiveSourceBoundary = effectiveSourceBoundary ?? EffectiveSourceBoundarySnapshot.All;
    }

    public SourceId SourceId { get; }
    public SourceDescriptorSnapshot SourceDescriptorSnapshot { get; }
    public VersionId? VersionId { get; }
    public CheckpointSourceDisposition Disposition { get; }
    public EffectiveSourceBoundarySnapshot EffectiveSourceBoundary { get; }
    public string EffectiveSourceBoundaryFingerprint => EffectiveSourceBoundary.Fingerprint;
}

public sealed record ConfigurationCheckpoint
{
    public ConfigurationCheckpoint(
        CheckpointId checkpointId,
        HistoryConfigId configId,
        DateTimeOffset createdAtUtc,
        RunId? createdByRunId,
        HistoryProvenance origin,
        IEnumerable<CheckpointSource> sources,
        IEnumerable<CheckpointId>? parentCheckpointIds = null,
        CheckpointCreationKind creationKind = CheckpointCreationKind.Capture)
        : this(
            checkpointId,
            configId,
            createdAtUtc,
            createdByRunId,
            origin,
            DomainCollections.Freeze(sources),
            DomainCollections.Freeze(parentCheckpointIds),
            creationKind)
    {
    }

    [JsonConstructor]
    public ConfigurationCheckpoint(
        CheckpointId checkpointId,
        HistoryConfigId configId,
        DateTimeOffset createdAtUtc,
        RunId? createdByRunId,
        HistoryProvenance origin,
        ImmutableArray<CheckpointSource> sources,
        ImmutableArray<CheckpointId> parentCheckpointIds = default,
        CheckpointCreationKind creationKind = CheckpointCreationKind.Capture)
    {
        CheckpointId = checkpointId;
        ConfigId = configId;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        CreatedByRunId = createdByRunId;
        Origin = origin ?? throw new ArgumentNullException(nameof(origin));
        Sources = sources.IsDefault ? ImmutableArray<CheckpointSource>.Empty : sources;
        ParentCheckpointIds = parentCheckpointIds.IsDefault
            ? ImmutableArray<CheckpointId>.Empty
            : parentCheckpointIds;
        CreationKind = creationKind;
    }

    public CheckpointId CheckpointId { get; }
    public HistoryConfigId ConfigId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public RunId? CreatedByRunId { get; }
    public HistoryProvenance Origin { get; }
    public ImmutableArray<CheckpointSource> Sources { get; }
    public ImmutableArray<CheckpointId> ParentCheckpointIds { get; }
    public CheckpointCreationKind CreationKind { get; }
    public bool IsStructurallyComplete => Sources.All(source => source.VersionId is not null);
}

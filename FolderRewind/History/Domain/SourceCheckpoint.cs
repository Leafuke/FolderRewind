using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Serialization;

namespace FolderRewind.History.Domain;

public enum CheckpointSourceDisposition { Captured = 0, Reused = 1, CarriedForward = 2, Unavailable = 3, Failed = 4 }
public enum CheckpointCreationKind { Capture = 0, Merge = 1, SafetySnapshot = 2, Aggregate = 3, Import = 4 }

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

/// <summary>A history position for one Source. Parents describe continuation, not content ancestry.</summary>
public sealed record SourceCheckpoint
{
    [JsonConstructor]
    public SourceCheckpoint(CheckpointId checkpointId, HistoryConfigId configId, SourceId sourceId,
        VersionId versionId, SourceDescriptorSnapshot sourceDescriptorSnapshot,
        EffectiveSourceBoundarySnapshot effectiveSourceBoundary, DateTimeOffset createdAtUtc,
        RunId? createdByRunId, HistoryProvenance origin, ImmutableArray<CheckpointId> parentCheckpointIds,
        CheckpointCreationKind creationKind = CheckpointCreationKind.Capture)
    {
        CheckpointId = checkpointId; ConfigId = configId; SourceId = sourceId; VersionId = versionId;
        SourceDescriptorSnapshot = sourceDescriptorSnapshot;
        EffectiveSourceBoundary = effectiveSourceBoundary ?? throw new ArgumentNullException(nameof(effectiveSourceBoundary));
        CreatedAtUtc = createdAtUtc.ToUniversalTime(); CreatedByRunId = createdByRunId;
        Origin = origin ?? throw new ArgumentNullException(nameof(origin));
        ParentCheckpointIds = parentCheckpointIds.IsDefault ? [] : parentCheckpointIds;
        CreationKind = creationKind;
    }

    // The shared materialization pipeline accepts collections, but a checkpoint always owns exactly one Source.
    public SourceCheckpoint(CheckpointId checkpointId, HistoryConfigId configId, DateTimeOffset createdAtUtc,
        RunId? createdByRunId, HistoryProvenance origin, IEnumerable<CheckpointSource> sources,
        IEnumerable<CheckpointId>? parentCheckpointIds = null, CheckpointCreationKind creationKind = CheckpointCreationKind.Capture)
        : this(checkpointId, configId, sources.Single(), createdAtUtc, createdByRunId, origin,
            DomainCollections.Freeze(parentCheckpointIds), creationKind) { }

    private SourceCheckpoint(CheckpointId id, HistoryConfigId config, CheckpointSource source, DateTimeOffset time,
        RunId? run, HistoryProvenance origin, ImmutableArray<CheckpointId> parents, CheckpointCreationKind kind)
        : this(id, config, source.SourceId, source.VersionId ?? throw new ArgumentException("A Source checkpoint requires a Version."),
            source.SourceDescriptorSnapshot, source.EffectiveSourceBoundary, time, run, origin, parents, kind) { }

    public CheckpointId CheckpointId { get; }
    public HistoryConfigId ConfigId { get; }
    public SourceId SourceId { get; }
    public VersionId VersionId { get; }
    public SourceDescriptorSnapshot SourceDescriptorSnapshot { get; }
    public EffectiveSourceBoundarySnapshot EffectiveSourceBoundary { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public RunId? CreatedByRunId { get; }
    public HistoryProvenance Origin { get; }
    public ImmutableArray<CheckpointId> ParentCheckpointIds { get; }
    public CheckpointCreationKind CreationKind { get; }
    [JsonIgnore] public string EffectiveSourceBoundaryFingerprint => EffectiveSourceBoundary.Fingerprint;
    [JsonIgnore] public bool IsStructurallyComplete => VersionId.Value != Guid.Empty;
    [JsonIgnore] public ImmutableArray<CheckpointSource> Sources =>
        [new(SourceId, SourceDescriptorSnapshot, VersionId, CheckpointSourceDisposition.CarriedForward, EffectiveSourceBoundary)];
}


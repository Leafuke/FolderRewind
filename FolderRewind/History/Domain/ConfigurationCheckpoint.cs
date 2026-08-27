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

public sealed record CheckpointSource(
    SourceId SourceId,
    SourceDescriptorSnapshot SourceDescriptorSnapshot,
    VersionId? VersionId,
    CheckpointSourceDisposition Disposition);

public sealed record ConfigurationCheckpoint
{
    public ConfigurationCheckpoint(
        CheckpointId checkpointId,
        HistoryConfigId configId,
        DateTimeOffset createdAtUtc,
        RunId? createdByRunId,
        HistoryProvenance origin,
        IEnumerable<CheckpointSource> sources)
        : this(
            checkpointId,
            configId,
            createdAtUtc,
            createdByRunId,
            origin,
            DomainCollections.Freeze(sources))
    {
    }

    [JsonConstructor]
    public ConfigurationCheckpoint(
        CheckpointId checkpointId,
        HistoryConfigId configId,
        DateTimeOffset createdAtUtc,
        RunId? createdByRunId,
        HistoryProvenance origin,
        ImmutableArray<CheckpointSource> sources)
    {
        CheckpointId = checkpointId;
        ConfigId = configId;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        CreatedByRunId = createdByRunId;
        Origin = origin ?? throw new ArgumentNullException(nameof(origin));
        Sources = sources.IsDefault ? ImmutableArray<CheckpointSource>.Empty : sources;
    }

    public CheckpointId CheckpointId { get; }
    public HistoryConfigId ConfigId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public RunId? CreatedByRunId { get; }
    public HistoryProvenance Origin { get; }
    public ImmutableArray<CheckpointSource> Sources { get; }
    public bool IsComplete => Sources.All(source => source.VersionId is not null);
}

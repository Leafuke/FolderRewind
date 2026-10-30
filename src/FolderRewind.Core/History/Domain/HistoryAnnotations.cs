using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace FolderRewind.History.Domain;

public enum HistoryAnnotationTargetKind
{
    Version = 0,
    Checkpoint = 1,
    Run = 2
}

public enum HistoryAnnotationKind
{
    Comment = 0,
    Pin = 1,
    Suppression = 2,
    RunImportant = 3
}

public readonly record struct HistoryAnnotationTarget
{
    [JsonConstructor]
    public HistoryAnnotationTarget(HistoryAnnotationTargetKind kind, Guid targetId)
    {
        Kind = kind;
        TargetId = HistoryGuidId.Require(targetId, nameof(targetId));
    }

    public HistoryAnnotationTargetKind Kind { get; }
    public Guid TargetId { get; }
}

public sealed record HistoryAnnotationUpdate
{
    public HistoryAnnotationUpdate(
        AnnotationUpdateId updateId,
        HistoryAnnotationTarget target,
        HistoryAnnotationKind annotationKind,
        IEnumerable<AnnotationUpdateId>? parentUpdateIds,
        string value,
        DateTimeOffset createdAtUtc)
        : this(
            updateId,
            target,
            annotationKind,
            DomainCollections.Freeze(parentUpdateIds),
            value,
            createdAtUtc)
    {
    }

    [JsonConstructor]
    public HistoryAnnotationUpdate(
        AnnotationUpdateId updateId,
        HistoryAnnotationTarget target,
        HistoryAnnotationKind annotationKind,
        ImmutableArray<AnnotationUpdateId> parentUpdateIds,
        string value,
        DateTimeOffset createdAtUtc)
    {
        UpdateId = updateId;
        Target = target;
        AnnotationKind = annotationKind;
        ParentUpdateIds = parentUpdateIds.IsDefault ? ImmutableArray<AnnotationUpdateId>.Empty : parentUpdateIds;
        Value = value ?? string.Empty;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
    }

    public AnnotationUpdateId UpdateId { get; }
    public HistoryAnnotationTarget Target { get; }
    public HistoryAnnotationKind AnnotationKind { get; }
    public ImmutableArray<AnnotationUpdateId> ParentUpdateIds { get; }
    public string Value { get; }
    public DateTimeOffset CreatedAtUtc { get; }
}

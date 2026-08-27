using FolderRewind.History.Domain;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace FolderRewind.History.LocalState;

public enum WorkspaceBaselineRelation
{
    Exact = 0,
    Derived = 1,
    Unknown = 2
}

public sealed record WorkspaceSourceBaseline(
    SourceId SourceId,
    VersionId? BaseVersionId,
    WorkspaceBaselineRelation Relation);

public sealed record HistoryWorkspace
{
    public const int CurrentFormatVersion = 1;

    public HistoryWorkspace(
        HistoryConfigId configId,
        long stateRevision,
        BranchId? activeBranchId,
        BranchUpdateId? activeBranchUpdateId,
        IEnumerable<WorkspaceSourceBaseline>? sourceBaselines)
        : this(
            configId,
            stateRevision,
            activeBranchId,
            activeBranchUpdateId,
            sourceBaselines is null ? [] : [.. sourceBaselines])
    {
    }

    [JsonConstructor]
    public HistoryWorkspace(
        HistoryConfigId configId,
        long stateRevision,
        BranchId? activeBranchId,
        BranchUpdateId? activeBranchUpdateId,
        ImmutableArray<WorkspaceSourceBaseline> sourceBaselines)
    {
        if (stateRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stateRevision));
        }

        if ((activeBranchId is null) != (activeBranchUpdateId is null))
        {
            throw new ArgumentException("Active Branch identity and update identity must be present together.");
        }

        ConfigId = configId;
        StateRevision = stateRevision;
        ActiveBranchId = activeBranchId;
        ActiveBranchUpdateId = activeBranchUpdateId;
        SourceBaselines = sourceBaselines.IsDefault ? [] : sourceBaselines;
    }

    public int FormatVersion => CurrentFormatVersion;
    public HistoryConfigId ConfigId { get; }
    public long StateRevision { get; }
    public BranchId? ActiveBranchId { get; }
    public BranchUpdateId? ActiveBranchUpdateId { get; }
    public ImmutableArray<WorkspaceSourceBaseline> SourceBaselines { get; }
}

public enum DeviceLocalStateStatus
{
    Valid = 0,
    Missing = 1,
    Corrupt = 2,
    Inaccessible = 3
}

public sealed record DeviceLocalStateLoadResult<T>(
    DeviceLocalStateStatus Status,
    T? Value,
    string Diagnostic)
    where T : class;

public sealed class DeviceLocalStateConflictException(string message) : Exception(message);

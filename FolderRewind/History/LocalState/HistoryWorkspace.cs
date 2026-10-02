using FolderRewind.History.Domain;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Serialization;

namespace FolderRewind.History.LocalState;

public enum WorkspaceBaselineRelation { Exact = 0, Derived = 1, Unknown = 2 }

public sealed record WorkspaceSourceBaseline(SourceId SourceId, VersionId? BaseVersionId,
    WorkspaceBaselineRelation Relation, BranchId? ActiveBranchId = null,
    BranchUpdateId? ActiveBranchUpdateId = null, CheckpointId? CheckpointAncestryAnchorId = null);

/// <summary>Device-local state. Each Source owns its Branch and continuation anchor independently.</summary>
public sealed record HistoryWorkspace
{
    public const int CurrentFormatVersion = 3;

    public HistoryWorkspace(HistoryConfigId configId, long stateRevision, IEnumerable<WorkspaceSourceBaseline>? sourceBaselines)
        : this(configId, stateRevision, sourceBaselines is null ? [] : [.. sourceBaselines]) { }

    [JsonConstructor]
    public HistoryWorkspace(HistoryConfigId configId, long stateRevision, ImmutableArray<WorkspaceSourceBaseline> sourceBaselines)
    {
        if (stateRevision < 0) throw new ArgumentOutOfRangeException(nameof(stateRevision));
        ConfigId = configId; StateRevision = stateRevision;
        SourceBaselines = sourceBaselines.IsDefault ? [] : sourceBaselines;
        if (SourceBaselines.Select(s => s.SourceId).Distinct().Count() != SourceBaselines.Length)
            throw new ArgumentException("Workspace Source identities must be unique.");
        if (SourceBaselines.Any(s => (s.ActiveBranchId is null) != (s.ActiveBranchUpdateId is null)))
            throw new ArgumentException("Active Branch and update identities must be present together for each Source.");
    }

    public int FormatVersion => CurrentFormatVersion;
    public HistoryConfigId ConfigId { get; }
    public long StateRevision { get; }
    public ImmutableArray<WorkspaceSourceBaseline> SourceBaselines { get; }
    public WorkspaceSourceBaseline GetSourceState(SourceId sourceId) => SourceBaselines.FirstOrDefault(s => s.SourceId == sourceId)
        ?? new(sourceId, null, WorkspaceBaselineRelation.Unknown);

    public HistoryWorkspace WithSourceStates(IEnumerable<WorkspaceSourceBaseline> updates)
    {
        var map = SourceBaselines.ToDictionary(s => s.SourceId);
        foreach (var state in updates) map[state.SourceId] = state;
        return new(ConfigId, checked(StateRevision + 1), map.Values.OrderBy(s => s.SourceId.ToString(), StringComparer.Ordinal));
    }

    public HistoryWorkspace WithContentBaselines(IEnumerable<WorkspaceSourceBaseline> baselines)
        => WithSourceStates(baselines.Select(s => GetSourceState(s.SourceId) with { BaseVersionId = s.BaseVersionId, Relation = s.Relation }));

    public static bool StateEquals(HistoryWorkspace left, HistoryWorkspace right) => left.ConfigId == right.ConfigId
        && left.StateRevision == right.StateRevision && left.SourceBaselines.OrderBy(s => s.SourceId.ToString(), StringComparer.Ordinal)
            .SequenceEqual(right.SourceBaselines.OrderBy(s => s.SourceId.ToString(), StringComparer.Ordinal));
}

public enum DeviceLocalStateStatus { Valid = 0, Missing = 1, Corrupt = 2, Inaccessible = 3 }
public sealed record DeviceLocalStateLoadResult<T>(DeviceLocalStateStatus Status, T? Value, string Diagnostic) where T : class;
public sealed class DeviceLocalStateConflictException(string message) : Exception(message);

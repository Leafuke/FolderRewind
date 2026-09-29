using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace FolderRewind.History.Domain;

public enum BackupInvocationKind
{
    Manual = 0,
    Automatic = 1,
    Remote = 2,
    PluginHotkey = 3,
    Internal = 4,
    Migration = 5,
    Recovery = 6
}

public enum BackupRunOutcome
{
    Completed = 0,
    Partial = 1,
    Failed = 2,
    NoChange = 3
}

public enum BackupRunSourceOutcome
{
    Captured = 0,
    Reused = 1,
    Failed = 2,
    Unavailable = 3,
    CarriedForward = 4
}

[method: JsonConstructor]
public sealed record BackupRunSourceResult(
    SourceId SourceId,
    BackupRunSourceOutcome Outcome,
    VersionId? VersionId,
    ImmutableArray<HistoryDiagnostic> Diagnostics)
{
    public BackupRunSourceResult(
        SourceId sourceId,
        BackupRunSourceOutcome outcome,
        VersionId? versionId,
        IEnumerable<HistoryDiagnostic>? diagnostics)
        : this(sourceId, outcome, versionId, DomainCollections.Freeze(diagnostics))
    {
    }
}

public sealed record BackupRun
{
    public BackupRun(
        RunId runId,
        HistoryConfigId configId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        BackupInvocationKind invocation,
        BackupRunOutcome outcome,
        IEnumerable<BackupRunSourceResult>? sourceResults,
        CheckpointId? resultCheckpointId,
        IEnumerable<HistoryDiagnostic>? diagnostics)
        : this(
            runId,
            configId,
            startedAtUtc,
            completedAtUtc,
            invocation,
            outcome,
            DomainCollections.Freeze(sourceResults),
            resultCheckpointId,
            DomainCollections.Freeze(diagnostics))
    {
    }

    [JsonConstructor]
    public BackupRun(
        RunId runId,
        HistoryConfigId configId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        BackupInvocationKind invocation,
        BackupRunOutcome outcome,
        ImmutableArray<BackupRunSourceResult> sourceResults,
        CheckpointId? resultCheckpointId,
        ImmutableArray<HistoryDiagnostic> diagnostics)
    {
        RunId = runId;
        ConfigId = configId;
        StartedAtUtc = startedAtUtc.ToUniversalTime();
        CompletedAtUtc = completedAtUtc.ToUniversalTime();
        Invocation = invocation;
        Outcome = outcome;
        SourceResults = sourceResults.IsDefault ? ImmutableArray<BackupRunSourceResult>.Empty : sourceResults;
        ResultCheckpointId = resultCheckpointId;
        Diagnostics = diagnostics.IsDefault ? ImmutableArray<HistoryDiagnostic>.Empty : diagnostics;
    }

    public RunId RunId { get; }
    public HistoryConfigId ConfigId { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public BackupInvocationKind Invocation { get; }
    public BackupRunOutcome Outcome { get; }
    public ImmutableArray<BackupRunSourceResult> SourceResults { get; }
    public CheckpointId? ResultCheckpointId { get; }
    public ImmutableArray<HistoryDiagnostic> Diagnostics { get; }
}

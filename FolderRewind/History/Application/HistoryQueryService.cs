using FolderRewind.History.Domain;
using FolderRewind.History.Index;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public enum HistoryTimelineEntryKind
{
    Version = 0,
    Checkpoint = 1,
    Run = 2
}

public sealed record HistoryTimelineEntry(
    HistoryTimelineEntryKind Kind,
    string Id,
    DateTimeOffset OccurredAtUtc,
    object Fact);

public sealed class HistoryQueryService
{
    private readonly HistoryIndex _index;

    public HistoryQueryService(HistoryIndex index)
        => _index = index ?? throw new ArgumentNullException(nameof(index));

    public Task<IReadOnlyList<SourceVersion>> GetVersionsForSourceAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default)
        => _index.GetVersionsForSourceAsync(sourceId, cancellationToken);

    public Task<ConfigurationCheckpoint?> GetCheckpointAsync(
        CheckpointId checkpointId,
        CancellationToken cancellationToken = default)
        => _index.GetCheckpointAsync(checkpointId, cancellationToken);

    public Task<IReadOnlyList<BranchUpdate>> GetBranchTipsAsync(
        BranchId branchId,
        CancellationToken cancellationToken = default)
        => _index.GetBranchTipsWithFactsAsync(branchId, cancellationToken);

    public Task<IReadOnlyList<BackupRun>> GetRunsAsync(
        CancellationToken cancellationToken = default)
        => _index.GetRunsAsync(cancellationToken);

    public Task<IReadOnlyList<VersionRepresentation>> GetRepresentationsAsync(
        VersionId versionId,
        CancellationToken cancellationToken = default)
        => _index.GetRepresentationsAsync(versionId, cancellationToken);

    public Task<IReadOnlyList<VersionRepresentation>> GetAllRepresentationsAsync(
        CancellationToken cancellationToken = default)
        => _index.GetAllRepresentationsAsync(cancellationToken);

    public async Task<IReadOnlyList<HistoryTimelineEntry>> GetTimelineAsync(
        CancellationToken cancellationToken = default)
    {
        var versionsTask = _index.GetAllVersionsAsync(cancellationToken);
        var checkpointsTask = _index.GetAllCheckpointsAsync(cancellationToken);
        var runsTask = _index.GetRunsAsync(cancellationToken);
        await Task.WhenAll(versionsTask, checkpointsTask, runsTask).ConfigureAwait(false);

        return versionsTask.Result
            .Select(item => new HistoryTimelineEntry(
                HistoryTimelineEntryKind.Version,
                item.VersionId.ToString(),
                item.CreatedAtUtc,
                item))
            .Concat(checkpointsTask.Result.Select(item => new HistoryTimelineEntry(
                HistoryTimelineEntryKind.Checkpoint,
                item.CheckpointId.ToString(),
                item.CreatedAtUtc,
                item)))
            .Concat(runsTask.Result.Select(item => new HistoryTimelineEntry(
                HistoryTimelineEntryKind.Run,
                item.RunId.ToString(),
                item.CompletedAtUtc,
                item)))
            .OrderByDescending(item => item.OccurredAtUtc)
            .ThenByDescending(item => item.Id, StringComparer.Ordinal)
            .ToImmutableArray();
    }
}

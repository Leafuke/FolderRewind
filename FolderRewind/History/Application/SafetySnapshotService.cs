using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed class SafetySnapshotService
{
    private readonly HistoryRuntime _history;
    private readonly HistoryPackCodec _codec;

    public SafetySnapshotService(HistoryRuntime history, HistoryPackCodec? codec = null)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _codec = codec ?? new HistoryPackCodec();
    }

    public Task<IReadOnlyList<SafetySnapshotProjection>> QueryAsync(
        bool activeOnly = true,
        CancellationToken cancellationToken = default)
        => _history.Query.GetSafetySnapshotProjectionsAsync(activeOnly, cancellationToken);

    public async Task<bool> ReleaseAsync(
        SafetySnapshotId snapshotId,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _history.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await _history.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = await _history.Query.GetSafetySnapshotsAsync(cancellationToken).ConfigureAwait(false);
        if (snapshots.All(item => item.SnapshotId != snapshotId))
            throw new InvalidOperationException("SafetySnapshot does not exist.");
        var releases = await _history.Query.GetSafetySnapshotReleasesAsync(snapshotId, cancellationToken)
            .ConfigureAwait(false);
        if (releases.Count > 0) return false;

        _ = await HistoryCommandCommitter.CommitInsideGateAsync(
            _history,
            _codec,
            [new SafetySnapshotRelease(
                SafetySnapshotReleaseId.New(),
                snapshotId,
                DateTimeOffset.UtcNow)],
            currentWorkspace: null,
            updatedWorkspace: null,
            cancellationToken).ConfigureAwait(false);
        return true;
    }
}

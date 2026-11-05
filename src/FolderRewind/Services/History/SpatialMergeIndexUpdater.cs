using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace FolderRewind.Services;

internal static class SpatialMergeIndexUpdater
{
    internal static bool CanApply(MergeChangeSnapshot indexed, MergeDecisionChange? change, MergeSession? current)
        => change is not null && change.Matches(current) && !change.Ids.IsDefaultOrEmpty
            && change.Before.State is MergeSessionState.Ready or MergeSessionState.Resolving
            && change.After.State is MergeSessionState.Ready or MergeSessionState.Resolving
            && indexed.SessionId == change.Before.Id && indexed.PlanRevision == change.Before.Plan.Revision
            && indexed.Revision == change.Before.Revision && change.After.Id == change.Before.Id
            && change.After.Plan.Revision == change.Before.Plan.Revision && change.After.Revision == change.Before.Revision + 1;

    // The VM has revoked/drained readers before entering this transaction. Rows, selection
    // and the owner's revision advance together; failed validation leaves the old index intact.
    internal static void Apply(MergeSessionStore store, SpatialMergeIndexBuild published, MergeDecisionChange change,
        Func<MergeChangeRecord, SpatialWorkspaceRow> project, CancellationToken token = default)
    {
        var indexed = published.Snapshot;
        if (!CanApply(indexed, change, change.After)) throw new MergeChangeSnapshotStaleException();
        var rows = store.ReadChangesSnapshot(change.After, change.Ids, token);
        published.Index.UpdateMergeRows(rows.Select(row =>
        {
            token.ThrowIfCancellationRequested();
            var projected = project(row);
            if (!StringComparer.Ordinal.Equals(projected.Id, row.Conflict.Id))
                throw new InvalidDataException("Merge spatial projection changed its identity.");
            return projected;
        }),
            indexed.Total, () => store.RequireChangesSnapshotCurrent(change.After, token), token);
        // No cancellation point after commit: this identity must describe the committed rows,
        // even if a newer operation arrived while the UI continuation was queued.
        published.Snapshot = indexed with { Revision = change.After.Revision };
    }
}

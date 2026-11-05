using FolderRewind.History.Merge;
using FolderRewind.Services;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace FolderRewind.History.LocalState;

internal sealed record MergeChangeRecord(MergeConflict Conflict, MergeResolution? Resolution, bool Automatic);
internal sealed record MergeChangeSnapshot(Guid SessionId, Guid PlanRevision, long Revision, int Total);

internal sealed class MergeChangeSnapshotStaleException : InvalidOperationException
{
    internal MergeChangeSnapshotStaleException()
        : base("Merge Session changed while its changes were being read.") { }
}

public sealed partial class MergeSessionStore
{
    /// <summary>Reads exactly the requested changes from one fixed, completed plan.</summary>
    internal IReadOnlyList<MergeChangeRecord> ReadChangesSnapshot(MergeSession expected,
        IReadOnlyList<string> ids, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        RequireReviewableSnapshot(expected, token);
        var requested = new HashSet<string>(StringComparer.Ordinal);
        var count = ids.Count;
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (ids[i] is not { } id) throw new ArgumentException("Merge change identities cannot be null.", nameof(ids));
            requested.Add(id);
        }
        token.ThrowIfCancellationRequested();
        var rows = new List<MergeChangeRecord>(requested.Count);
        using (var db = OpenSnapshotRead())
        using (var cancellation = new SqliteReadCancellation(db, token))
        {
            try
            {
                using var transaction = db.BeginTransaction(deferred: true);
                RequireSnapshotRevision(db, expected, transaction);
                if (requested.Count > 0)
                {
                    using var query = Command(db, """
                        SELECT id,data,resolution,
                            ((COALESCE(json_extract(data,'$.semanticSubject.isAutomatic'),0) AND json_extract(resolution,'$.choice')=5) OR COALESCE(json_extract(data,'$.automaticDisplayOnly'),0))
                        FROM conflicts WHERE session=$s AND revision=$p AND id IN (SELECT value FROM json_each($ids)) ORDER BY id
                        """, ("$s", expected.Id.ToString()), ("$p", expected.Plan.Revision.ToString()), ("$ids", Encode(requested)));
                    query.Transaction = transaction;
                    using var reader = query.ExecuteReader();
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        var conflict = Decode<MergeConflict>(reader.GetString(1));
                        if (!StringComparer.Ordinal.Equals(conflict.Id, reader.GetString(0)))
                            throw new InvalidDataException("Merge change identity differs from its stored key.");
                        rows.Add(new(conflict, reader.IsDBNull(2) ? null : Decode<MergeResolution>(reader.GetString(2)), reader.GetBoolean(3)));
                    }
                }
                token.ThrowIfCancellationRequested();
                if (rows.Count != requested.Count) throw new KeyNotFoundException("A requested Merge change is missing from the fixed plan.");
                transaction.Commit();
            }
            catch (SqliteException error) when (error.SqliteErrorCode == 9 && token.IsCancellationRequested)
            {
                throw new OperationCanceledException("Merge change snapshot read was canceled.", error, token);
            }
        }
        RequireChangesSnapshotCurrent(expected, token);
        return rows;
    }

    /// <summary>Fresh validation for callers about to publish a separately prepared index update.</summary>
    internal void RequireChangesSnapshotCurrent(MergeSession expected, CancellationToken token = default)
    {
        RequireReviewableSnapshot(expected, token);
        using (var db = OpenSnapshotRead())
        using (var cancellation = new SqliteReadCancellation(db, token))
        {
            try { RequireSnapshotRevision(db, expected); }
            catch (SqliteException error) when (error.SqliteErrorCode == 9 && token.IsCancellationRequested)
            {
                throw new OperationCanceledException("Merge change snapshot validation was canceled.", error, token);
            }
        }
        token.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Streams a fixed, completed plan in bounded primary-key order, without display sorting or
    /// per-page summaries. Consumed batches are provisional until this method returns: callers
    /// must discard their candidate if cancellation, a stale revision or another failure occurs.
    /// </summary>
    internal MergeChangeSnapshot ExportChangesSnapshot(MergeSession expected,
        Action<IReadOnlyList<MergeChangeRecord>> consume, CancellationToken token = default, Action<int>? started = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(consume);
        token.ThrowIfCancellationRequested();
        if (expected.State is not (MergeSessionState.Resolving or MergeSessionState.Ready))
            throw new InvalidOperationException("Merge changes require a completed, reviewable Session.");

        MergeChangeSnapshot snapshot;
        using (var db = OpenSnapshotRead())
        using (var cancellation = new SqliteReadCancellation(db, token))
        {
            try
            {
                using var transaction = db.BeginTransaction(deferred: true);
                RequireSnapshotRevision(db, expected, transaction);
                using var count = Command(db, "SELECT COUNT(*) FROM conflicts WHERE session=$s AND revision=$p",
                    ("$s", expected.Id.ToString()), ("$p", expected.Plan.Revision.ToString()));
                count.Transaction = transaction;
                snapshot = new(expected.Id, expected.Plan.Revision, expected.Revision, Convert.ToInt32(count.ExecuteScalar()));
                started?.Invoke(snapshot.Total);
                token.ThrowIfCancellationRequested();

                using var query = Command(db, """
                    SELECT data,resolution,
                        ((COALESCE(json_extract(data,'$.semanticSubject.isAutomatic'),0) AND json_extract(resolution,'$.choice')=5) OR COALESCE(json_extract(data,'$.automaticDisplayOnly'),0))
                    FROM conflicts WHERE session=$s AND revision=$p ORDER BY id
                    """, ("$s", expected.Id.ToString()), ("$p", expected.Plan.Revision.ToString()));
                query.Transaction = transaction;
                var batch = new List<MergeChangeRecord>(1000);
                var emitted = 0;
                using (var reader = query.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        batch.Add(new(Decode<MergeConflict>(reader.GetString(0)),
                            reader.IsDBNull(1) ? null : Decode<MergeResolution>(reader.GetString(1)), reader.GetBoolean(2)));
                        if (batch.Count != 1000) continue;
                        consume(batch.ToArray());
                        emitted += batch.Count;
                        batch.Clear();
                        token.ThrowIfCancellationRequested();
                    }
                }
                if (batch.Count > 0)
                {
                    consume(batch.ToArray());
                    emitted += batch.Count;
                }
                token.ThrowIfCancellationRequested();
                if (emitted != snapshot.Total) throw new InvalidOperationException("Merge change snapshot count is inconsistent.");
                transaction.Commit();
            }
            catch (SqliteException error) when (error.SqliteErrorCode == 9 && token.IsCancellationRequested)
            {
                throw new OperationCanceledException("Merge change snapshot read was canceled.", error, token);
            }
        }

        // A validation inside the read transaction would only see its old snapshot. Reopen
        // after releasing it so a resolution, replan or state change cannot be published.
        RequireChangesSnapshotCurrent(expected, token);
        return snapshot;
    }

    private static void RequireReviewableSnapshot(MergeSession expected, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(expected);
        token.ThrowIfCancellationRequested();
        if (expected.State is not (MergeSessionState.Resolving or MergeSessionState.Ready))
            throw new InvalidOperationException("Merge changes require a completed, reviewable Session.");
    }

    private SqliteConnection OpenSnapshotRead()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = _path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try { db.Open(); return db; }
        catch { db.Dispose(); throw; }
    }

    private static void RequireSnapshotRevision(SqliteConnection db, MergeSession expected, SqliteTransaction? transaction = null)
    {
        using var query = Command(db, "SELECT revision,state,json_extract(data,'$.plan.revision') FROM sessions WHERE id=$s",
            ("$s", expected.Id.ToString()));
        query.Transaction = transaction;
        using var reader = query.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(0) != expected.Revision || reader.GetInt32(1) != (int)expected.State
            || !Guid.TryParse(reader.GetString(2), out var plan) || plan != expected.Plan.Revision)
            throw new MergeChangeSnapshotStaleException();
    }
}

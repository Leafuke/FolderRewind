using FolderRewind.History.Merge;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.History.LocalState;

public sealed partial class MergeSessionStore
{
    public IReadOnlyList<(string Id, string Path, bool Resolved)> ConflictIndex(MergeSession session, string search = "")
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT id,COALESCE(json_extract(data,'$.subject.paths[0]'),''),resolution IS NOT NULL FROM conflicts
            WHERE session=$s AND revision=$r AND ($q='' OR EXISTS(SELECT 1 FROM json_each(data,'$.subject.paths') WHERE instr(lower(value),lower($q))>0))
            """, ("$s", session.Id.ToString()), ("$r", session.Plan.Revision.ToString()), ("$q", search));
        using var read = cmd.ExecuteReader(); var rows = new List<(string, string, bool)>();
        while (read.Read()) rows.Add((read.GetString(0), read.GetString(1), read.GetBoolean(2)));
        return rows;
    }
    public IReadOnlyDictionary<string, (MergeConflict Conflict, MergeResolution? Resolution)> GetConflicts(MergeSession session, IEnumerable<string> ids)
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT id,data,resolution FROM conflicts WHERE session=$s AND revision=$r AND id IN (SELECT value FROM json_each($ids))",
            ("$s", session.Id.ToString()), ("$r", session.Plan.Revision.ToString()), ("$ids", Encode(ids.ToArray())));
        using var read = cmd.ExecuteReader(); var rows = new Dictionary<string, (MergeConflict, MergeResolution?)>();
        while (read.Read()) rows.Add(read.GetString(0), (Decode<MergeConflict>(read.GetString(1)), read.IsDBNull(2) ? null : Decode<MergeResolution>(read.GetString(2))));
        return rows;
    }
    public (int Total, int Unresolved, int Resolved) ConflictCounts(MergeSession session)
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT COUNT(*), COALESCE(SUM(resolution IS NULL),0) FROM conflicts WHERE session=$s AND revision=$r",
            ("$s", session.Id.ToString()), ("$r", session.Plan.Revision.ToString()));
        using var read = cmd.ExecuteReader(); read.Read();
        var total = read.GetInt32(0); var unresolved = read.GetInt32(1); return (total, unresolved, total - unresolved);
    }
    public (MergeConflict Conflict, MergeResolution? Resolution) GetConflict(MergeSession session, string id)
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT data,resolution FROM conflicts WHERE session=$s AND revision=$r AND id=$id",
            ("$s", session.Id.ToString()), ("$r", session.Plan.Revision.ToString()), ("$id", id));
        using var read = cmd.ExecuteReader();
        if (!read.Read()) throw new System.IO.InvalidDataException("Conflict is missing.");
        return (Decode<MergeConflict>(read.GetString(0)), read.IsDBNull(1) ? null : Decode<MergeResolution>(read.GetString(1)));
    }
    public IReadOnlyList<(MergeConflict Conflict, MergeResolution? Resolution)> QueryConflicts(MergeSession session,
        string search = "", bool? resolved = null, int offset = 0, int count = 100)
    {
        if (offset < 0 || count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(count));
        using var db = Open();
        using var cmd = Command(db, """
            SELECT data,resolution FROM conflicts WHERE session=$s AND revision=$r
            AND ($q='' OR EXISTS(SELECT 1 FROM json_each(data,'$.subject.paths') WHERE instr(lower(value),lower($q))>0))
            AND ($resolved IS NULL OR (resolution IS NOT NULL)=$resolved)
            ORDER BY COALESCE(json_extract(data,'$.subject.paths[0]'),''),id LIMIT $n OFFSET $o
            """, ("$s", session.Id.ToString()), ("$r", session.Plan.Revision.ToString()), ("$q", search),
            ("$resolved", resolved is null ? null : resolved.Value ? 1 : 0), ("$n", count), ("$o", offset));
        using var read = cmd.ExecuteReader(); var rows = new List<(MergeConflict, MergeResolution?)>();
        while (read.Read()) rows.Add((Decode<MergeConflict>(read.GetString(0)), read.IsDBNull(1) ? null : Decode<MergeResolution>(read.GetString(1))));
        return rows;
    }
}

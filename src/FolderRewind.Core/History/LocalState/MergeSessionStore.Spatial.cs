using FolderRewind.History.Merge;
using FolderRewind.Plugin.Abstractions;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace FolderRewind.History.LocalState;

public sealed partial class MergeSessionStore
{
    private static void RequireSpatialSchema(SqliteConnection db)
    {
        using var check = Command(db, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('spatial_subjects','spatial_subjects_window')");
        if (Convert.ToInt64(check.ExecuteScalar()) != 2) throw new InvalidDataException("Spatial Merge index is missing; preserve the session.");
    }
    private static void WriteSpatialLocations(SqliteConnection db, MergeSession session, MergeConflict conflict)
    {
        if (conflict.SemanticSubject is not { } subject) return;
        using var clear = Command(db, "DELETE FROM spatial_subjects WHERE session=$s AND plan=$p AND id=$id",
            ("$s", session.Id.ToString()), ("$p", session.Plan.Revision.ToString()), ("$id", conflict.Id)); clear.ExecuteNonQuery();
        for (var i = 0; i < subject.SpatialLocations.Count; i++)
        {
            var p = subject.SpatialLocations[i];
            using var write = Command(db, "INSERT INTO spatial_subjects VALUES($s,$p,$id,$n,$w,$l,$x,$y,$xx,$yy)",
                ("$s", session.Id.ToString()), ("$p", session.Plan.Revision.ToString()), ("$id", conflict.Id), ("$n", i),
                ("$w", p.WorldId), ("$l", p.LayerId), ("$x", p.Bounds?.MinX), ("$y", p.Bounds?.MinY), ("$xx", p.Bounds?.MaxX), ("$yy", p.Bounds?.MaxY));
            write.ExecuteNonQuery();
        }
    }
    public IReadOnlyList<(MergeConflict Conflict, MergeResolution? Resolution)> SpatialChanges(MergeSession session,
        string world, string? layer, SpatialPreviewBounds? bounds, int offset, int count, bool unresolvedOnly = false)
    {
        if (offset < 0 || count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(count));
        using var db = Open();
        using var query = Command(db, """
            SELECT c.data,c.resolution FROM conflicts c WHERE c.session=$s AND c.revision=$p
              AND ($u=0 OR c.resolution IS NULL) AND EXISTS (
                SELECT 1 FROM spatial_subjects m WHERE m.session=c.session AND m.plan=c.revision AND m.id=c.id
                AND m.world=$w AND ($l IS NULL OR m.layer IS NULL OR m.layer=$l)
                AND ($x IS NULL OR m.minx IS NULL OR m.minx<$xx AND m.maxx>$x AND m.miny<$yy AND m.maxy>$y))
            ORDER BY c.id LIMIT $n OFFSET $o
            """, ("$s", session.Id.ToString()), ("$p", session.Plan.Revision.ToString()), ("$w", world), ("$l", layer),
            ("$x", bounds?.MinX), ("$y", bounds?.MinY), ("$xx", bounds?.MaxX), ("$yy", bounds?.MaxY), ("$u", unresolvedOnly ? 1 : 0), ("$n", count), ("$o", offset));
        using var read = query.ExecuteReader(); var rows = new List<(MergeConflict, MergeResolution?)>();
        while (read.Read()) rows.Add((Decode<MergeConflict>(read.GetString(0)), read.IsDBNull(1) ? null : Decode<MergeResolution>(read.GetString(1))));
        return rows;
    }
}

using FolderRewind.History.Domain;
using FolderRewind.History.Merge;
using FolderRewind.Plugin.Abstractions;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FolderRewind.History.LocalState;

public sealed partial class MergeSessionStore
{
    private static void RequireSemanticSchema(SqliteConnection db)
    {
        using var check = Command(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('semantic_groups','semantic_paths','semantic_data')");
        if (Convert.ToInt64(check.ExecuteScalar()) != 3) throw new InvalidDataException("Semantic Merge schema is incomplete; preserve session data.");
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name IN ('semantic_paths_group','conflicts_semantic_group','semantic_subject_identity')";
        if (Convert.ToInt64(check.ExecuteScalar()) != 3) throw new InvalidDataException("Semantic Merge indexes are missing.");
    }

    private static void RequirePreparing(SqliteConnection db, MergeSession session)
    {
        using var check = Command(db, "SELECT revision FROM sessions WHERE id=$s AND state=$st",
            ("$s", session.Id.ToString()), ("$st", (int)MergeSessionState.Preparing));
        if (Convert.ToInt64(check.ExecuteScalar() ?? -1L) != session.Revision) throw new InvalidOperationException("Merge analysis revision changed.");
    }

    internal void BeginSemanticSource(MergeSession session, SourceId source)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); RequirePreparing(db, session);
        foreach (var table in new[] { "semantic_groups", "semantic_paths", "semantic_data" })
        {
            using var clear = Command(db, $"DELETE FROM {table} WHERE session=$s AND plan=$p AND source=$source",
                ("$s", session.Id.ToString()), ("$p", session.Plan.Revision.ToString()), ("$source", source.ToString()));
            clear.ExecuteNonQuery();
        }
        using (var clear = Command(db, "DELETE FROM conflicts WHERE session=$s AND revision=$p AND json_extract(data,'$.subject.sourceId')=$source AND json_extract(data,'$.semanticSubject') IS NOT NULL",
            ("$s", session.Id.ToString()), ("$p", session.Plan.Revision.ToString()), ("$source", source.ToString()))) clear.ExecuteNonQuery();
        tx.Commit();
    }

    internal void AddSemanticGroup(MergeSession session, SourceId source, MergeOutputGroup group)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); RequirePreparing(db, session);
        using var write = Command(db, "INSERT INTO semantic_groups VALUES($s,$p,$source,$id,$d)",
            ("$s", session.Id.ToString()), ("$p", session.Plan.Revision.ToString()), ("$source", source.ToString()), ("$id", group.GroupId), ("$d", Encode(group)));
        write.ExecuteNonQuery(); tx.Commit();
    }

    internal IReadOnlyList<MergeOutputGroup> SemanticGroups(MergeSession session, SourceId source)
    {
        using var db = Open(); using var query = Command(db, "SELECT data FROM semantic_groups WHERE session=$s AND plan=$p AND source=$source ORDER BY id",
            ("$s", session.Id.ToString()), ("$p", session.Plan.Revision.ToString()), ("$source", source.ToString()));
        using var reader = query.ExecuteReader(); var groups = new List<MergeOutputGroup>();
        while (reader.Read()) groups.Add(Decode<MergeOutputGroup>(reader.GetString(0)));
        return groups;
    }

    internal void ClaimSemanticPaths(MergeSession session, SourceId source, string group, IReadOnlyList<string> inputs, IReadOnlyList<string> outputs)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); RequirePreparing(db, session);
        using var exists = Command(db, "SELECT COUNT(*) FROM semantic_groups WHERE session=$s AND plan=$p AND source=$source AND id=$id",
            ("$s", session.Id.ToString()), ("$p", session.Plan.Revision.ToString()), ("$source", source.ToString()), ("$id", group));
        if (Convert.ToInt64(exists.ExecuteScalar()) != 1) throw new InvalidDataException("Merge output group is missing.");
        var inputSet = inputs.ToHashSet(StringComparer.Ordinal); var outputSet = outputs.ToHashSet(StringComparer.Ordinal);
        foreach (var path in inputSet.Concat(outputSet).Distinct(StringComparer.Ordinal))
        {
            using var read = Command(db, "SELECT groupid FROM semantic_paths WHERE session=$s AND plan=$p AND source=$source AND path=$path",
                ("$s", session.Id.ToString()), ("$p", session.Plan.Revision.ToString()), ("$source", source.ToString()), ("$path", path));
            if (read.ExecuteScalar() is string owner && owner != group) throw new InvalidDataException("Merge output groups overlap.");
            using var write = Command(db, """
                INSERT INTO semantic_paths VALUES($s,$p,$source,$path,$g,$i,$o)
                ON CONFLICT(session,plan,source,path) DO UPDATE SET input=MAX(input,$i),output=MAX(output,$o)
                """, ("$s", session.Id.ToString()), ("$p", session.Plan.Revision.ToString()), ("$source", source.ToString()),
                ("$path", path), ("$g", group), ("$i", inputSet.Contains(path) ? 1 : 0), ("$o", outputSet.Contains(path) ? 1 : 0));
            write.ExecuteNonQuery();
        }
        tx.Commit();
    }

    internal IReadOnlyList<(string Path, string Group, bool Input, bool Output)> SemanticPaths(MergeSession session, SourceId source, string? group = null)
    {
        using var db = Open(); using var query = Command(db,
            "SELECT path,groupid,input,output FROM semantic_paths WHERE session=$s AND plan=$p AND source=$source AND ($g IS NULL OR groupid=$g) ORDER BY path",
            ("$s", session.Id.ToString()), ("$p", session.Plan.Revision.ToString()), ("$source", source.ToString()), ("$g", group));
        using var reader = query.ExecuteReader(); var paths = new List<(string, string, bool, bool)>();
        while (reader.Read()) paths.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2) != 0, reader.GetInt32(3) != 0));
        return paths;
    }

    internal void AddSemanticConflicts(MergeSession session, IReadOnlyList<MergeConflict> conflicts)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); RequirePreparing(db, session);
        foreach (var conflict in conflicts)
        {
            var subject = conflict.SemanticSubject ?? throw new InvalidDataException("Semantic subject is missing.");
            MergeResolution? resolution = subject.IsAutomatic
                ? new(session.Plan.Revision, conflict.Id, conflict.InputSignature, MergeResolutionChoice.Automatic) : null;
            if (!subject.IsAutomatic || subject.CanOverrideAutomatic)
            {
                using var old = Command(db, "SELECT resolution FROM conflicts WHERE session=$s AND revision<>$p AND json_extract(data,'$.inputSignature')=$sig AND resolution IS NOT NULL ORDER BY rowid DESC LIMIT 1",
                    ("$s", session.Id.ToString()), ("$p", session.Plan.Revision.ToString()), ("$sig", conflict.InputSignature));
                if (old.ExecuteScalar() is string prior)
                {
                    var candidate = Decode<MergeResolution>(prior);
                    if (SemanticChoiceAllowed(subject, candidate.Choice)
                        && (candidate.Choice != MergeResolutionChoice.Manual || candidate.Manual is { } file && ManualIsOwnedAndValid(session, file)))
                        resolution = candidate with { PlanRevision = session.Plan.Revision, ConflictId = conflict.Id };
                }
            }
            WriteSpatialLocations(db, session, conflict);
            using var write = Command(db, "INSERT INTO conflicts VALUES($s,$p,$id,$d,$r)",
                ("$s", session.Id.ToString()), ("$p", session.Plan.Revision.ToString()), ("$id", conflict.Id),
                ("$d", Encode(conflict)), ("$r", resolution is null ? null : Encode(resolution)));
            write.ExecuteNonQuery();
        }
        tx.Commit();
    }

    internal static MergeDecisionKind SemanticChoice(MergeResolutionChoice choice) => choice switch
    {
        MergeResolutionChoice.Ours => MergeDecisionKind.Ours, MergeResolutionChoice.Theirs => MergeDecisionKind.Theirs,
        MergeResolutionChoice.Base => MergeDecisionKind.Base, MergeResolutionChoice.Delete => MergeDecisionKind.Delete,
        MergeResolutionChoice.Manual => MergeDecisionKind.Manual, MergeResolutionChoice.Automatic => MergeDecisionKind.Automatic,
        _ => throw new InvalidDataException("Unknown Merge choice.")
    };
    internal static bool SemanticChoiceAllowed(MergeSubject subject, MergeResolutionChoice choice)
        => !subject.IsBlocked && (subject.IsAutomatic && choice == MergeResolutionChoice.Automatic
            || (!subject.IsAutomatic || subject.CanOverrideAutomatic) && Enum.IsDefined(choice)
                && choice != MergeResolutionChoice.Automatic && subject.AllowedDecisions.Contains(SemanticChoice(choice)));

    internal IReadOnlyList<(MergeConflict Conflict, MergeResolution? Resolution)> SemanticDecisionPage(MergeSession session, SourceId source, string group, string? after, int count)
    {
        if (count is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(count));
        using var db = Open(); using var query = Command(db, """
            SELECT data,resolution FROM conflicts WHERE session=$s AND revision=$p
            AND json_extract(data,'$.subject.sourceId')=$source AND json_extract(data,'$.semanticSubject.groupId')=$g
            AND id>$after ORDER BY id LIMIT $n
            """, ("$s", session.Id.ToString()), ("$p", session.Plan.Revision.ToString()), ("$source", source.ToString()),
            ("$g", group), ("$after", after ?? ""), ("$n", count));
        using var reader = query.ExecuteReader(); var rows = new List<(MergeConflict, MergeResolution?)>();
        while (reader.Read()) rows.Add((Decode<MergeConflict>(reader.GetString(0)), reader.IsDBNull(1) ? null : Decode<MergeResolution>(reader.GetString(1))));
        return rows;
    }

    internal void SaveSemanticData(MergeSession session, SourceId source, string key, string path, long length, string digest)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); RequirePreparing(db, session);
        using var write = Command(db, "INSERT INTO semantic_data VALUES($s,$p,$source,$key,$path,$length,$digest)",
            ("$s", session.Id.ToString()), ("$p", session.Plan.Revision.ToString()), ("$source", source.ToString()),
            ("$key", key), ("$path", path), ("$length", length), ("$digest", digest));
        write.ExecuteNonQuery(); tx.Commit();
    }
    internal MergeFileValue ReadSemanticData(MergeSession session, SourceId source, string key)
    {
        using var db = Open(); using var query = Command(db, "SELECT path,digest,length FROM semantic_data WHERE session=$s AND plan=$p AND source=$source AND key=$key",
            ("$s", session.Id.ToString()), ("$p", session.Plan.Revision.ToString()), ("$source", source.ToString()), ("$key", key));
        using var reader = query.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("Merge plan data is unavailable.");
        return new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
    }
}

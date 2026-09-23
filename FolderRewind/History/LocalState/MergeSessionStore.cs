using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Merge;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using FolderRewind.History.Storage;

namespace FolderRewind.History.LocalState;

public enum MergeSessionState { Preparing, Resolving, Ready, Stale, Applying, Committed, Abandoned }
public enum MergeResolutionChoice { Ours, Theirs, Manual }
public sealed record MergeResolution(Guid PlanRevision, string ConflictId, string InputSignature,
    MergeResolutionChoice Choice, MergeFileValue? Manual = null);
public sealed record MergeSession(Guid Id, long Revision, MergeSessionState State, HistoryMergePlan Plan,
    HistoryTransactionId? ApplyTransactionId = null, PackId? IntendedPackId = null, HistoryWorkspace? ProtectedWorkspace = null);
public sealed record MergeSessionSource(HistoryMergeSourcePlan Plan, MergeTreeManifest Automatic,
    MergeTreeManifest Base, MergeTreeManifest Ours, MergeTreeManifest Theirs);
public sealed record PreparedMergeDescriptor(Guid SessionId, Guid PlanRevision, long ResolutionRevision,
    string ProviderVersion, string PolicyVersion, ImmutableArray<PreparedMergeSource> Sources,
    ConfigurationCheckpoint Checkpoint, ImmutableArray<LocalReplicaCatalogEntry> NewReplicas, byte[] PackBytes)
{
    public PreparedMerge Restore(MergeSession session)
    {
        if (session.Id != SessionId || session.Plan.Revision != PlanRevision || session.Revision != ResolutionRevision
            || session.Plan.ProviderVersion != ProviderVersion || session.Plan.PolicyVersion != PolicyVersion)
            throw new InvalidOperationException("Prepared Merge belongs to another Session revision.");
        var codec = new HistoryPackCodec(); var pack = codec.Decode(PackBytes).Pack;
        var facts = pack.Objects.Select(codec.DeserializeKnown).ToImmutableArray();
        return new(session, Sources, facts, Checkpoint, facts.OfType<BranchUpdate>().Single(), NewReplicas, pack.PackId, pack.TransactionId);
    }
}

public sealed class MergeSessionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    public string Root { get; }
    public MergeSessionStore(string localStateRoot)
    {
        Root = Path.Combine(localStateRoot, "merge-sessions"); _path = Path.Combine(Root, "sessions.db");
    }
    public string SessionDirectory(Guid id) => Path.Combine(Root, id.ToString("N"));
    private SqliteConnection Open()
    {
        if (!File.Exists(_path) && Directory.Exists(Root) && Directory.EnumerateFileSystemEntries(Root).Any())
            throw new InvalidDataException("Merge Session database is missing; recovery is required.");
        Directory.CreateDirectory(Root);
        bool existed = File.Exists(_path);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        try
        {
            db.Open();
            using var version = db.CreateCommand(); version.CommandText = "PRAGMA user_version";
            var schema = Convert.ToInt32(version.ExecuteScalar());
            if (schema is < 0 or > 1) throw new InvalidDataException("Unsupported Merge Session schema; preserve artifacts.");
            if (existed)
            {
                using var check = db.CreateCommand();
                check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('sessions','plans','sources','conflicts','roots')";
                if (Convert.ToInt64(check.ExecuteScalar()) != 5) throw new InvalidDataException("Merge Session schema is incomplete; recovery is required.");
                if (schema == 1)
                {
                    check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('prepared','artifacts')";
                    if (Convert.ToInt64(check.ExecuteScalar()) != 2) throw new InvalidDataException("Prepared Merge schema is incomplete.");
                }
            }
            using var command = db.CreateCommand(); command.CommandText = """
                PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=30000;
                CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, revision INTEGER NOT NULL, state INTEGER NOT NULL, data TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS plans(session TEXT NOT NULL, revision TEXT NOT NULL, data TEXT NOT NULL, PRIMARY KEY(session,revision));
                CREATE TABLE IF NOT EXISTS sources(session TEXT NOT NULL, revision TEXT NOT NULL, source TEXT NOT NULL, data TEXT NOT NULL, PRIMARY KEY(session,revision,source));
                CREATE TABLE IF NOT EXISTS conflicts(session TEXT NOT NULL, revision TEXT NOT NULL, id TEXT NOT NULL, data TEXT NOT NULL, resolution TEXT, PRIMARY KEY(session,revision,id));
                CREATE TABLE IF NOT EXISTS roots(session TEXT NOT NULL, version TEXT NOT NULL, PRIMARY KEY(session,version));
                CREATE TABLE IF NOT EXISTS prepared(session TEXT NOT NULL, revision INTEGER NOT NULL, data TEXT NOT NULL, PRIMARY KEY(session,revision));
                CREATE TABLE IF NOT EXISTS artifacts(session TEXT NOT NULL, path TEXT NOT NULL PRIMARY KEY);
                PRAGMA user_version=1;
                """; command.ExecuteNonQuery(); return db;
        }
        catch { db.Dispose(); throw; }
    }
    private static SqliteCommand Command(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    private static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static T Decode<T>(string value) => JsonSerializer.Deserialize<T>(value, Json) ?? throw new InvalidDataException("Merge Session data is empty.");

    public MergeSession Create(HistoryMergePlan plan, IEnumerable<VersionId> roots)
    {
        var session = new MergeSession(Guid.NewGuid(), 0, MergeSessionState.Preparing, plan);
        using var db = Open(); using var tx = db.BeginTransaction();
        using (var cmd = Command(db, "INSERT INTO sessions VALUES($id,0,$state,$data)",
            ("$id", session.Id.ToString()), ("$state", (int)session.State), ("$data", Encode(session)))) cmd.ExecuteNonQuery();
        SavePlan(db, session, roots); tx.Commit(); return session;
    }
    private static void SavePlan(SqliteConnection db, MergeSession session, IEnumerable<VersionId> roots)
    {
        using (var cmd = Command(db, "INSERT INTO plans VALUES($s,$r,$d)", ("$s", session.Id.ToString()),
            ("$r", session.Plan.Revision.ToString()), ("$d", Encode(session.Plan)))) cmd.ExecuteNonQuery();
        foreach (var root in roots.Distinct())
        {
            using var cmd = Command(db, "INSERT OR IGNORE INTO roots VALUES($s,$v)", ("$s", session.Id.ToString()), ("$v", root.ToString())); cmd.ExecuteNonQuery();
        }
    }
    public MergeSession Load(Guid id)
    {
        using var db = Open(); using var cmd = Command(db, "SELECT data FROM sessions WHERE id=$id", ("$id", id.ToString()));
        return Decode<MergeSession>(cmd.ExecuteScalar() as string ?? throw new InvalidDataException("Merge Session is missing."));
    }

    public PreparedMergeDescriptor? LoadPrepared(MergeSession session)
    {
        using var db = Open(); using var cmd = Command(db, "SELECT data FROM prepared WHERE session=$s AND revision=$r",
            ("$s", session.Id.ToString()), ("$r", session.Revision));
        return cmd.ExecuteScalar() is string data ? Decode<PreparedMergeDescriptor>(data) : null;
    }

    public void RegisterArtifact(MergeSession expected, string path)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        RequireReadyRevision(db, expected);
        using var cmd = Command(db, "INSERT INTO artifacts VALUES($s,$p)", ("$s", expected.Id.ToString()), ("$p", Path.GetFullPath(path)));
        cmd.ExecuteNonQuery(); tx.Commit();
    }

    public PreparedMergeDescriptor SavePrepared(MergeSession expected, PreparedMerge prepared)
    {
        var codec = new HistoryPackCodec();
        var pack = new HistoryCommitPack(prepared.PackId, prepared.TransactionId, prepared.Update.CreatedAtUtc, prepared.Facts.Select(f => codec.CreateObject(f)));
        var descriptor = new PreparedMergeDescriptor(expected.Id, expected.Plan.Revision, expected.Revision,
            expected.Plan.ProviderVersion, expected.Plan.PolicyVersion, prepared.Sources, prepared.Checkpoint, prepared.NewReplicas, codec.Encode(pack));
        using var db = Open(); using var tx = db.BeginTransaction(); RequireReadyRevision(db, expected);
        using (var cmd = Command(db, "INSERT OR IGNORE INTO prepared VALUES($s,$r,$d)", ("$s", expected.Id.ToString()), ("$r", expected.Revision), ("$d", Encode(descriptor)))) cmd.ExecuteNonQuery();
        tx.Commit(); return LoadPrepared(expected)!;
    }

    private static void RequireReadyRevision(SqliteConnection db, MergeSession expected)
    {
        using var cmd = Command(db, "SELECT revision FROM sessions WHERE id=$s AND state=$st", ("$s", expected.Id.ToString()), ("$st", (int)MergeSessionState.Ready));
        if (Convert.ToInt64(cmd.ExecuteScalar() ?? -1L) != expected.Revision) throw new InvalidOperationException("Merge Session is not Ready at this revision.");
    }
    public MergeSession Replan(MergeSession expected, HistoryMergePlan plan, IEnumerable<VersionId> roots)
    {
        if (expected.State is MergeSessionState.Applying or MergeSessionState.Committed or MergeSessionState.Abandoned)
            throw new InvalidOperationException("This Session cannot be replanned.");
        var next = expected with { Revision = expected.Revision + 1, State = MergeSessionState.Preparing, Plan = plan, ProtectedWorkspace = null };
        using var db = Open(); using var tx = db.BeginTransaction();
        SavePlan(db, next, roots); Cas(db, expected, next); tx.Commit(); return next;
    }
    public MergeSession CompletePreparation(MergeSession expected, IEnumerable<VersionId> roots)
    {
        if (expected.State != MergeSessionState.Preparing) throw new InvalidOperationException("Session is not preparing.");
        using var db = Open(); using var tx = db.BeginTransaction();
        using var missing = Command(db, "SELECT COUNT(*) FROM conflicts WHERE session=$s AND revision=$r AND resolution IS NULL",
            ("$s", expected.Id.ToString()), ("$r", expected.Plan.Revision.ToString()));
        var next = expected with { Revision = checked(expected.Revision + 1),
            State = Convert.ToInt64(missing.ExecuteScalar()) == 0 ? MergeSessionState.Ready : MergeSessionState.Resolving };
        Cas(db, expected, next);
        using (var clear = Command(db, "DELETE FROM roots WHERE session=$s", ("$s", expected.Id.ToString()))) clear.ExecuteNonQuery();
        foreach (var root in roots.Distinct())
        {
            using var insert = Command(db, "INSERT INTO roots VALUES($s,$v)", ("$s", expected.Id.ToString()), ("$v", root.ToString())); insert.ExecuteNonQuery();
        }
        tx.Commit(); return next;
    }

    private bool ManualIsOwnedAndValid(MergeSession session, MergeFileValue file)
    {
        try
        {
            var root = Path.GetFullPath(SessionDirectory(session.Id));
            var path = Path.GetFullPath(file.Handle);
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            for (var parent = path; !parent.Equals(root, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent)!)
                if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes("folderrewind/file/1\0"));
            var buffer = new byte[65536]; int count;
            while ((count = stream.Read(buffer)) != 0) hash.AppendData(buffer, 0, count);
            return stream.Length == file.Length && Convert.ToHexString(hash.GetHashAndReset()) == file.Digest;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public IReadOnlyList<MergeSession> List()
    {
        using var db = Open(); using var cmd = Command(db, "SELECT data FROM sessions ORDER BY rowid DESC");
        using var reader = cmd.ExecuteReader(); var result = new List<MergeSession>();
        while (reader.Read()) result.Add(Decode<MergeSession>(reader.GetString(0))); return result;
    }
    public MergeSession RecordProtection(MergeSession expected, HistoryWorkspace workspace)
    {
        if (expected.State != MergeSessionState.Ready) throw new InvalidOperationException("Session is not ready.");
        var next = expected with { Revision = checked(expected.Revision + 1), ProtectedWorkspace = workspace };
        using var db = Open(); using var tx = db.BeginTransaction(); Cas(db, expected, next); tx.Commit(); return next;
    }
    public MergeSession Update(MergeSession expected, MergeSessionState state, HistoryTransactionId? transaction = null, PackId? pack = null)
    {
        bool allowed = expected.State switch
        {
            MergeSessionState.Preparing => state is MergeSessionState.Ready or MergeSessionState.Resolving or MergeSessionState.Stale or MergeSessionState.Abandoned,
            MergeSessionState.Resolving => state is MergeSessionState.Stale or MergeSessionState.Abandoned,
            MergeSessionState.Ready => state is MergeSessionState.Applying or MergeSessionState.Stale or MergeSessionState.Abandoned,
            MergeSessionState.Stale => state == MergeSessionState.Abandoned,
            MergeSessionState.Applying => state is MergeSessionState.Ready or MergeSessionState.Committed,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException("Invalid Merge Session state transition.");
        if (state == MergeSessionState.Applying && (transaction is null || pack is null))
            throw new InvalidOperationException("Applying requires a durable transaction identity.");
        var next = expected with { Revision = checked(expected.Revision + 1), State = state,
            ApplyTransactionId = transaction ?? expected.ApplyTransactionId, IntendedPackId = pack ?? expected.IntendedPackId };
        using var db = Open(); using var tx = db.BeginTransaction(); Cas(db, expected, next); tx.Commit(); return next;
    }
    private static void Cas(SqliteConnection db, MergeSession expected, MergeSession next)
    {
        using var cmd = Command(db, "UPDATE sessions SET revision=$r,state=$st,data=$d WHERE id=$id AND revision=$old",
            ("$r", next.Revision), ("$st", (int)next.State), ("$d", Encode(next)), ("$id", expected.Id.ToString()), ("$old", expected.Revision));
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Merge Session revision changed.");
    }
    public void SaveSource(MergeSession session, MergeSessionSource source, IEnumerable<MergeConflict> conflicts)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using (var check = Command(db, "SELECT revision FROM sessions WHERE id=$s AND state=$st", ("$s", session.Id.ToString()), ("$st", (int)MergeSessionState.Preparing)))
            if (Convert.ToInt64(check.ExecuteScalar() ?? -1L) != session.Revision) throw new InvalidOperationException("Session is not preparing this revision.");
        using (var cmd = Command(db, "INSERT OR REPLACE INTO sources VALUES($s,$r,$id,$d)", ("$s", session.Id.ToString()),
            ("$r", session.Plan.Revision.ToString()), ("$id", source.Plan.SourceId.ToString()), ("$d", Encode(source)))) cmd.ExecuteNonQuery();
        using (var clear = Command(db, "DELETE FROM conflicts WHERE session=$s AND revision=$r AND json_extract(data,'$.subject.sourceId')=$source",
            ("$s", session.Id.ToString()), ("$r", session.Plan.Revision.ToString()), ("$source", source.Plan.SourceId.ToString()))) clear.ExecuteNonQuery();
        foreach (var conflict in conflicts)
        {
            MergeResolution? migrated = null;
            using (var prior = Command(db, "SELECT resolution FROM conflicts WHERE session=$s AND revision<>$r AND json_extract(data,'$.inputSignature')=$sig AND resolution IS NOT NULL ORDER BY rowid DESC LIMIT 1",
                ("$s", session.Id.ToString()), ("$r", session.Plan.Revision.ToString()), ("$sig", conflict.InputSignature)))
            {
                if (prior.ExecuteScalar() is string data)
                {
                    var value = Decode<MergeResolution>(data);
                    if (value.InputSignature == conflict.InputSignature && Enum.IsDefined(value.Choice)
                        && (value.Choice != MergeResolutionChoice.Manual || value.Manual is { } manual && ManualIsOwnedAndValid(session, manual)))
                        migrated = value with { PlanRevision = session.Plan.Revision, ConflictId = conflict.Id };
                }
            }
            using var cmd = Command(db, "INSERT OR REPLACE INTO conflicts VALUES($s,$r,$id,$d,$resolution)", ("$s", session.Id.ToString()),
                ("$r", session.Plan.Revision.ToString()), ("$id", conflict.Id), ("$d", Encode(conflict)),
                ("$resolution", migrated is null ? null : Encode(migrated))); cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    public IReadOnlyList<MergeSessionSource> Sources(MergeSession session)
    {
        using var db = Open(); using var cmd = Command(db, "SELECT data FROM sources WHERE session=$s AND revision=$r ORDER BY source",
            ("$s", session.Id.ToString()), ("$r", session.Plan.Revision.ToString()));
        using var reader = cmd.ExecuteReader(); var result = new List<MergeSessionSource>();
        while (reader.Read()) result.Add(Decode<MergeSessionSource>(reader.GetString(0))); return result;
    }
    public IReadOnlyList<(MergeConflict Conflict, MergeResolution? Resolution)> Conflicts(MergeSession session, int offset = 0, int count = 100)
    {
        if (offset < 0 || count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(count));
        using var db = Open(); using var cmd = Command(db, "SELECT data,resolution FROM conflicts WHERE session=$s AND revision=$r ORDER BY id LIMIT $n OFFSET $o",
            ("$s", session.Id.ToString()), ("$r", session.Plan.Revision.ToString()), ("$n", count), ("$o", offset));
        using var reader = cmd.ExecuteReader(); var result = new List<(MergeConflict, MergeResolution?)>();
        while (reader.Read()) result.Add((Decode<MergeConflict>(reader.GetString(0)), reader.IsDBNull(1) ? null : Decode<MergeResolution>(reader.GetString(1)))); return result;
    }
    public MergeSession Resolve(MergeSession session, MergeResolution resolution)
    {
        if (!Enum.IsDefined(resolution.Choice)) throw new InvalidOperationException("Unknown resolution choice.");
        if (session.State is not (MergeSessionState.Resolving or MergeSessionState.Ready) || resolution.PlanRevision != session.Plan.Revision)
            throw new InvalidOperationException("Resolution belongs to another plan or Session state.");
        using var db = Open(); using var tx = db.BeginTransaction();
        using var read = Command(db, "SELECT data FROM conflicts WHERE session=$s AND revision=$r AND id=$id",
            ("$s", session.Id.ToString()), ("$r", session.Plan.Revision.ToString()), ("$id", resolution.ConflictId));
        var conflict = Decode<MergeConflict>(read.ExecuteScalar() as string ?? throw new InvalidDataException("Conflict is missing."));
        if (resolution.Choice == MergeResolutionChoice.Manual && (resolution.Manual is null || conflict.Subject.Paths.Length != 1
            || conflict.Kind is MergeConflictKind.PathStructure or MergeConflictKind.SourceRoster or MergeConflictKind.SourceBoundary))
            throw new InvalidOperationException("Manual resolution requires a single file artifact.");
        if (resolution.Choice != MergeResolutionChoice.Manual && resolution.Manual is not null)
            throw new InvalidOperationException("Whole-side resolution cannot attach manual content.");
        if (conflict.InputSignature != resolution.InputSignature) throw new InvalidOperationException("Conflict inputs changed.");
        using (var update = Command(db, "UPDATE conflicts SET resolution=$d WHERE session=$s AND revision=$r AND id=$id",
            ("$d", Encode(resolution)), ("$s", session.Id.ToString()), ("$r", session.Plan.Revision.ToString()), ("$id", resolution.ConflictId))) update.ExecuteNonQuery();
        using var unresolved = Command(db, "SELECT COUNT(*) FROM conflicts WHERE session=$s AND revision=$r AND resolution IS NULL",
            ("$s", session.Id.ToString()), ("$r", session.Plan.Revision.ToString()));
        var next = session with { Revision = session.Revision + 1, State = (long)unresolved.ExecuteScalar()! == 0 ? MergeSessionState.Ready : MergeSessionState.Resolving };
        Cas(db, session, next); tx.Commit(); return next;
    }
    // Caller holds the Runtime gate. Keep every published payload, even for completed sessions.
    public void CleanupTerminalArtifacts(LocalReplicaCatalog catalog)
    {
        var repositoryRoot = Path.GetDirectoryName(Path.GetDirectoryName(Root))!;
        HistoryRestoreTransactionJournalStore.RequireRecovered(Path.Combine(repositoryRoot, "transactions"));
        var retained = catalog.Entries.Where(e => e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath)
            .Select(e => Path.GetFullPath(e.Locator.AbsolutePath)).ToArray();
        foreach (var session in List().Where(s => s.State is MergeSessionState.Committed or MergeSessionState.Abandoned))
        {
            var root = Path.GetFullPath(SessionDirectory(session.Id));
            void Clean(string directory, string ownedRoot)
            {
                var full = Path.GetFullPath(directory);
                if (!full.Equals(ownedRoot, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(ownedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Session cleanup escapes its root.");
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Session cleanup cannot follow links.");
                foreach (var child in Directory.EnumerateDirectories(full)) Clean(child, ownedRoot);
                foreach (var file in Directory.EnumerateFiles(full))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Session cleanup cannot follow links.");
                    if (!retained.Contains(Path.GetFullPath(file), StringComparer.OrdinalIgnoreCase)) File.Delete(file);
                }
                if (!Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full);
            }
            if (Directory.Exists(root)) Clean(root, root);
            using var db = Open();
            var owned = new List<string>();
            using (var query = Command(db, "SELECT path FROM artifacts WHERE session=$s", ("$s", session.Id.ToString())))
            using (var reader = query.ExecuteReader()) while (reader.Read()) owned.Add(Path.GetFullPath(reader.GetString(0)));
            foreach (var path in owned)
            {
                var payloadRoot = Path.Combine(repositoryRoot, "payloads");
                if (path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(path), payloadRoot))
                    throw new InvalidDataException("Owned payload directory escapes repository payloads.");
                _ = RepresentationId.Parse(Path.GetFileName(path));
                if (Directory.Exists(path)) Clean(path, path);
            }
        }
    }

    public IReadOnlySet<VersionId> ActiveRoots()
    {
        // 数据库丢失但仍有会话产物时不能视为零 roots。
        if (!File.Exists(_path) && Directory.Exists(Root) && Directory.EnumerateFileSystemEntries(Root).Any())
            throw new InvalidDataException("Merge Session store is missing; preserve artifacts and block GC.");
        using var db = Open(); using var cmd = Command(db, "SELECT DISTINCT version FROM roots JOIN sessions ON roots.session=sessions.id WHERE state NOT IN ($c,$a)",
            ("$c", (int)MergeSessionState.Committed), ("$a", (int)MergeSessionState.Abandoned));
        using var reader = cmd.ExecuteReader(); var result = new HashSet<VersionId>();
        while (reader.Read()) result.Add(VersionId.Parse(reader.GetString(0))); return result;
    }
    public IReadOnlySet<RepresentationId> ProtectedRepresentations(IEnumerable<VersionRepresentation> representations)
    {
        var roots = ActiveRoots(); var map = representations.ToDictionary(r => r.RepresentationId);
        var pending = new Stack<RepresentationId>(map.Values.Where(r => roots.Contains(r.VersionId)).Select(r => r.RepresentationId));
        var result = new HashSet<RepresentationId>();
        while (pending.TryPop(out var id))
        {
            if (!result.Add(id)) continue;
            if (!map.TryGetValue(id, out var value)) throw new InvalidDataException("Session representation dependency is missing.");
            foreach (var dependency in value.DependencyRepresentationIds) pending.Push(dependency);
        }
        return result;
    }
}

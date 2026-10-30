using FolderRewind.Plugin.Abstractions;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text;

namespace FolderRewind.Services;

internal sealed record SpatialWorkspaceRow(string Id, string Title, string Description, string Status,
    bool CanSelect, IReadOnlyList<SpatialSubjectLocation> Locations)
{
    public string[] Components { get; init; } = [];
    public string[] SourcePaths { get; init; } = [];
    public string[] MemberIds { get; init; } = [];
    public string[] States { get; init; } = [];
    public bool Checked { get; init; }
}

/// <summary>Rebuildable UI index. Decisions continue to be owned by MergeSessionStore.</summary>
internal sealed partial class SpatialWorkspaceIndex(string path) : IDisposable
{
    private readonly object _gate = new();
    private SqliteConnection? _db;
    private bool _disposed;
    private long _metadataBytes;
    private int _rowCount;
    private SqliteConnection Db
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_db is not null) return _db;
            _db = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); _db.Open();
            using var command = _db.CreateCommand(); command.CommandText = """
                CREATE TABLE rows(id TEXT PRIMARY KEY,title TEXT NOT NULL,status TEXT NOT NULL,selectable INTEGER NOT NULL,data TEXT NOT NULL,bytes INTEGER NOT NULL,groupkey TEXT NOT NULL,sortx REAL,sorty REAL);
                CREATE INDEX rows_group ON rows(groupkey);
                CREATE TABLE components(id TEXT NOT NULL,component TEXT NOT NULL);
                CREATE INDEX components_lookup ON components(component,id);
                CREATE INDEX components_id ON components(id);
                CREATE TABLE locations(id TEXT NOT NULL,world TEXT NOT NULL,layer TEXT,minx REAL,miny REAL,maxx REAL,maxy REAL);
                CREATE INDEX locations_window ON locations(world,layer,minx,maxx,miny,maxy,id);
                CREATE INDEX locations_id ON locations(id);
                CREATE TABLE selection(id TEXT PRIMARY KEY);
                """; command.ExecuteNonQuery(); return _db;
        }
    }
    private SqliteCommand Command(string sql, params (string, object?)[] parameters)
    {
        var command = Db.CreateCommand(); command.CommandText = sql;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    internal void Clear()
    { lock (_gate) { using var c = Command("DELETE FROM selection; DELETE FROM locations; DELETE FROM rows; DELETE FROM components"); c.ExecuteNonQuery(); _metadataBytes = 0; _rowCount = 0; } }
    internal void Put(IEnumerable<SpatialWorkspaceRow> rows, bool allowReplace = true)
    {
        lock (_gate)
        {
            using var tx = Db.BeginTransaction();
            var charged = _metadataBytes; var rowCount = _rowCount;
            foreach (var row in rows)
            {
                var data = JsonSerializer.Serialize(row);
                if (data.Length > 65536 || row.Locations.Count > 128) throw new InvalidDataException("Spatial item budget exceeded.");
                var bytes = Encoding.UTF8.GetByteCount(data) + row.Title.Length * 3L + row.Locations.Count * 256L + row.Id.Length * 4L;
                using var previous = Command("SELECT bytes FROM rows WHERE id=$i", ("$i", row.Id)); var prior = previous.ExecuteScalar();
                if (!allowReplace && prior is not null) throw new InvalidDataException("Duplicate spatial comparison identity.");
                charged += bytes - (prior is null ? 0 : Convert.ToInt64(prior)); if (prior is null) rowCount++;
                if (charged > 256L * 1024 * 1024 || rowCount > 1_000_000) throw new IOException("Spatial index budget exceeded.");
                using var write = Command("INSERT OR REPLACE INTO rows VALUES($i,$t,$s,$c,$d,$b,$g,$sx,$sy)", ("$i", row.Id), ("$t", row.Title + "\n" + row.Description + "\n" + string.Join("\n", row.SourcePaths)), ("$s", row.Status), ("$c", row.CanSelect ? 1 : 0), ("$d", data), ("$b", bytes), ("$g", GroupKey(row)), ("$sx", row.Locations.FirstOrDefault()?.Bounds?.MinX), ("$sy", row.Locations.FirstOrDefault()?.Bounds?.MinY)); write.ExecuteNonQuery();
                using (var clearComponents = Command("DELETE FROM components WHERE id=$i", ("$i", row.Id))) clearComponents.ExecuteNonQuery();
                foreach (var component in row.Components.Distinct(StringComparer.Ordinal))
                { using var insert = Command("INSERT INTO components VALUES($i,$c)", ("$i", row.Id), ("$c", component)); insert.ExecuteNonQuery(); }
                using var clear = Command("DELETE FROM locations WHERE id=$i", ("$i", row.Id)); clear.ExecuteNonQuery();
                foreach (var l in row.Locations)
                {
                    if (string.IsNullOrWhiteSpace(l.WorldId) || l.Bounds is { } b && (new[] { b.MinX, b.MinY, b.MaxX, b.MaxY }.Any(v => !double.IsFinite(v)) || b.MinX >= b.MaxX || b.MinY >= b.MaxY))
                        throw new InvalidDataException("Invalid spatial bounds.");
                    using var location = Command("INSERT INTO locations VALUES($i,$w,$l,$x,$y,$xx,$yy)", ("$i", row.Id), ("$w", l.WorldId), ("$l", l.LayerId), ("$x", l.Bounds?.MinX), ("$y", l.Bounds?.MinY), ("$xx", l.Bounds?.MaxX), ("$yy", l.Bounds?.MaxY)); location.ExecuteNonQuery();
                }
            }
            tx.Commit(); _metadataBytes = charged; _rowCount = rowCount;
        }
    }
    private const string Window = "EXISTS(SELECT 1 FROM locations l WHERE l.id=r.id AND l.world=$w AND (l.layer IS NULL OR l.layer=$l) AND (l.minx IS NULL OR l.minx<$xx AND l.maxx>$x AND l.miny<$yy AND l.maxy>$y))";
    internal IReadOnlyList<SpatialWorkspaceRow> Query(string search, string filter, int offset, int count = 100)
    {
        lock (_gate)
        {
            using var c = Command("SELECT data FROM rows r WHERE ($q='' OR instr(lower(title),lower($q))>0) AND ($f='' OR status=$f) ORDER BY title,id LIMIT $n OFFSET $o", ("$q", search), ("$f", filter), ("$n", count), ("$o", offset));
            return Read(c);
        }
    }
    internal IReadOnlyList<SpatialWorkspaceRow> InView(string world, string layer, SpatialPreviewBounds bounds, int count = 1000)
    {
        lock (_gate)
        {
            using var c = Command("SELECT data FROM rows r WHERE " + Window + " ORDER BY id LIMIT $n", Args(world, layer, bounds).Append(("$n", (object?)count)).ToArray());
            return Read(c);
        }
    }
    internal void SelectArea(string world, string layer, SpatialPreviewBounds bounds, bool additive, bool remove, string filter)
    {
        lock (_gate)
        {
            using var tx = Db.BeginTransaction();
            if (!additive && !remove) { using var clear = Command("DELETE FROM selection"); clear.ExecuteNonQuery(); }
            using var c = Command((remove ? "DELETE FROM selection WHERE id IN (SELECT r.id FROM rows r WHERE " : "INSERT OR IGNORE INTO selection SELECT r.id FROM rows r WHERE ")
                + Window + " AND r.selectable=1 AND ($f='' OR r.status=$f)" + (remove ? ")" : ""), Args(world, layer, bounds).Append(("$f", (object?)filter)).ToArray()); c.ExecuteNonQuery(); tx.Commit();
        }
    }
    internal void SelectItem(string id, bool additive)
    {
        lock (_gate)
        {
            if (!additive) { using var clear = Command("DELETE FROM selection"); clear.ExecuteNonQuery(); }
            using var c = Command("INSERT OR IGNORE INTO selection SELECT id FROM rows WHERE id=$i AND selectable=1", ("$i", id)); c.ExecuteNonQuery();
        }
    }
    internal string[] Selected()
    {
        lock (_gate)
        {
            using var c = Command("SELECT id FROM selection ORDER BY id"); using var read = c.ExecuteReader(); var result = new List<string>();
            while (read.Read()) { if (result.Count >= 100_000) throw new IOException("Selection exceeds the operation budget."); result.Add(read.GetString(0)); } return result.ToArray();
        }
    }
    internal (int Total, int Selected, int NonSpatial) Counts()
    {
        lock (_gate)
        {
            using var c = Command("SELECT (SELECT COUNT(*) FROM rows),(SELECT COUNT(*) FROM selection),(SELECT COUNT(*) FROM rows r WHERE NOT EXISTS(SELECT 1 FROM locations l WHERE l.id=r.id))");
            using var r = c.ExecuteReader(); r.Read(); return (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
        }
    }
    private static (string, object?)[] Args(string world, string layer, SpatialPreviewBounds b)
        => [("$w", world), ("$l", layer), ("$x", b.MinX), ("$y", b.MinY), ("$xx", b.MaxX), ("$yy", b.MaxY)];
    private static IReadOnlyList<SpatialWorkspaceRow> Read(SqliteCommand command)
    {
        using var read = command.ExecuteReader(); var rows = new List<SpatialWorkspaceRow>();
        while (read.Read()) rows.Add(JsonSerializer.Deserialize<SpatialWorkspaceRow>(read.GetString(0))!); return rows;
    }
    public void Dispose() { lock (_gate) { _disposed = true; _db?.Dispose(); _db = null; } }
}

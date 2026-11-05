using FolderRewind.Plugin.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace FolderRewind.Services;

internal sealed record SpatialWorkspaceQuery(string World, string Layer, string Search = "", string Status = "",
    string[]? Categories = null, bool Files = false, bool Group = true, string[]? KnownCategories = null);
internal sealed record SpatialWorkspaceOverlay(SpatialPreviewBounds Bounds, string Status, int Count,
    bool Aggregate = false, string[]? States = null, string[]? Ids = null, string[]? Components = null, bool Checked = false);
internal sealed record SpatialAreaSelectionSummary(int Unresolved, int Decided, int Locked);

internal sealed partial class SpatialWorkspaceIndex
{
    internal bool IsDisposed => Volatile.Read(ref _disposed);
    internal SpatialWorkspaceRow? ByIds(string[] ids)
    {
        lock (_gate)
        {
            if (_disposed || ids.Length == 0) return null;
            using var c = Command("SELECT data FROM rows WHERE id IN (" + string.Join(",", ids.Select((_, i) => "$i" + i)) + ")",
                ids.Select((id, i) => ("$i" + i, (object?)id)).ToArray());
            var rows = Read(c); return rows.Count == 0 ? null : Combine(rows);
        }
    }
    private static string GroupKey(SpatialWorkspaceRow row) => row.Locations.Count == 1 && row.Locations[0].Bounds is not null
        ? JsonSerializer.Serialize(row.Locations[0]) : row.Id;
    private static (string Sql, (string, object?)[] Args) Predicate(SpatialWorkspaceQuery query)
    {
        var args = new List<(string, object?)> { ("$w", query.World), ("$l", query.Layer), ("$q", query.Search), ("$f", query.Status) };
        var sql = "($q='' OR instr(lower(r.title),lower($q))>0) AND ($f='' OR r.status=$f) AND ";
        sql += query.Files ? "NOT EXISTS(SELECT 1 FROM locations l WHERE l.id=r.id)"
            : "EXISTS(SELECT 1 FROM locations l WHERE l.id=r.id AND l.world=$w AND (l.layer IS NULL OR l.layer=$l))";
        if (query.Categories is { } categories)
        {
            var names = categories.Select((c, i) => { var name = "$c" + i; args.Add((name, c)); return name; }).ToArray();
            var unknown = "";
            if (query.KnownCategories is { Length: > 0 } known)
            {
                var keys = known.Select((c, i) => { var name = "$known" + i; args.Add((name, c)); return name; }).ToArray();
                unknown = " OR EXISTS(SELECT 1 FROM components c WHERE c.id=r.id AND c.component NOT IN (" + string.Join(",", keys) + "))";
            }
            sql += " AND (r.status='Unavailable' OR NOT EXISTS(SELECT 1 FROM components c WHERE c.id=r.id)"
                + unknown + (names.Length == 0 ? "" : " OR EXISTS(SELECT 1 FROM components c WHERE c.id=r.id AND c.component IN (" + string.Join(",", names) + "))") + ")";
        }
        return (sql, args.ToArray());
    }
    internal IReadOnlyList<SpatialWorkspaceRow> Query(SpatialWorkspaceQuery query, int offset, int count = 100)
        => Query(query, offset, count, CancellationToken.None);
    private IReadOnlyList<SpatialWorkspaceRow> Query(SpatialWorkspaceQuery query, int offset, int count, CancellationToken token)
    {
        lock (_gate)
        {
            var p = Predicate(query); var key = query.Group ? "r.groupkey" : "r.id";
            using var groups = Command("SELECT " + key + " FROM rows r WHERE " + p.Sql + " GROUP BY " + key
                + " ORDER BY MIN(sortx),MIN(sorty),MIN(title)," + key + " LIMIT $n OFFSET $o",
                p.Args.Concat(new (string, object?)[] { ("$n", count), ("$o", offset) }).ToArray());
            var keys = new List<string>();
            using (var read = Executed(groups).ExecuteReader())
                while (read.Read()) { token.ThrowIfCancellationRequested(); keys.Add(read.GetString(0)); }
            return keys.Select(k =>
            {
                token.ThrowIfCancellationRequested();
                using var rows = Command("SELECT data FROM rows r WHERE " + p.Sql + " AND " + key + "=$key ORDER BY r.id",
                    p.Args.Append(("$key", (object?)k)).ToArray());
                return Combine(Read(rows));
            }).ToArray();
        }
    }
    private SpatialWorkspaceRow Combine(IReadOnlyList<SpatialWorkspaceRow> rows)
    {
        var first = rows[0]; var states = rows.Select(r => r.Status).Distinct().ToArray();
        using var selected = Command("SELECT COUNT(*) FROM selection s JOIN rows r ON r.id=s.id WHERE r.groupkey=$g", ("$g", GroupKey(first)));
        return first with { MemberIds = rows.Select(r => r.Id).ToArray(), States = states,
            Status = states.Length == 1 ? states[0] : "Mixed",
            Description = string.Join("\n\n", rows.Select(r => r.Description).Distinct()),
            Components = rows.SelectMany(r => r.Components).Distinct().ToArray(),
            SourcePaths = rows.SelectMany(r => r.SourcePaths).Distinct().ToArray(),
            Checked = Convert.ToInt32(Executed(selected).ExecuteScalar()) > 0 };
    }
    internal int Count(SpatialWorkspaceQuery query)
    {
        lock (_gate)
        {
            var p = Predicate(query);
            using var c = Command("SELECT COUNT(DISTINCT " + (query.Group ? "r.groupkey" : "r.id") + ") FROM rows r WHERE " + p.Sql, p.Args);
            return Convert.ToInt32(Executed(c).ExecuteScalar());
        }
    }
    internal int WholeDomainCount(SpatialWorkspaceQuery query)
    {
        lock (_gate)
        {
            var p = Predicate(query);
            using var c = Command("SELECT COUNT(*) FROM rows r WHERE " + p.Sql + " AND EXISTS(SELECT 1 FROM locations l WHERE l.id=r.id AND l.minx IS NULL)", p.Args);
            return Convert.ToInt32(Executed(c).ExecuteScalar());
        }
    }
    internal IReadOnlyDictionary<string, int> CategoryCounts(SpatialWorkspaceQuery query)
    {
        lock (_gate)
        {
            var p = Predicate(query with { Categories = null });
            using var c = Command("SELECT c.component,COUNT(DISTINCT r.groupkey) FROM rows r JOIN components c ON c.id=r.id WHERE " + p.Sql + " GROUP BY c.component", p.Args);
            var result = new Dictionary<string, int>();
            using var r = Executed(c).ExecuteReader(); while (r.Read()) result[r.GetString(0)] = r.GetInt32(1);
            return result;
        }
    }
    internal IReadOnlyList<SpatialWorkspaceOverlay> Overlays(string world, string layer, SpatialPreviewBounds bounds)
        => Overlays(new(world, layer, Group: false), bounds, 1, 16);
    internal IReadOnlyList<SpatialWorkspaceOverlay> Overlays(SpatialWorkspaceQuery query, SpatialPreviewBounds bounds, double scale, double grid)
        => Overlays(query, bounds, scale, grid, out _);
    internal IReadOnlyList<SpatialWorkspaceOverlay> Overlays(SpatialWorkspaceQuery query, SpatialPreviewBounds bounds, double scale, double grid,
        out long indexVersion, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using (EnterOperation("overlay", token, _diagnostics is null ? null : new Dictionary<string, string>
        {
            ["query"] = JsonSerializer.Serialize(query), ["bounds"] = JsonSerializer.Serialize(bounds),
            ["scale"] = scale.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            ["grid"] = grid.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
        }))
        {
            indexVersion = _mutationVersion;
            using var cancellation = new SqliteReadCancellation(Db, token);
            try { return ReadOverlays(query, bounds, scale, grid, token); }
            catch (Microsoft.Data.Sqlite.SqliteException error) when (error.SqliteErrorCode == 9 && token.IsCancellationRequested)
            {
                throw new OperationCanceledException("Spatial overlay read was superseded.", error, token);
            }
        }
    }
    private IReadOnlyList<SpatialWorkspaceOverlay> ReadOverlays(SpatialWorkspaceQuery query, SpatialPreviewBounds bounds,
        double scale, double grid, CancellationToken token)
    {
            if (query.Files || bounds.MinX >= bounds.MaxX || bounds.MinY >= bounds.MaxY) return [];
            var p = Predicate(query);
            var args = p.Args.Concat(new (string, object?)[] { ("$x", bounds.MinX), ("$y", bounds.MinY), ("$xx", bounds.MaxX), ("$yy", bounds.MaxY) }).ToArray();
            var where = p.Sql + " AND " + Window + " AND EXISTS(SELECT 1 FROM locations l WHERE l.id=r.id AND l.world=$w AND (l.layer=$l OR l.layer IS NULL) AND l.minx IS NOT NULL)";
            // A zoomed-out view always aggregates, so it does not need the detail count.
            var detail = false;
            if (grid * scale >= 10)
            {
                using var detailCountPhase = Measure("overlay.detail-count");
                using var count = Command("SELECT COUNT(DISTINCT r.groupkey) FROM rows r WHERE " + where, args);
                detail = Convert.ToInt32(Executed(count).ExecuteScalar()) <= 1000;
            }
            if (detail)
            {
                using var detailPhase = Measure("overlay.details");
                using var c = Command("SELECT data FROM rows r WHERE " + where + " ORDER BY groupkey,id", args);
                var rows = Read(c);
                return rows.SelectMany(r => r.Locations.Where(l => l.Bounds is not null && l.WorldId == query.World && (l.LayerId is null || l.LayerId == query.Layer)
                    && l.Bounds.MinX < bounds.MaxX && l.Bounds.MaxX > bounds.MinX && l.Bounds.MinY < bounds.MaxY && l.Bounds.MaxY > bounds.MinY).Select(l => (Row: r, Location: l)))
                    .GroupBy(p => p.Location).Select(g =>
                {
                    token.ThrowIfCancellationRequested();
                    var combined = Combine(g.Select(p => p.Row).DistinctBy(r => r.Id).ToArray());
                    return new SpatialWorkspaceOverlay(g.Key.Bounds!, combined.Status, 1, false, combined.States, combined.MemberIds, combined.Components, combined.Checked);
                }).ToArray();
            }
            grid = double.IsFinite(grid) && grid > 0 ? grid : 1;
            var cell = grid * Math.Pow(2, Math.Max(0, Math.Ceiling(Math.Log2(48 / Math.Max(1e-8, grid * scale)))));
            // Bound marker count by the viewport, independently of the number of changes.
            while ((bounds.MaxX - bounds.MinX) / cell * ((bounds.MaxY - bounds.MinY) / cell) > 512) cell *= 2;
            // CAST truncates toward zero; subtract one for a negative non-integral quotient.
            const string bx = "(CAST(l.minx/$cell AS INTEGER)-(l.minx/$cell<CAST(l.minx/$cell AS INTEGER)))";
            const string by = "(CAST(l.miny/$cell AS INTEGER)-(l.miny/$cell<CAST(l.miny/$cell AS INTEGER)))";
            args = p.Args.Concat(new (string, object?)[] { ("$x", Math.Floor(bounds.MinX / cell) * cell), ("$y", Math.Floor(bounds.MinY / cell) * cell),
                ("$xx", Math.Ceiling(bounds.MaxX / cell) * cell), ("$yy", Math.Ceiling(bounds.MaxY / cell) * cell) }).ToArray();
            using var aggregate = Command("SELECT " + bx + "," + by + ",COUNT(DISTINCT printf('%.17g,%.17g,%.17g,%.17g',l.minx,l.miny,l.maxx,l.maxy)),GROUP_CONCAT(DISTINCT r.status),MAX(s.id IS NOT NULL),GROUP_CONCAT(DISTINCT c.component)"
                + " FROM rows r JOIN locations l ON l.id=r.id LEFT JOIN selection s ON s.id=r.id LEFT JOIN components c ON c.id=r.id WHERE " + p.Sql
                + " AND l.world=$w AND (l.layer=$l OR l.layer IS NULL) AND l.minx IS NOT NULL"
                + " AND l.minx<$xx AND l.maxx>$x AND l.miny<$yy AND l.maxy>$y GROUP BY " + bx + "," + by,
                args.Append(("$cell", (object?)cell)).ToArray());
            using var aggregatePhase = Measure("overlay.aggregate");
            var result = new List<SpatialWorkspaceOverlay>();
            using var read = Executed(aggregate).ExecuteReader();
            while (read.Read())
            {
                token.ThrowIfCancellationRequested();
                var x = read.GetInt64(0) * cell; var y = read.GetInt64(1) * cell;
                var states = read.GetString(3).Split(',');
                result.Add(new(new(x, y, x + cell, y + cell), states.Length == 1 ? states[0] : "Mixed", read.GetInt32(2), true, states,
                    Components: read.IsDBNull(5) ? [] : read.GetString(5).Split(','), Checked: read.GetBoolean(4)));
            }
            token.ThrowIfCancellationRequested();
            return result;
    }
    internal void ToggleItem(string id, bool selected)
    {
        lock (_gate)
        {
            using var c = selected ? Command("INSERT OR IGNORE INTO selection SELECT id FROM rows WHERE id=$i AND selectable=1", ("$i", id))
                : Command("DELETE FROM selection WHERE id=$i", ("$i", id)); Executed(c).ExecuteNonQuery();
        }
    }
    internal SpatialAreaSelectionSummary SelectArea(SpatialWorkspaceQuery query, SpatialPreviewBounds bounds, bool additive, bool remove, bool onlyUnresolved = true)
    {
        lock (_gate)
        {
            var p = Predicate(query);
            var args = p.Args.Concat(new (string, object?)[] { ("$x", bounds.MinX), ("$y", bounds.MinY), ("$xx", bounds.MaxX), ("$yy", bounds.MaxY) }).ToArray();
            var all = Predicate(query with { Status = "" });
            using var summary = Command("SELECT COALESCE(SUM(r.selectable=1 AND r.status='Unresolved'),0),"
                + "COALESCE(SUM(r.selectable=1 AND r.status<>'Unresolved'),0),COALESCE(SUM(r.selectable=0),0) FROM rows r WHERE " + all.Sql + " AND " + Window,
                all.Args.Concat(new (string, object?)[] { ("$x", bounds.MinX), ("$y", bounds.MinY), ("$xx", bounds.MaxX), ("$yy", bounds.MaxY) }).ToArray());
            SpatialAreaSelectionSummary counts;
            using (var read = Executed(summary).ExecuteReader()) { read.Read(); counts = new(read.GetInt32(0), read.GetInt32(1), read.GetInt32(2)); }
            using var tx = Db.BeginTransaction();
            if (!additive && !remove) { using var clear = Command("DELETE FROM selection"); Executed(clear).ExecuteNonQuery(); }
            using var c = Command((remove ? "DELETE FROM selection WHERE id IN (SELECT r.id FROM rows r WHERE " : "INSERT OR IGNORE INTO selection SELECT r.id FROM rows r WHERE ")
                + p.Sql + " AND " + Window + " AND r.selectable=1" + (!remove && onlyUnresolved ? " AND r.status='Unresolved'" : "") + (remove ? ")" : ""), args);
            Executed(c).ExecuteNonQuery(); tx.Commit();
            return counts;
        }
    }
    internal void ClearSelection() { lock (_gate) { using var c = Command("DELETE FROM selection"); Executed(c).ExecuteNonQuery(); } }
}

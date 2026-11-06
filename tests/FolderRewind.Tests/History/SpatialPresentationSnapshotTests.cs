using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using Microsoft.Data.Sqlite;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SpatialPresentationSnapshotTests
{
    [TestMethod]
    public void CorrelatedLocationReadsSeekByRowIdentityBeforeWorldAndViewport()
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-spatial-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "index.db");
            using var index = new SpatialWorkspaceIndex(path);
            index.Put([]);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            const string location = "EXISTS(SELECT 1 FROM locations l WHERE l.id=r.id AND l.world=$w AND (l.layer IS NULL OR l.layer=$l))";
            const string viewport = "EXISTS(SELECT 1 FROM locations l WHERE l.id=r.id AND l.world=$w AND (l.layer IS NULL OR l.layer=$l) AND (l.minx IS NULL OR l.minx<$xx AND l.maxx>$x AND l.miny<$yy AND l.maxy>$y))";
            foreach (var query in new[] {
                "SELECT COUNT(DISTINCT r.groupkey) FROM rows r WHERE " + location,
                "SELECT COUNT(*) FROM rows r WHERE " + location + " AND EXISTS(SELECT 1 FROM locations l WHERE l.id=r.id AND l.minx IS NULL)",
                "SELECT c.component,COUNT(DISTINCT r.groupkey) FROM rows r JOIN components c ON c.id=r.id WHERE " + location + " GROUP BY c.component",
                "SELECT data FROM rows r WHERE " + location + " AND r.groupkey=$key ORDER BY r.id",
                "SELECT data FROM rows r WHERE " + viewport + " ORDER BY id LIMIT 1000" })
            {
                using var command = connection.CreateCommand();
                command.CommandText = "EXPLAIN QUERY PLAN " + query;
                foreach (var parameter in new[] { "$w", "$l", "$key", "$x", "$y", "$xx", "$yy" })
                    command.Parameters.AddWithValue(parameter, "fixture");
                var plan = new List<string>();
                using var reader = command.ExecuteReader();
                while (reader.Read()) plan.Add(reader.GetString(3));
                // A world-only covering seek makes these per-row EXISTS reads quadratic.
                Assert.IsTrue(plan.Any(step => step.Contains("(id=? AND world=?)", StringComparison.Ordinal)),
                    string.Join("; ", plan));
            }
            using var componentPlan = connection.CreateCommand();
            componentPlan.CommandText = "EXPLAIN QUERY PLAN SELECT c.component FROM rows r LEFT JOIN components c ON c.id=r.id WHERE r.id='fixture'";
            var componentSteps = new List<string>();
            using (var reader = componentPlan.ExecuteReader())
                while (reader.Read()) componentSteps.Add(reader.GetString(3));
            Assert.IsTrue(componentSteps.Any(step => step.Contains("COVERING INDEX", StringComparison.Ordinal)
                && step.Contains("(id=?)", StringComparison.Ordinal)), string.Join("; ", componentSteps));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task PresentationRowsAndCountsComeFromTheSameMutationGeneration()
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-spatial-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var index = new SpatialWorkspaceIndex(Path.Combine(root, "index.db"));
            index.Put([]);
            var writer = Task.Run(() =>
            {
                for (int batch = 0; batch < 30; batch++)
                    index.Put(Enumerable.Range(batch * 100, 100).SelectMany(i => new[]
                    { Row("blocks-" + i, i, "blocks"), Row("entities-" + i, i, "entities") }));
            });
            do
            {
                var page = await Task.Run(() => index.ReadPresentation(new("world", "layer"), 0, false));
                Assert.AreEqual(page.Total * 2, page.Global.Total);
                Assert.AreEqual(page.Total, page.UnfilteredTotal);
                if (page.Total > 0)
                {
                    Assert.AreEqual(page.Total, page.Categories["blocks"]);
                    Assert.AreEqual(page.Total, page.Categories["entities"]);
                    Assert.IsTrue(page.Rows.All(r => r.MemberIds.Length == 2));
                }
            } while (!writer.IsCompleted);
            await writer;
            var final = index.ReadPresentation(new("world", "layer"), 0, false);
            Assert.AreEqual(3000, final.Total); Assert.AreEqual(6000, final.Global.Total);
            Assert.HasCount(101, final.Rows);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CombinedReadPreservesFilteringWholeDomainAndMergeCounts()
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-spatial-summary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var index = new SpatialWorkspaceIndex(Path.Combine(root, "index.db"));
            index.Put([Row("block", 0, "blocks") with { Status = "Unresolved" },
                Row("entity", 1, "entities") with { Status = "Automatic" },
                Row("world", 2, "blocks") with { Status = "Locked", Locations = [new("world", null, null)] }]);
            var query = new SpatialWorkspaceQuery("world", "layer", Categories: ["blocks"], Group: false);
            var page = index.ReadPresentation(query, 0, true);
            Assert.AreEqual(index.Count(query), page.Total);
            Assert.AreEqual(index.WholeDomainCount(query), page.WholeDomain);
            Assert.AreEqual(index.Count(query with { Categories = null }), page.UnfilteredTotal);
            CollectionAssert.AreEqual(new[] { 0, 0, 1, 1, 0 }, page.MergeCounts);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Assert.ThrowsExactly<OperationCanceledException>(() => index.ReadPresentation(query, 0, true, canceled.Token));
            index.Put([Row("after-cancel", 3, "blocks")]);
            Assert.AreEqual(4, index.Counts().Total);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ProgressCallbackInterruptsReadAndIsRemovedBeforeTheNextWrite()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        using var cancellation = new CancellationTokenSource();
        Task cancel;
        using (new SqliteReadCancellation(connection, cancellation.Token))
        {
            cancel = Task.Run(async () => { await Task.Delay(20); cancellation.Cancel(); });
            using var query = connection.CreateCommand();
            query.CommandText = "WITH RECURSIVE n(x) AS (VALUES(0) UNION ALL SELECT x+1 FROM n WHERE x<1000000000) SELECT SUM(x) FROM n";
            var error = Assert.ThrowsExactly<SqliteException>(() => query.ExecuteScalar());
            Assert.AreEqual(9, error.SqliteErrorCode);
        }
        await cancel;
        using var write = connection.CreateCommand();
        write.CommandText = "CREATE TABLE probe(value INTEGER); INSERT INTO probe VALUES(7); SELECT value FROM probe";
        Assert.AreEqual(7L, write.ExecuteScalar());
    }

    private static SpatialWorkspaceRow Row(string id, int position, string component)
        => new(id, id, "fixture", "Modified", true,
            [new("world", "layer", new(position * 16d, 0, (position + 1d) * 16, 16))]) { Components = [component] };
}

using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SpatialQueryBatchTests
{
    [TestMethod]
    public void FilteredMembersKeepGroupSelectionAndFirstOccurrenceMergeOrder()
    {
        var rows = new[]
        {
            Row("d", 0) with { Title = "Visible D", Description = "first", Components = ["blocks", "Shared", "tail"], SourcePaths = ["C", "shared"] },
            Row("c", 0) with { Title = "hidden", Description = "first", Status = "Resolved", Components = ["entities"], SourcePaths = ["hidden"] },
            Row("b", 0) with { Title = "Visible B", Description = "second", Status = "Added", Components = ["blocks", "shared"], SourcePaths = ["shared", "B", "A"] },
            Row("a", 0) with { Title = "Visible A", Description = "first", CanSelect = false, Components = ["blocks", "Shared", "blocks"], SourcePaths = ["A", "shared"] },
            Row("elsewhere", 16) with { Components = ["entities"] }
        };
        WithIndex(rows, ["c", "elsewhere"], index =>
        {
            var categories = new SpatialWorkspaceQuery("world", "layer", Categories: ["blocks"], KnownCategories: ["blocks", "entities"]);
            var merged = index.Query(categories, 0).Single();
            Assert.AreEqual("a", merged.Id);
            Assert.AreEqual("Visible A", merged.Title);
            Assert.IsFalse(merged.CanSelect, "The first member supplies the retained row properties.");
            CollectionAssert.AreEqual(new[] { "a", "b", "d" }, merged.MemberIds);
            CollectionAssert.AreEqual(new[] { "Modified", "Added" }, merged.States);
            Assert.AreEqual("Mixed", merged.Status);
            Assert.AreEqual("first\n\nsecond", merged.Description);
            CollectionAssert.AreEqual(new[] { "blocks", "Shared", "shared", "tail" }, merged.Components);
            CollectionAssert.AreEqual(new[] { "A", "shared", "B", "C" }, merged.SourcePaths);
            Assert.IsTrue(merged.Checked, "Selection belongs to the entire location, including hidden members.");

            foreach (var query in new SpatialWorkspaceQuery[]
            {
                categories,
                new("world", "layer", Status: "Modified"),
                new("world", "layer", Search: "VISIBLE"),
                categories with { Search = "VISIBLE", Status = "Modified" }
            })
            {
                AssertQuery(index, rows, ["c", "elsewhere"], query, 0, 101);
                var individual = AssertQuery(index, rows, ["c", "elsewhere"], query with { Group = false }, 0, 101);
                Assert.IsTrue(individual.All(row => row.Checked), "Ungrouped rows still share the location's selection state.");
            }

            index.ToggleItem("c", false);
            Assert.IsFalse(index.Query(categories, 0).Single().Checked, "Selection at a different location cannot mark this group.");
        });
    }

    [TestMethod]
    public void WorldWideLocationsUnknownCategoriesAndFilesKeepTheirScopeRules()
    {
        var rows = new[]
        {
            Row("whole-world", 0) with { Locations = [new("world", null, null)], Components = [] },
            Row("whole-world-other", 0) with { Locations = [new("world", null, null)] },
            Row("whole-layer", 0) with { Locations = [new("world", "layer", null)] },
            Row("all-layers", 0) with { Locations = [new("world", null, new(0, 0, 16, 16))] },
            Row("local", 0),
            Row("other-layer", 0) with { Locations = [new("world", "elsewhere", new(0, 0, 16, 16))] },
            Row("other-world", 0) with { Locations = [new("elsewhere", "layer", null)] },
            Row("multi-a", 0) with { Locations = [new("elsewhere", "layer", new(-160, 0, -144, 16)), new("world", "layer", null)] },
            Row("multi-b", 0) with { Locations = [new("elsewhere", "layer", new(-160, 0, -144, 16)), new("world", "layer", null)] },
            Row("unknown", 16) with { Components = ["provider:new"] },
            Row("unavailable", 32) with { Status = "Unavailable", Components = ["entities"] },
            Row("unclassified", 48) with { Components = [] },
            Row("file-known", 0) with { Locations = [], SourcePaths = ["data/known.dat"] },
            Row("file-unknown", 0) with { Locations = [], Components = ["provider:new"] },
            Row("file-unavailable", 0) with { Locations = [], Status = "Unavailable", Components = ["entities"] },
            Row("file-unclassified", 0) with { Locations = [], Components = [] }
        };
        WithIndex(rows, ["whole-world", "multi-b", "file-unknown"], index =>
        {
            var spatial = new SpatialWorkspaceQuery("world", "layer");
            var result = AssertQuery(index, rows, ["whole-world", "multi-b", "file-unknown"], spatial, 0, 101);
            CollectionAssert.AreEquivalent(new[] { "whole-world", "whole-world-other", "whole-layer", "all-layers", "local", "multi-a", "multi-b", "unknown", "unavailable", "unclassified" },
                result.SelectMany(row => row.MemberIds).ToArray());
            Assert.AreEqual(2, result.Count(row => row.Id.StartsWith("multi-", StringComparison.Ordinal)), "Identical multiple-location rows remain separate groups.");
            Assert.IsFalse(result.Single(row => row.Id == "whole-world-other").Checked, "Unbounded locations keep row identity for grouping and selection.");
            AssertQuery(index, rows, ["whole-world", "multi-b", "file-unknown"], spatial with { Layer = "elsewhere" }, 0, 101);

            var categoryQuery = spatial with { Categories = [], KnownCategories = ["blocks", "entities"] };
            var categories = AssertQuery(index, rows, ["whole-world", "multi-b", "file-unknown"], categoryQuery, 0, 101);
            CollectionAssert.AreEquivalent(new[] { "whole-world", "unknown", "unavailable", "unclassified" }, categories.SelectMany(row => row.MemberIds).ToArray());

            var files = AssertQuery(index, rows, ["whole-world", "multi-b", "file-unknown"], categoryQuery with { World = "absent", Layer = "absent", Files = true }, 0, 101);
            CollectionAssert.AreEquivalent(new[] { "file-unknown", "file-unavailable", "file-unclassified" }, files.SelectMany(row => row.MemberIds).ToArray());
            Assert.IsTrue(files.Single(row => row.Id == "file-unknown").Checked);
        });
    }

    [TestMethod]
    public void NumericAndTieOrderSurvivePageBoundariesAndRequestsLargerThanOneBatch()
    {
        var rows = Enumerable.Range(0, 243).SelectMany(i =>
        {
            var x = (i / 4 - 30) * 16;
            var y = i % 4 / 2 * 16;
            var first = Row($"primary-{i:000}", x) with
            {
                Title = "tie", Description = "same", SourcePaths = ["same"],
                Locations = [new("world", "layer", new(x, y, x + 16 + i % 2, y + 16))]
            };
            return i % 3 == 0
                ? new[] { first with { Id = $"secondary-{i:000}", Status = "Added" }, first }
                : new[] { first };
        }).Reverse().ToArray();
        var selected = Enumerable.Range(0, 243).Where(i => i % 17 == 0).Select(i => $"primary-{i:000}").ToArray();
        WithIndex(rows, selected, index =>
        {
            foreach (var group in new[] { true, false })
            {
                var query = new SpatialWorkspaceQuery("world", "layer", Group: group);
                var firstPage = AssertQuery(index, rows, selected, query, 0, 100);
                var secondPage = AssertQuery(index, rows, selected, query, 100, 100);
                var whole = AssertQuery(index, rows, selected, query, 0, 1000);
                CollectionAssert.AreEqual(whole.Take(200).Select(row => row.Id).ToArray(), firstPage.Concat(secondPage).Select(row => row.Id).ToArray());
                AssertQuery(index, rows, selected, query, 99, 101);
                AssertQuery(index, rows, selected, query, 17, 205);
                AssertQuery(index, rows, selected, query, 0, 0);
                AssertQuery(index, rows, selected, query, whole.Count, 101);
                AssertQuery(index, rows, selected, query, whole.Count + 100, 205);
            }
        });
    }

    [TestMethod]
    public void FixedSeedCorpusMatchesReferenceAcrossFiltersAndGroupingModes()
    {
        var random = new Random(184729);
        var statuses = new[] { "Modified", "Added", "Deleted", "Unresolved", "Resolved", "Unavailable" };
        var components = new[] { "blocks", "entities", "biomes", "provider:new" };
        var rows = Enumerable.Range(0, 360).Select(i =>
        {
            var world = i % 11 == 0 ? "elsewhere" : "world";
            var layer = i % 7 == 0 ? null : i % 13 == 0 ? "elsewhere" : "layer";
            var x = random.Next(-12, 13) * 16;
            var y = random.Next(-4, 5) * 16;
            IReadOnlyList<SpatialSubjectLocation> locations = i % 19 == 0 ? []
                : i % 17 == 0 ? [new(world, layer, null)]
                : i % 23 == 0 ? [new("elsewhere", "elsewhere", new(-320, -16, -304, 0)), new(world, layer, new(x, y, x + 16, y + 16))]
                : [new(world, layer, new(x, y, x + 16, y + 16))];
            return new SpatialWorkspaceRow($"row-{i:000}", $"title-{random.Next(6)}", $"detail-{random.Next(9)}", statuses[random.Next(statuses.Length)], i % 9 != 0, locations)
            {
                Components = i % 8 == 0 ? [] : Enumerable.Range(0, random.Next(1, 4)).Select(_ => components[random.Next(components.Length)]).ToArray(),
                SourcePaths = i % 5 == 0 ? ["data/NEEDLE.dat", $"data/{i % 4}.dat", "data/NEEDLE.dat"] : [$"data/{i % 4}.dat"],
                MemberIds = ["stale-member"], States = ["stale-state"], Checked = true
            };
        }).OrderBy(_ => random.Next()).ToArray();
        var selected = rows.Where(row => int.Parse(row.Id.AsSpan(4)) % 7 == 0).Select(row => row.Id).ToArray();
        var queries = new SpatialWorkspaceQuery[]
        {
            new SpatialWorkspaceQuery("world", "layer"),
            new("world", "layer", Search: "needle"),
            new("world", "layer", Status: "Modified"),
            new("world", "layer", Search: "detail-3", Status: "Unresolved"),
            new("world", "layer", Categories: ["blocks"], KnownCategories: ["blocks", "entities", "biomes"]),
            new("world", "layer", Categories: [], KnownCategories: ["blocks", "entities", "biomes"]),
            new("world", "layer", Categories: []),
            new("world", "layer", Categories: ["entities"], KnownCategories: []),
            new("world", "layer", Categories: ["biomes"], KnownCategories: ["blocks", "entities", "biomes"], Search: "needle"),
            new("world", "layer", Files: true),
            new("world", "layer", Files: true, Search: "needle", Categories: ["blocks"], KnownCategories: ["blocks", "entities", "biomes"]),
            new("elsewhere", "layer"),
            new("world", "elsewhere"),
            new("absent", "absent")
        };
        WithIndex(rows, selected, index =>
        {
            foreach (var query in queries)
            foreach (var group in new[] { true, false })
            {
                var grouped = query with { Group = group };
                AssertQuery(index, rows, selected, grouped, 0, 101);
                AssertQuery(index, rows, selected, grouped, 7, 105);
                AssertQuery(index, rows, selected, grouped, 100, 37);
                AssertQuery(index, rows, selected, grouped, 10000, 101);
            }
        });
    }

    private static IReadOnlyList<SpatialWorkspaceRow> AssertQuery(SpatialWorkspaceIndex index, SpatialWorkspaceRow[] rows, string[] selected,
        SpatialWorkspaceQuery query, int offset, int count)
    {
        var expected = ReferenceQuery(rows, selected, query).ToArray();
        Assert.AreEqual(expected.Length, index.Count(query), $"Count: {JsonSerializer.Serialize(query)}");
        var page = expected.Skip(offset).Take(count).ToArray();
        var actual = index.Query(query, offset, count);
        var context = $"Query: {JsonSerializer.Serialize(query)}, offset={offset}, count={count}";
        Assert.HasCount(page.Length, actual, context);
        for (var i = 0; i < page.Length; i++)
        {
            var wanted = page[i]; var found = actual[i]; var message = $"{context}, row={i}";
            Assert.AreEqual(wanted.Id, found.Id, message);
            Assert.AreEqual(wanted.Title, found.Title, message);
            Assert.AreEqual(wanted.Description, found.Description, message);
            Assert.AreEqual(wanted.Status, found.Status, message);
            Assert.AreEqual(wanted.CanSelect, found.CanSelect, message);
            Assert.AreEqual(wanted.Checked, found.Checked, message);
            CollectionAssert.AreEqual(wanted.Locations.ToArray(), found.Locations.ToArray(), message);
            CollectionAssert.AreEqual(wanted.MemberIds, found.MemberIds, message);
            CollectionAssert.AreEqual(wanted.States, found.States, message);
            CollectionAssert.AreEqual(wanted.Components, found.Components, message);
            CollectionAssert.AreEqual(wanted.SourcePaths, found.SourcePaths, message);
        }
        return actual;
    }

    // This oracle operates on input objects and contains no database queries. Fixtures use ASCII
    // text so ordinal case-insensitive matching has the same alphabet as SQLite's built-in lower().
    private static IEnumerable<SpatialWorkspaceRow> ReferenceQuery(SpatialWorkspaceRow[] rows, string[] requestedSelection, SpatialWorkspaceQuery query)
    {
        var selection = requestedSelection.ToHashSet(StringComparer.Ordinal);
        var selectedLocations = rows.Where(row => row.CanSelect && selection.Contains(row.Id)).Select(LocationIdentity).ToHashSet(StringComparer.Ordinal);
        var groups = rows.Where(row => Matches(row, query))
            .GroupBy(row => query.Group ? LocationIdentity(row) : row.Id, StringComparer.Ordinal)
            .Select(group => new { group.Key, Members = group.OrderBy(row => row.Id, StringComparer.Ordinal).ToArray() })
            .OrderBy(group => group.Members.Select(row => row.Locations.FirstOrDefault()?.Bounds?.MinX).Min())
            .ThenBy(group => group.Members.Select(row => row.Locations.FirstOrDefault()?.Bounds?.MinY).Min())
            .ThenBy(group => group.Members.Select(SearchableText).OrderBy(text => text, StringComparer.Ordinal).First(), StringComparer.Ordinal)
            .ThenBy(group => group.Key, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var first = group.Members[0];
            var states = group.Members.Select(row => row.Status).Distinct(StringComparer.Ordinal).ToArray();
            yield return first with
            {
                MemberIds = group.Members.Select(row => row.Id).ToArray(),
                States = states,
                Status = states.Length == 1 ? states[0] : "Mixed",
                Description = string.Join("\n\n", group.Members.Select(row => row.Description).Distinct(StringComparer.Ordinal)),
                Components = group.Members.SelectMany(row => row.Components).Distinct(StringComparer.Ordinal).ToArray(),
                SourcePaths = group.Members.SelectMany(row => row.SourcePaths).Distinct(StringComparer.Ordinal).ToArray(),
                Checked = selectedLocations.Contains(LocationIdentity(first))
            };
        }
    }

    private static bool Matches(SpatialWorkspaceRow row, SpatialWorkspaceQuery query)
    {
        if (!SearchableText(row).Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || query.Status.Length > 0 && row.Status != query.Status) return false;
        if (query.Files ? row.Locations.Count != 0
            : !row.Locations.Any(location => location.WorldId == query.World && (location.LayerId is null || location.LayerId == query.Layer))) return false;
        return query.Categories is null || row.Status == "Unavailable" || row.Components.Length == 0
            || row.Components.Any(component => query.Categories.Contains(component, StringComparer.Ordinal))
            || query.KnownCategories is { Length: > 0 } known && row.Components.Any(component => !known.Contains(component, StringComparer.Ordinal));
    }

    private static string SearchableText(SpatialWorkspaceRow row) => row.Title + "\n" + row.Description + "\n" + string.Join("\n", row.SourcePaths);

    private static string LocationIdentity(SpatialWorkspaceRow row) => row.Locations.Count == 1 && row.Locations[0].Bounds is not null
        ? JsonSerializer.Serialize(row.Locations[0]) : row.Id;

    private static SpatialWorkspaceRow Row(string id, int x) => new(id, id, "detail", "Modified", true,
        [new("world", "layer", new(x, 0, x + 16, 16))]) { Components = ["blocks"] };

    private static void WithIndex(SpatialWorkspaceRow[] rows, string[] selected, Action<SpatialWorkspaceIndex> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-spatial-query-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var index = new SpatialWorkspaceIndex(Path.Combine(root, "index.db"));
            index.Put(rows);
            foreach (var id in selected) index.ToggleItem(id, true);
            test(index);
        }
        finally { Directory.Delete(root, true); }
    }
}

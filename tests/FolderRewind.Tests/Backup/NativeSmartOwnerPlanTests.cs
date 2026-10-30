using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
[TestCategory("ExternalTool")]
public sealed class NativeSmartOwnerPlanTests
{
    [TestMethod]
    public async Task DeletedLastChildFallsBackAndRetainsItsEmptyParent()
    {
        await using var f = new HistoryChainRewriteFixture(); await f.InitializeAsync();
        var node = await f.AddAsync(null, dir => { Directory.CreateDirectory(Path.Combine(dir, "parent")); File.WriteAllText(Path.Combine(dir, "parent", "last"), "old"); });
        node = await f.AddAsync(node, dir => File.Delete(Path.Combine(dir, "parent", "last")));
        var graph = await f.History.Query.GetAllRepresentationsAsync();
        var env = new RepresentationEnvironment((await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries, [], []);
        var staging = Path.Combine(f.Root, "fallback");
        var diagnostics = HistoryOperationDiagnostics.Begin("deleted-last-child");
        using (var verification = new ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced))
        using (diagnostics)
            await f.Engine.MaterializeAsync(node.Representation.RepresentationId, graph, env, MaterializationFidelity.Exact, staging, default, verification);
        Assert.AreEqual("final-empty-directory", diagnostics.Summary!.Observations["smart-fallback"]);
        Assert.IsTrue(Directory.Exists(Path.Combine(staging, "parent")));
        Assert.IsFalse(File.Exists(Path.Combine(staging, "parent", "last")));
    }
    [TestMethod]
    [DataRow(RestoreArchivePreflightMode.Balanced, false)]
    [DataRow(RestoreArchivePreflightMode.Strict, true)]
    public async Task RealSelectiveExtractionMatchesSequentialTreeAndAvoidsObsoleteWrites(RestoreArchivePreflightMode mode, bool encrypted)
    {
        await using var f = new HistoryChainRewriteFixture(encrypted); await f.InitializeAsync();
        var bytes = new byte[1024 * 1024]; new Random(719).NextBytes(bytes);
        var node = await f.AddAsync(null, dir => {
            Directory.CreateDirectory(Path.Combine(dir, "中文目录"));
            File.WriteAllBytes(Path.Combine(dir, "中文目录", "[world].bin"), bytes);
            File.WriteAllText(Path.Combine(dir, "stable.txt"), "spaces preserved");
            File.WriteAllText(Path.Combine(dir, "deleted.txt"), "obsolete");
        });
        for (int i = 1; i <= 5; i++)
        {
            bytes[i] ^= 1;
            node = await f.AddAsync(node, dir => {
                File.WriteAllBytes(Path.Combine(dir, "中文目录", "[world].bin"), bytes);
                if (i == 5) File.Delete(Path.Combine(dir, "deleted.txt"));
            });
        }
        var graph = await f.History.Query.GetAllRepresentationsAsync();
        var env = new RepresentationEnvironment((await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries, [], []);
        var output = Path.Combine(f.Root, "selective");
        var diagnostics = HistoryOperationDiagnostics.Begin("selective-smart");
        using (var verification = new ArchiveRestoreVerificationContext(mode))
        using (diagnostics)
            await f.Engine.MaterializeAsync(node.Representation.RepresentationId, graph, env, MaterializationFidelity.Exact,
                output, default, verification);
        Assert.IsTrue(node.Tree.EquivalentTo(await HistoryRewriteTree.ReadAsync(output)));
        var stats = diagnostics.Summary!;
        Assert.AreEqual(2L, stats.Counters["7z-x"]);
        Assert.AreEqual(mode == RestoreArchivePreflightMode.Strict ? 6L : 0L, stats.Counters.GetValueOrDefault("7z-t"));
        Assert.AreEqual(6L, stats.Counters["7z-l"]);
        Assert.AreEqual(1024 * 1024 + "spaces preserved".Length, stats.Counters["uncompressed-bytes-written-estimate"]);
    }

    [TestMethod]
    public async Task ExplicitEmptyDirectoryFallsBackAndIsPreservedInStaging()
    {
        await using var f = new HistoryChainRewriteFixture(); await f.InitializeAsync();
        var node = await f.AddAsync(null, dir => { Directory.CreateDirectory(Path.Combine(dir, "empty")); File.WriteAllText(Path.Combine(dir, "file"), "old"); });
        node = await f.AddAsync(node, dir => File.WriteAllText(Path.Combine(dir, "file"), "new"));
        var graph = await f.History.Query.GetAllRepresentationsAsync();
        var env = new RepresentationEnvironment((await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries, [], []);
        var staging = Path.Combine(f.Root, "fallback");
        var diagnostics = HistoryOperationDiagnostics.Begin("fallback-smart");
        using (var verification = new ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced))
        using (diagnostics)
            await f.Engine.MaterializeAsync(node.Representation.RepresentationId, graph, env, MaterializationFidelity.Exact,
                staging, default, verification);
        Assert.AreEqual("explicit-empty-directory", diagnostics.Summary!.Observations["smart-fallback"]);
        Assert.IsTrue(Directory.Exists(Path.Combine(staging, "empty")));
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(staging, "file")));
    }

    [TestMethod]
    public async Task LeadingSpaceNameFallsBackWithoutChangingItsName()
    {
        await using var f = new HistoryChainRewriteFixture(); await f.InitializeAsync();
        var node = await f.AddAsync(null, dir => File.WriteAllText(Path.Combine(dir, " leading.txt"), "old"));
        node = await f.AddAsync(node, dir => File.WriteAllText(Path.Combine(dir, " leading.txt"), "new"));
        var graph = await f.History.Query.GetAllRepresentationsAsync();
        var env = new RepresentationEnvironment((await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries, [], []);
        var staging = Path.Combine(f.Root, "fallback");
        var diagnostics = HistoryOperationDiagnostics.Begin("whitespace-smart");
        using (var verification = new ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced))
        using (diagnostics)
            await f.Engine.MaterializeAsync(node.Representation.RepresentationId, graph, env, MaterializationFidelity.Exact, staging, default, verification);
        Assert.AreEqual("listfile-leading-whitespace", diagnostics.Summary!.Observations["smart-fallback"]);
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(staging, " leading.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(staging, "leading.txt")));
    }
}

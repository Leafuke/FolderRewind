using System.Collections.Immutable;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class RestorePlanningOptimizationTests
{
    [TestMethod]
    public void PathIndexPreservesSpellingAndSegmentBoundariesAcrossSubtreeDeletion()
    {
        var index = new RestorePathIndex<int>();
        index.Set("Dir/A", 1); index.Set("DIR/a", 2); index.Set("dir/B/leaf", 3); index.Set("dir2/a", 4);
        Assert.AreEqual(3, index.Count);
        Assert.IsTrue(index.Contains("dir/A")); Assert.IsFalse(index.Contains("di"));
        Assert.IsTrue(index.HasAncestor("DIR/A/child")); Assert.IsFalse(index.HasAncestor("dir/ab"));
        Assert.IsTrue(index.HasDescendants("dIR")); Assert.IsFalse(index.HasDescendants("di"));
        var removed = index.RemoveSubtree("DiR").OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { "DIR/a", "dir/B/leaf" }, removed.Select(p => p.Key).ToArray());
        Assert.AreEqual(2, removed[0].Value); Assert.AreEqual(1, index.Count);
        Assert.IsFalse(index.HasDescendants("dir")); Assert.IsTrue(index.Contains("DIR2/A"));
        index.Set("dir/new", 5); Assert.AreEqual(2, index.Count);
        Assert.HasCount(2, index.RemoveSubtree("")); Assert.AreEqual(0, index.Count);
    }

    [TestMethod]
    public void IndexedSmartPlanMatchesFrozenAlgorithmAcrossGeneratedChains()
    {
        string[] paths = ["a", "A", "a/b", "a/b/c", "ab/c", "ab/d", "中文/存档", "中文/其他",
            "empty/last", "stable", " leading", ".restore-marker/empty", "prefix/name", "prefix2/name"];
        var random = new Random(719);
        for (int sample = 0; sample < 700; sample++)
        {
            var inputs = new List<ArchiveMaterializationInput>(); var listings = new List<ArchiveSafeListing>();
            for (int layer = 0; layer < 1 + sample % 6; layer++)
            {
                var files = new Dictionary<string, ArchiveFileListingEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in paths.Where(_ => random.Next(4) == 0)) files[path] = new(path, random.Next(64));
                var directories = new[] { "a", "empty", "中文", "prefix", ".restore-marker" }.Where(_ => random.Next(6) == 0).ToArray();
                var deleted = paths.Where(_ => random.Next(9) == 0).ToArray();
                var metadata = ImmutableDictionary<string, string>.Empty;
                if (layer > 0 || sample % 20 == 0) metadata = metadata.SetItem("deletedFiles", string.Join('\n', deleted));
                var rep = new VersionRepresentation(RepresentationId.New(), VersionId.New(),
                    layer == 0 ? RepresentationKind.CoreFull : RepresentationKind.CoreSmartDelta, "7z",
                    layer == 0 ? [] : [inputs[^1].Representation.RepresentationId], MaterializationFidelity.Exact, null, null, metadata);
                inputs.Add(new(rep, "unused-" + layer)); listings.Add(new(files, directories));
            }
            var expected = NativeSmartOwnerPlannerReference.TryPlan(inputs, listings, ".restore-marker", out var expectedFallback);
            var actual = NativeSmartOwnerPlanner.TryPlan(inputs, listings, ".restore-marker", out var actualFallback);
            Assert.AreEqual(expectedFallback, actualFallback, $"sample {sample}");
            Assert.AreEqual(expected is null, actual is null, $"sample {sample}");
            if (expected is null) continue;
            CollectionAssert.AreEqual(expected.Files.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),
                actual!.Files.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(), $"sample {sample}");
        }
    }

    [TestMethod]
    public async Task MultiSourcePreparationLoadsEnvironmentOnceAndLaterOperationReloads()
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        int loads = 0;
        var restore = new HistoryRestoreService(f.History, new RepresentationRuntime([new CoreArchiveRepresentationHandler(f.Archives.Inner)]),
            async token => { loads++; return new RepresentationEnvironment((await f.History.LocalReplicaCatalogStore.LoadAsync(token)).Value!.Entries, [], []); },
            new FileSystemHistoryRestoreMutationBackend()) { UseTargetVolumeWorkspaces = true };
        var selections = new Dictionary<SourceId, VersionId> { [f.Source] = f.Before.GetSourceState(f.Source).BaseVersionId!.Value,
            [f.Other] = f.Before.GetSourceState(f.Other).BaseVersionId!.Value };
        var bindings = new[] { new HistoryRestoreSourceBinding(f.Source, f.Target), new HistoryRestoreSourceBinding(f.Other, f.OtherTarget) };
        var result = await restore.RestoreVersionsAsync(selections, bindings, f.Before, HistoryRestoreApplyMode.Clean);
        Assert.IsTrue(result.Succeeded, result.Diagnostic); Assert.AreEqual(1, loads);
        var workspace = (await f.History.WorkspaceStore.LoadAsync()).Value!;
        result = await restore.RestoreVersionsAsync(selections, bindings, workspace, HistoryRestoreApplyMode.Clean);
        Assert.IsTrue(result.Succeeded, result.Diagnostic); Assert.AreEqual(2, loads);
        Assert.AreEqual("ours-a", File.ReadAllText(Path.Combine(f.Target, "a.txt")));
        Assert.AreEqual("untouched", File.ReadAllText(Path.Combine(f.OtherTarget, "keep.txt")));
    }

    [TestMethod]
    public async Task BudgetAndMaterializationShareOwnerPlanIncludingFallback()
    {
        await using var f = new HistoryChainRewriteFixture(); await f.InitializeAsync();
        var node = await f.AddAsync(null, path => { Directory.CreateDirectory(Path.Combine(path, "empty")); File.WriteAllText(Path.Combine(path, "a"), "first"); });
        node = await f.AddAsync(node, path => File.WriteAllText(Path.Combine(path, "a"), "second"));
        using var verification = new ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced);
        var graph = await f.History.Query.GetAllRepresentationsAsync();
        var environment = new RepresentationEnvironment((await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries, [], []);
        var diagnostic = HistoryOperationDiagnostics.Begin("owner-reuse");
        var staging = Path.Combine(f.Root, "materialized");
        using (diagnostic) await f.Engine.MaterializeAsync(node.Representation.RepresentationId, graph, environment,
            MaterializationFidelity.Exact, staging, default, verification);
        Assert.AreEqual(1L, diagnostic.Summary!.Counters["smart-owner-plan-builds"]);
        Assert.IsGreaterThanOrEqualTo(1L, diagnostic.Summary.Counters["smart-owner-plan-reuses"]);
        Assert.AreEqual("explicit-empty-directory", diagnostic.Summary.Observations["smart-fallback"]);
        Assert.IsTrue(Directory.Exists(Path.Combine(staging, "empty")));
        Assert.AreEqual("second", File.ReadAllText(Path.Combine(staging, "a")));
    }

    [TestMethod]
    public async Task PreparationInvalidationAndScopeExitForceFreshInputs()
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        int loads = 0;
        using var verification = new ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced);
        var restore = new HistoryRestoreService(f.History, new RepresentationRuntime([new CoreArchiveRepresentationHandler(f.Archives.Inner)]),
            async token => { loads++; return new RepresentationEnvironment((await f.History.LocalReplicaCatalogStore.LoadAsync(token)).Value!.Entries, [], []); },
            new FileSystemHistoryRestoreMutationBackend()) { UseTargetVolumeWorkspaces = true, VerificationContext = verification };
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(f.Config);
        var binding = new HistoryRestoreSourceBinding(f.Source, f.Target);
        using var session = restore.CreatePreparationSession([binding], f.Before);
        using (restore.EnterPreparationInputs(operation))
        {
            var version = f.Ours.NewVersions.Single();
            await restore.ReservePreparationSpaceAsync([(version, binding, MaterializationFidelity.Exact)], session, default);
            await restore.PrepareSourceAsync(version, binding, MaterializationFidelity.Exact, HistoryRestoreApplyMode.Clean, default, session);
            Assert.AreEqual(1, loads);
            restore.InvalidatePreparationInputs(); // Same boundary used before a safety snapshot may commit new history.
            await restore.ReservePreparationSpaceAsync([(version, binding, MaterializationFidelity.Exact)], session, default);
            Assert.AreEqual(2, loads);
        }
        await restore.ReservePreparationSpaceAsync([(f.Ours.NewVersions.Single(), binding, MaterializationFidelity.Exact)], session, default);
        Assert.AreEqual(3, loads);
        Assert.AreEqual("ours-a", File.ReadAllText(Path.Combine(f.Target, "a.txt")));
    }

    [TestMethod]
    public async Task PreparationEnvironmentReceivesTheAlreadyLoadedGraph()
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        int loads = 0;
        var restore = new HistoryRestoreService(f.History, new RepresentationRuntime([new CoreArchiveRepresentationHandler(f.Archives.Inner)]),
            _ => throw new AssertFailedException("Preparation must use the graph-aware environment factory."),
            new FileSystemHistoryRestoreMutationBackend())
        {
            UseTargetVolumeWorkspaces = true,
            PreparationEnvironmentFactory = async (graph, token) => {
                loads++;
                Assert.IsTrue(graph.Any(r => r.VersionId == f.Ours.NewVersions.Single().VersionId));
                return new RepresentationEnvironment((await f.History.LocalReplicaCatalogStore.LoadAsync(token)).Value!.Entries, [], []);
            }
        };
        var result = await restore.RestoreVersionAsync(f.Ours.NewVersions.Single().VersionId, new(f.Source, f.Target), f.Before, HistoryRestoreApplyMode.Clean);
        Assert.IsTrue(result.Succeeded, result.Diagnostic); Assert.AreEqual(1, loads);
    }
}

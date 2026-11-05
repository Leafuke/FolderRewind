using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using FolderRewind.Models;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryChainRewriteOptimizationTests
{
    [TestMethod]
    public void NetworkAndRelativePathsDoNotEnableLocalVerificationCache()
    {
        Assert.IsNull(HistoryChainRewriteVerification.Create([@"\\server\share\backup.7z"]));
        Assert.IsNull(HistoryChainRewriteVerification.Create([@"\\?\UNC\server\share\backup.7z"]));
        Assert.IsNull(HistoryChainRewriteVerification.Create(["relative.7z"]));
    }

    [TestMethod]
    public void LocalLookingDirectoryAliasesKeepTheLegacyVerificationPath()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Requires Windows symbolic-link support.");
        var root = Path.Combine(Path.GetTempPath(), "retention-alias-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var alias = Path.Combine(root, "alias");
        Directory.CreateDirectory(target);
        try
        {
            File.WriteAllText(Path.Combine(target, "payload.7z"), "fixture");
            try { Directory.CreateSymbolicLink(alias, target); }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException)
            { Assert.Inconclusive("Symbolic-link creation unavailable: " + error.Message); }
            Assert.IsNull(HistoryChainRewriteVerification.Create([Path.Combine(alias, "payload.7z")]));
            Assert.IsNull(HistoryChainRewriteVerification.Create([Path.Combine(alias, "not-created-output")]));
        }
        finally
        {
            if (Directory.Exists(alias)) Directory.Delete(alias);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task VerificationOnlyReusesStrictEvidencePreservesLegacyMaterializationAndReleasesGuards()
    {
        var root = Path.Combine(Path.GetTempPath(), "retention-context-" + Guid.NewGuid().ToString("N"));
        var renamed = root + "-renamed";
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "payload.7z");
        File.WriteAllText(path, "fake backend payload");
        try
        {
            var representation = Representation();
            var backend = new FakeArchiveBackend();
            using (var context = HistoryChainRewriteVerification.Create([path]))
            {
                if (context is null) Assert.Inconclusive("Requires local Windows NTFS for protected verification.");
                Assert.AreEqual(RestoreArchivePreflightMode.Strict, context.Mode);
                Assert.IsFalse(context.UseRestoreMaterialization);
                Assert.IsTrue((await context.VerifyAsync(backend, representation, path, default)).Success);
                Assert.IsTrue((await context.VerifyAsync(backend, representation, path, default)).Success);
                Assert.AreEqual(1, backend.Verifications);
                Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None).Dispose());
                var tooSmall = new ArtifactSpaceBudget(_ => new("test-volume", 8195));
                Assert.Throws<IOException>(() => context.ReserveLegacyMaterializationSpace([new(representation, path)], root, tooSmall));
                var enough = new ArtifactSpaceBudget(_ => new("test-volume", 8196));
                using (context.ReserveLegacyMaterializationSpace([new(representation, path)], root, enough)) { }
                using (context.ReserveLegacyMaterializationSpace([new(representation, path)], root, enough)) { }
                var emptyRepresentation = Representation();
                Assert.IsTrue((await context.VerifyAsync(new FakeArchiveBackend { EmptyListing = true }, emptyRepresentation, path, default)).Success);
                Assert.Throws<IOException>(() => context.ReserveLegacyMaterializationSpace([new(emptyRepresentation, path)], root,
                    new ArtifactSpaceBudget(_ => new("empty-volume", 4095))));
                using (context.ReserveLegacyMaterializationSpace([new(emptyRepresentation, path)], root,
                    new ArtifactSpaceBudget(_ => new("empty-volume", 4096)))) { }
                var nestedRepresentation = Representation();
                Assert.IsTrue((await context.VerifyAsync(new FakeArchiveBackend { NestedListing = true }, nestedRepresentation, path, default)).Success);
                Assert.Throws<IOException>(() => context.ReserveLegacyMaterializationSpace([new(nestedRepresentation, path)], root,
                    new ArtifactSpaceBudget(_ => new("nested-volume", 20483))));
                using (context.ReserveLegacyMaterializationSpace([new(nestedRepresentation, path)], root,
                    new ArtifactSpaceBudget(_ => new("nested-volume", 20484)))) { }
                var changed = new VersionRepresentation(representation.RepresentationId, representation.VersionId,
                    representation.Kind, representation.Format, [], representation.Fidelity, null, null,
                    new Dictionary<string, string> { ["changed"] = "binding" });
                await Assert.ThrowsAsync<IOException>(async () => await context.VerifyAsync(backend, changed, path, default));

                var environment = new RepresentationEnvironment([new(representation.RepresentationId, LocalReplicaId.New(),
                    LocalReplicaLocator.ControlledAbsolute(path), DateTimeOffset.UtcNow)], [], []);
                var engine = new RepresentationRuntime([new CoreArchiveRepresentationHandler(backend)]);
                await engine.MaterializeAsync(representation.RepresentationId, [representation], environment,
                    MaterializationFidelity.Exact, Path.Combine(root, "staging"), default, context);
                Assert.AreEqual(1, backend.LegacyMaterializations);
                Assert.AreEqual(0, backend.RestoreMaterializations);
                Assert.AreEqual(1, backend.Verifications);
            }
            using (File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None)) { }
            Directory.Move(root, renamed);
            Assert.IsTrue(Directory.Exists(renamed));
            Assert.IsFalse(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(renamed)) Directory.Delete(renamed, recursive: true);
        }
    }

    [TestMethod]
    public async Task FailedReplicaSuppliesNoCachedEvidenceAndMissingListingKeepsLegacyBudgetPolicy()
    {
        var root = Path.Combine(Path.GetTempPath(), "retention-replicas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var bad = Path.Combine(root, "bad.7z"); var good = Path.Combine(root, "good.7z");
        File.WriteAllText(bad, "bad"); File.WriteAllText(good, "good");
        try
        {
            using var context = HistoryChainRewriteVerification.Create([bad, good]);
            if (context is null) Assert.Inconclusive("Requires local Windows NTFS for protected verification.");
            var representation = Representation();
            var backend = new FakeArchiveBackend { SupplyListing = false };
            Assert.IsFalse((await context.VerifyAsync(backend, representation, bad, default)).Success);
            using (File.Open(bad, FileMode.Open, FileAccess.Write, FileShare.None)) { }
            Assert.IsTrue((await context.VerifyAsync(backend, representation, good, default)).Success);
            Assert.IsNull(context.ReserveLegacyMaterializationSpace([new(representation, good)], root,
                new ArtifactSpaceBudget(_ => throw new AssertFailedException("Unknown listing must not guess a capacity reservation."))));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod, TestCategory("ExternalTool")]
    public async Task ForkRewriteReleasesFinishedTreesAndPreparedDisposalReleasesArchiveLeases()
    {
        await using var f = new HistoryChainRewriteFixture(); await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllBytes(Path.Combine(p, "base.bin"), new byte[10000]));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        var c = await f.AddAsync(b, p => File.WriteAllText(Path.Combine(p, "c.txt"), "C"));
        var d = await f.AddAsync(c, p => File.WriteAllText(Path.Combine(p, "d.txt"), "D"));
        var fork = await f.AddAsync(b, p => File.WriteAllText(Path.Combine(p, "fork.txt"), "fork"));
        var plan = await f.Planner.PlanAsync(f.Delete(b));
        var paths = new HistoryChainRewriteJournalStore(f.History, backupRoot: f.BackupRoot);
        await using (var prepared = await f.Executor.PrepareAsync(plan))
        {
            Assert.IsEmpty(Directory.EnumerateFiles(paths.WorkRoot(plan.OperationId), "*", SearchOption.AllDirectories));
            Assert.IsTrue((await f.Executor.CommitAsync(prepared)).Committed);
        }
        foreach (var entry in (await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries)
            using (File.Open(entry.Locator.AbsolutePath, FileMode.Open, FileAccess.Write, FileShare.None)) { }
        await f.AssertRestoresAsync(a, c, d, fork);
    }

    [TestMethod, TestCategory("ExternalTool")]
    public async Task CancellationAfterExactComparisonCleansOwnedTreesAndReleasesInputs()
    {
        await using var f = new HistoryChainRewriteFixture(); await f.InitializeAsync();
        var a = await f.AddAsync(null, p => File.WriteAllBytes(Path.Combine(p, "base.bin"), new byte[10000]));
        var b = await f.AddAsync(a, p => File.WriteAllText(Path.Combine(p, "b.txt"), "B"));
        var c = await f.AddAsync(b, p => File.WriteAllText(Path.Combine(p, "c.txt"), "C"));
        var d = await f.AddAsync(c, p => File.WriteAllText(Path.Combine(p, "d.txt"), "D"));
        using var cancelled = new CancellationTokenSource();
        var executor = new HistoryChainRewriteExecutor(f.History, f.Engine, f.Archive,
            stage => { if (stage == "replacement-verified") cancelled.Cancel(); });
        var plan = await f.Planner.PlanAsync(f.Delete(b));
        await Assert.ThrowsAsync<OperationCanceledException>(() => executor.PrepareAsync(plan, token: cancelled.Token));
        var paths = new HistoryChainRewriteJournalStore(f.History, backupRoot: f.BackupRoot);
        Assert.IsFalse(Directory.Exists(paths.WorkRoot(plan.OperationId)));
        foreach (var node in f.Nodes)
            using (File.Open(node.Entry.Locator.AbsolutePath, FileMode.Open, FileAccess.Write, FileShare.None)) { }
        await f.AssertRestoresAsync(a, b, c, d);
    }

    [TestMethod, TestCategory("ExternalTool")]
    public async Task WorkSubtreeCleanupCannotDeleteItsRootPayloadsOrAdjacentDirectory()
    {
        await using var f = new HistoryChainRewriteFixture(); await f.InitializeAsync();
        var paths = new HistoryChainRewriteJournalStore(f.History, backupRoot: f.BackupRoot);
        var id = HistoryTransactionId.New();
        var work = paths.WorkRoot(id);
        var payloads = paths.PayloadRoot(id);
        var sibling = work + "-other";
        foreach (var directory in new[] { work, payloads, sibling })
        { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "keep"), "untouched"); }
        foreach (var rejected in new[] { work, work + Path.DirectorySeparatorChar, Path.Combine(work, "."), payloads, sibling, Path.Combine(work, "..", "payloads") })
            Assert.ThrowsExactly<InvalidDataException>(() => paths.CleanupWorkSubtree(id, rejected));
        foreach (var directory in new[] { work, payloads, sibling }) Assert.IsTrue(File.Exists(Path.Combine(directory, "keep")));
        var owned = Path.Combine(work, "verify", "completed");
        Directory.CreateDirectory(owned); File.WriteAllText(Path.Combine(owned, "scratch"), "remove");
        paths.CleanupWorkSubtree(id, owned);
        Assert.IsFalse(Directory.Exists(owned));
    }

    private static VersionRepresentation Representation() => new(RepresentationId.New(), VersionId.New(),
        RepresentationKind.CoreFull, "test", [], MaterializationFidelity.Exact, null, null, null);

    private sealed class FakeArchiveBackend : IArchiveRepresentationBackend, IArchiveRestorePreflightBackend
    {
        internal int Verifications, LegacyMaterializations, RestoreMaterializations;
        internal bool SupplyListing { get; init; } = true;
        internal bool NestedListing { get; init; }
        internal bool EmptyListing { get; init; }
        public object InputPolicyIdentity => this;
        public bool TryGetListing(string path, out ArchiveSafeListing? listing)
        {
            var name = NestedListing ? "a/b/c/file" : "file";
            listing = SupplyListing ? new(EmptyListing ? new Dictionary<string, ArchiveFileListingEntry>()
                : new Dictionary<string, ArchiveFileListingEntry> { [name] = new(name, 4) }, []) : null;
            return listing is not null;
        }
        public ValueTask<PayloadVerificationResult> VerifyAsync(VersionRepresentation representation, string path, CancellationToken token)
        { Verifications++; return ValueTask.FromResult(new PayloadVerificationResult(!Path.GetFileName(path).StartsWith("bad", StringComparison.Ordinal), "fake", "invalid")); }
        public ValueTask<PayloadVerificationResult> PreflightAsync(VersionRepresentation representation, string path, bool fullContentTest, CancellationToken token)
        { Assert.IsTrue(fullContentTest); return VerifyAsync(representation, path, token); }
        public ValueTask MaterializeAsync(IReadOnlyList<ArchiveMaterializationInput> inputs, string staging, CancellationToken token)
        { LegacyMaterializations++; File.WriteAllText(Path.Combine(staging, "file"), "data"); return ValueTask.CompletedTask; }
        public ValueTask MaterializeRestoreAsync(IReadOnlyList<ArchiveMaterializationInput> inputs, string staging, ArchiveRestoreVerificationContext context, CancellationToken token)
        { RestoreMaterializations++; throw new AssertFailedException("Retention must preserve legacy extraction semantics."); }
        public long EstimateMaterializationBytes(IReadOnlyList<ArchiveMaterializationInput> inputs, ArchiveRestoreVerificationContext context)
            => throw new AssertFailedException("Retention must not use selective restore estimates.");
    }
}

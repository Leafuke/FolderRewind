using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Merge;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using FolderRewind.Models;
using FolderRewind.History.Storage;
using FolderRewind.Services;

namespace FolderRewind.Tests;

// 显式启用的真实服务基准。语料、结果均隔离，不读取用户配置；普通 test 不生成大文件。
[TestClass]
[DoNotParallelize]
public sealed class BackupRestorePerformanceBenchmarkTests
{
    private static int BenchmarkRepeats => int.TryParse(Environment.GetEnvironmentVariable("FOLDERREWIND_BENCHMARK_REPEATS"), out var count) && count is > 0 and <= 20 ? count : 5;
    private static bool Candidate => Environment.GetEnvironmentVariable("FOLDERREWIND_BENCHMARK_CANDIDATE") == "1";
    private static RestoreArchivePreflightMode Policy => Environment.GetEnvironmentVariable("FOLDERREWIND_BENCHMARK_POLICY") == "Strict"
        ? RestoreArchivePreflightMode.Strict : RestoreArchivePreflightMode.Balanced;
    [TestMethod]
    [TestCategory("PerformanceBenchmark")]
    public async Task MeasureQuickSnapshotAndReuseFlow()
    {
        var output = Environment.GetEnvironmentVariable("FOLDERREWIND_BENCHMARK_OUTPUT");
        if (string.IsNullOrEmpty(output) || !Candidate) Assert.Inconclusive("Candidate benchmark opt-in required.");
        var results = new List<object>();
        foreach (var ordinary in new[] { false, true }) foreach (var quick in new[] { false, true })
        {
            await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
            var settings = new ArchiveSettings { BackupBeforeRestore = ordinary, QuickRestoreSafetySnapshotEnabled = quick };
            foreach (var changed in new[] { false, true }) for (int repeat = 0; repeat < BenchmarkRepeats; repeat++)
            {
                File.WriteAllText(Path.Combine(f.Target, "a.txt"), changed ? "local changed" : "ours-a");
                using var verification = new ArchiveRestoreVerificationContext(Policy);
                var restore = f.Restore(verification: verification, targetVolume: true);
                int snapshots = 0;
                var scope = HistoryOperationDiagnostics.Begin("B4-quick-flow"); HistoryRestoreResult result;
                using (scope)
                    result = await HistoryQuickRestoreExecution.ExecuteAsync(f.History, restore, new(f.Source, f.Target),
                        f.Ours.NewVersions.Single().VersionId, (await f.History.WorkspaceStore.LoadAsync()).Value!, HistoryRestoreApplyMode.Clean,
                        settings.ShouldCreateSafetySnapshot(true), false,
                        async (_, _, token) => { snapshots++; await f.CaptureAsync(f.Source); return ((await f.History.WorkspaceStore.LoadAsync(token)).Value, null); },
                        _ => Task.CompletedTask, default);
                Assert.AreEqual(changed ? HistoryRestoreStatus.Committed : HistoryRestoreStatus.NoChanges, result.Status, result.Diagnostic);
                Assert.AreEqual(changed && quick ? 1 : 0, snapshots);
                Assert.AreEqual(1L, scope.Summary!.Counters["7z-x"]);
                results.Add(new { scenario = "B4-flow", ordinary, quick, changed, repeat, snapshots, policy = Policy.ToString(),
                    cache = "uncontrolled", installation = "linked-service-with-real-capture-protector", diagnostic = scope.Summary, correct = true });
            }
        }
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "quick-snapshot-reuse-flow.json"), JsonSerializer.Serialize(new { results }, new JsonSerializerOptions { WriteIndented = true }));
    }
    [TestMethod]
    [TestCategory("PerformanceBenchmark")]
    public async Task MeasureSmartFilteredAndQuickComponents()
    {
        var output = Environment.GetEnvironmentVariable("FOLDERREWIND_BENCHMARK_OUTPUT");
        if (string.IsNullOrEmpty(output)) Assert.Inconclusive("Explicit benchmark opt-in required.");
        var results = new List<object>();
        await using (var f = new HistoryChainRewriteFixture())
        {
            await f.InitializeAsync();
            var data = new byte[8 * 1024 * 1024]; new Random(719).NextBytes(data);
            var node = await f.AddAsync(null, dir => {
                File.WriteAllBytes(Path.Combine(dir, "shared.bin"), data);
                File.WriteAllText(Path.Combine(dir, "deleted.txt"), "deleted later");
            });
            for (int step = 1; step <= 5; step++)
            {
                data[step] ^= 1;
                node = await f.AddAsync(node, dir => {
                    File.WriteAllBytes(Path.Combine(dir, "shared.bin"), data);
                    File.WriteAllText(Path.Combine(dir, "new.txt"), "final version " + step);
                    if (step == 5) File.Delete(Path.Combine(dir, "deleted.txt"));
                });
            }
            var graph = await f.History.Query.GetAllRepresentationsAsync();
            var env = new RepresentationEnvironment((await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries, [], []);
            for (int repeat = 0; repeat < BenchmarkRepeats; repeat++)
            {
                var staging = Path.Combine(f.Root, "bench-smart");
                var scope = HistoryOperationDiagnostics.Begin("B2-smart-components");
                using (scope)
                using (var verification = Candidate ? new ArchiveRestoreVerificationContext(Policy) : null)
                    await f.Engine.MaterializeAsync(node.Representation.RepresentationId, graph, env, MaterializationFidelity.Exact, staging, default, verification);
                Assert.IsTrue(node.Tree.EquivalentTo(await HistoryRewriteTree.ReadAsync(staging)));
                results.Add(new { scenario = "B2", repeat, cache = "uncontrolled", diagnostic = scope.Summary, correct = true });
                Directory.Delete(staging, true);
            }
        }
        await using (var f = new HistoryMergeArchiveFixture())
        {
            await f.SeedAsync();
            foreach (var changed in new[] { false, true })
            for (int repeat = 0; repeat < BenchmarkRepeats; repeat++)
            {
                File.WriteAllText(Path.Combine(f.Target, "a.txt"), changed ? "local changed" : "ours-a");
                var baseline = f.Before.GetSourceState(f.Source);
                bool exact;
                var scope = HistoryOperationDiagnostics.Begin("B4-quick-probe-components");
                using (scope)
                using (var verification = Candidate ? new ArchiveRestoreVerificationContext(Policy) : null)
                    exact = await new HistoryExactWorkingStateProbe(f.History, f.Restore(verification: verification, targetVolume: Candidate)).IsExactAsync(new(f.Source, f.Target), baseline, default);
                Assert.AreEqual(!changed, exact);
                results.Add(new { scenario = "B4-probe", changed, repeat, cache = "uncontrolled", diagnostic = scope.Summary, correct = true });
            }
            foreach (var mode in new[] { HistoryRestoreApplyMode.Clean, HistoryRestoreApplyMode.Overwrite })
            for (int repeat = 0; repeat < BenchmarkRepeats; repeat++)
            {
                var root = Path.Combine(f.Root, "bench-filtered");
                var target = Path.Combine(root, "target"); var staging = Path.Combine(root, "staging");
                Directory.CreateDirectory(target); Directory.CreateDirectory(staging);
                var data = new byte[8 * 1024 * 1024]; new Random(719).NextBytes(data);
                File.WriteAllBytes(Path.Combine(target, "session.lock"), data);
                File.WriteAllText(Path.Combine(target, "stale.txt"), "remove only in clean");
                File.WriteAllText(Path.Combine(staging, "a.txt"), "restored");
                var boundary = new EffectiveSourceBoundarySnapshot(EffectiveBoundaryScopeMode.All, [],
                    EffectiveBoundaryFilterMode.Blacklist, ["session.lock"], false);
                var binding = new HistoryRestoreSourceBinding(f.Source, target, boundary);
                var backend = new FileSystemHistoryRestoreMutationBackend();
                var snapshot = backend.PlanRollback(binding, HistoryTransactionId.New());
                var identity = WindowsRestoreFileSystem.Identity(Path.Combine(target, "session.lock"));
                var scope = HistoryOperationDiagnostics.Begin("B3-filtered-mutation-components");
                using (scope)
                {
                    if (Candidate)
                    {
                        var workspace = (await f.History.WorkspaceStore.LoadAsync()).Value!;
                        var restore = f.Restore(targetVolume: true, allowSwap: false);
                        using var session = restore.CreatePreparationSession([binding], workspace)!;
                        var ownedStaging = session.CreateStaging(f.Source);
                        File.Move(Path.Combine(staging, "a.txt"), Path.Combine(ownedStaging, "a.txt"));
                        session.RecordPrepared(f.Source);
                        var version = await f.History.Query.GetVersionAsync(f.Ours.NewVersions.Single().VersionId);
                        var result = await restore.ExecuteMutationAsync([new(binding, version!, MaterializationFidelity.Exact, mode, ownedStaging)],
                            workspace, workspace.WithContentBaselines(workspace.SourceBaselines), default, preparationSession: session);
                        Assert.IsTrue(result.Succeeded, result.Diagnostic);
                    }
                    else
                    {
                        await backend.PrepareRollbackAsync(snapshot, default);
                        await backend.ApplyAsync(binding, staging, mode, snapshot, default);
                        await backend.CommitAsync(snapshot, default);
                    }
                }
                CollectionAssert.AreEqual(data, File.ReadAllBytes(Path.Combine(target, "session.lock")));
                Assert.AreEqual(mode == HistoryRestoreApplyMode.Overwrite, File.Exists(Path.Combine(target, "stale.txt")));
                if (Candidate) Assert.AreEqual(identity, WindowsRestoreFileSystem.Identity(Path.Combine(target, "session.lock")));
                results.Add(new { scenario = "B3", mode = mode.ToString(), repeat, cache = "uncontrolled", diagnostic = scope.Summary, correct = true });
                Directory.Delete(root, true);
            }
        }
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "smart-filtered-quick-components.json"),
            JsonSerializer.Serialize(new { dataset = "synthetic-seed-719", results }, new JsonSerializerOptions { WriteIndented = true }));
    }
    [TestMethod]
    [TestCategory("PerformanceBenchmark")]
    public async Task MeasureFullRestoreComponents()
    {
        var output = Environment.GetEnvironmentVariable("FOLDERREWIND_BENCHMARK_OUTPUT");
        if (string.IsNullOrEmpty(output)) Assert.Inconclusive("Set FOLDERREWIND_BENCHMARK_OUTPUT to opt in.");
        await using var f = new HistoryMergeArchiveFixture();
        await f.SeedAsync();
        var mib = int.TryParse(Environment.GetEnvironmentVariable("FOLDERREWIND_BENCHMARK_MIB"), out var configured) ? configured : 64;
        Assert.IsTrue(mib >= 4 && mib <= 10240);
        var block = new byte[1024 * 1024];
        var random = new Random(719);
        for (int i = 0; i < mib; i++)
        {
            if (i % 2 == 0) random.NextBytes(block); else Array.Clear(block);
            await File.WriteAllBytesAsync(Path.Combine(f.Target, $"data-{i:D5}.bin"), block);
        }
        for (int i = 0; i < 1000; i++)
            await File.WriteAllTextAsync(Path.Combine(f.Target, $"small-{i:D5}.txt"), $"deterministic synthetic file {i}");
        var capture = await f.CaptureAsync(f.Source);
        var version = capture.NewVersions.Single().VersionId;
        var expected = (await MergeTreeManifest.ReadAsync(f.Target, _ => true, default)).Digest;
        var results = new List<object>();
        foreach (var targetVolume in new[] { "C", "D" })
        foreach (var mode in new[] { HistoryRestoreApplyMode.Clean, HistoryRestoreApplyMode.Overwrite })
        for (int repeat = 0; repeat < BenchmarkRepeats; repeat++)
        {
            var root = targetVolume == "C" ? Path.Combine(f.Root, "benchmark")
                : Path.Combine(Path.GetFullPath(output), "isolated-targets", Guid.NewGuid().ToString("N"));
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(Path.Combine(target, "unrelated.txt"), "keep only for overwrite");
            var workspace = (await f.History.WorkspaceStore.LoadAsync()).Value!;
            var clock = Stopwatch.StartNew();
            using var verification = Candidate ? new ArchiveRestoreVerificationContext(Policy) : null;
            var diagnosticScope = HistoryOperationDiagnostics.Begin("B1-B5-full-restore");
            HistoryRestoreResult result;
            using (diagnosticScope)
                result = await f.Restore(verification: verification, targetVolume: Candidate).RestoreVersionAsync(version, new(f.Source, target), workspace, mode);
            clock.Stop();
            Assert.IsTrue(result.Succeeded, result.Diagnostic);
            var actual = (await MergeTreeManifest.ReadAsync(target,
                path => mode != HistoryRestoreApplyMode.Overwrite || path != "unrelated.txt", default)).Digest;
            Assert.AreEqual(expected, actual);
            var diagnostic = diagnosticScope.Summary;
            results.Add(new { scenario = targetVolume == "D" ? "B5" : "B1", mode = mode.ToString(), repeat,
                mib, files = mib + 1002, elapsedMs = clock.Elapsed.TotalMilliseconds,
                cache = "uncontrolled", installation = "linked-service-test", candidate = Candidate, policy = Policy.ToString(), diagnostic, correct = true });
            Directory.Delete(root, true);
        }
        Directory.CreateDirectory(output);
        var exe = SevenZipExecutableLocator.Resolve(Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_7Z"))!;
        await File.WriteAllTextAsync(Path.Combine(output, "full-restore-components.json"), JsonSerializer.Serialize(new {
            dataset = "synthetic-mixed-seed-719", sourceMiB = mib,
            sevenZipSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))), results
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

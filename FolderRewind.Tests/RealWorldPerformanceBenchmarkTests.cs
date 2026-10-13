using System.Text.Json;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using FolderRewind.History.Merge;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RealWorldPerformanceBenchmarkTests
{
    [TestMethod]
    [TestCategory("PerformanceBenchmark")]
    public async Task CreateReadProtectedWorldSnapshot()
    {
        var source = Environment.GetEnvironmentVariable("FOLDERREWIND_BENCHMARK_SOURCE");
        var snapshot = Environment.GetEnvironmentVariable("FOLDERREWIND_BENCHMARK_SNAPSHOT");
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(snapshot)) Assert.Inconclusive("Explicit isolated snapshot opt-in required.");
        Assert.IsFalse(Path.Exists(snapshot), "Never overwrite an existing snapshot.");
        var rules = await MineRewindPerformanceIntegrationTests.ReadPackagedPolicyAsync(source);
        var boundary = new EffectiveSourceBoundarySnapshot(EffectiveBoundaryScopeMode.All, [], EffectiveBoundaryFilterMode.Blacklist, rules, false);
        var binding = new HistoryRestoreSourceBinding(SourceId.New(), source, boundary);
        using var reads = new HistoryWorkingTreeReadLease(binding);
        var include = FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(binding);
        Directory.CreateDirectory(snapshot);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory).Replace('\\', '/');
            if (include(relative)) Directory.CreateDirectory(Path.Combine(snapshot, relative));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (!include(relative)) continue;
            var destination = Path.Combine(snapshot, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var input = File.OpenRead(file)) using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
            { await input.CopyToAsync(stream); stream.Flush(true); }
            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(file));
            File.SetAttributes(destination, File.GetAttributes(file));
        }
        reads.Validate();
        var expected = await MergeTreeManifest.ReadAsync(source, include, default);
        var actual = await MergeTreeManifest.ReadAsync(snapshot, _ => true, default);
        Assert.AreEqual(expected.Digest, actual.Digest);
        var sizes = Directory.GetFiles(snapshot, "*", SearchOption.AllDirectories).Select(path => new FileInfo(path).Length).ToArray();
        await File.WriteAllTextAsync(snapshot + ".metadata.json", JsonSerializer.Serialize(new { dataset = "real-world-a-unexpanded", files = sizes.Length, bytes = sizes.Sum(), rules, correct = true }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [TestMethod]
    [TestCategory("PerformanceBenchmark")]
    public async Task MeasureRealWorldFilteredRestore()
    {
        var snapshot = Environment.GetEnvironmentVariable("FOLDERREWIND_BENCHMARK_SNAPSHOT");
        var output = Environment.GetEnvironmentVariable("FOLDERREWIND_BENCHMARK_OUTPUT");
        if (string.IsNullOrEmpty(snapshot) || string.IsNullOrEmpty(output)) Assert.Inconclusive("Real world benchmark opt-in required.");
        using var metadata = JsonDocument.Parse(File.ReadAllText(snapshot + ".metadata.json"));
        var rules = metadata.RootElement.GetProperty("rules").EnumerateArray().Select(value => value.GetString()!).ToArray();
        var boundary = new EffectiveSourceBoundarySnapshot(EffectiveBoundaryScopeMode.All, [], EffectiveBoundaryFilterMode.Blacklist, rules, false);
        await using var f = new HistoryChainRewriteFixture(); await f.InitializeAsync();
        var node = await f.AddAsync(null, dir => {
            foreach (var file in Directory.EnumerateFiles(snapshot, "*", SearchOption.AllDirectories))
            { var destination = Path.Combine(dir, Path.GetRelativePath(snapshot, file)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination); }
        }, boundary: boundary);
        bool candidate = Environment.GetEnvironmentVariable("FOLDERREWIND_BENCHMARK_CANDIDATE") == "1";
        var policy = Environment.GetEnvironmentVariable("FOLDERREWIND_BENCHMARK_POLICY") == "Strict" ? RestoreArchivePreflightMode.Strict : RestoreArchivePreflightMode.Balanced;
        var results = new List<object>();
        foreach (var mode in new[] { HistoryRestoreApplyMode.Clean, HistoryRestoreApplyMode.Overwrite })
        for (int repeat = 0; repeat < 3; repeat++)
        {
            var target = Path.Combine(f.Root, "world"); Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "session.lock"), "excluded original");
            Directory.CreateDirectory(Path.Combine(target, "voxy")); File.WriteAllBytes(Path.Combine(target, "voxy", "cache.bin"), new byte[16 * 1024 * 1024]);
            File.WriteAllText(Path.Combine(target, "stale.txt"), "clean removes; overwrite retains");
            var untouched = WindowsRestoreFileSystem.Identity(Path.Combine(target, "voxy", "cache.bin"));
            using var verification = candidate ? new ArchiveRestoreVerificationContext(policy) : null;
            var restore = new HistoryRestoreService(f.History, f.Engine,
                async token => new RepresentationEnvironment((await f.History.LocalReplicaCatalogStore.LoadAsync(token)).Value!.Entries, [], []),
                new FileSystemHistoryRestoreMutationBackend()) { UseTargetVolumeWorkspaces = candidate, VerificationContext = verification };
            var scope = HistoryOperationDiagnostics.Begin("real-world-filtered-restore"); HistoryRestoreResult result;
            using (scope) result = await restore.RestoreVersionAsync(node.Version.VersionId, new(f.Source, target, boundary),
                (await f.History.WorkspaceStore.LoadAsync()).Value!, mode);
            Assert.IsTrue(result.Succeeded, result.Diagnostic);
            var include = FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(new(f.Source, target, boundary));
            var actual = await MergeTreeManifest.ReadAsync(target, path => include(path) && path != "stale.txt", default);
            var expected = await MergeTreeManifest.ReadAsync(node.StateDirectory, _ => true, default);
            Assert.AreEqual(expected.Digest, actual.Digest);
            Assert.AreEqual(mode == HistoryRestoreApplyMode.Overwrite, File.Exists(Path.Combine(target, "stale.txt")));
            Assert.AreEqual("excluded original", File.ReadAllText(Path.Combine(target, "session.lock")));
            if (candidate) Assert.AreEqual(untouched, WindowsRestoreFileSystem.Identity(Path.Combine(target, "voxy", "cache.bin")));
            results.Add(new { scenario = "B1-real-filtered", mode = mode.ToString(), repeat, candidate, policy = policy.ToString(), cache = "uncontrolled", diagnostic = scope.Summary, correct = true });
            Directory.Delete(target, true);
        }
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "real-world-filtered-restore.json"), JsonSerializer.Serialize(new { dataset = "real-world-a-unexpanded", excludedTargetBytes = 16 * 1024 * 1024, results }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

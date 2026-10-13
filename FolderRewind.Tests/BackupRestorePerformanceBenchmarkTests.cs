using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Merge;
using FolderRewind.Services;

namespace FolderRewind.Tests;

// 显式启用的真实服务基准。语料、结果均隔离，不读取用户配置；普通 test 不生成大文件。
[TestClass]
public sealed class BackupRestorePerformanceBenchmarkTests
{
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
        for (int repeat = 0; repeat < 3; repeat++)
        {
            var root = targetVolume == "C" ? Path.Combine(f.Root, "benchmark")
                : Path.Combine(Path.GetFullPath(output), "isolated-targets", Guid.NewGuid().ToString("N"));
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(Path.Combine(target, "unrelated.txt"), "keep only for overwrite");
            var workspace = (await f.History.WorkspaceStore.LoadAsync()).Value!;
            var clock = Stopwatch.StartNew();
            var result = await f.Restore().RestoreVersionAsync(version, new(f.Source, target), workspace, mode);
            clock.Stop();
            Assert.IsTrue(result.Succeeded, result.Diagnostic);
            var actual = (await MergeTreeManifest.ReadAsync(target,
                path => mode != HistoryRestoreApplyMode.Overwrite || path != "unrelated.txt", default)).Digest;
            Assert.AreEqual(expected, actual);
            var diagnostic = HistoryOperationDiagnostics.GetRecent().Last();
            results.Add(new { scenario = targetVolume == "D" ? "B5" : "B1", mode = mode.ToString(), repeat,
                mib, files = mib + 1002, elapsedMs = clock.Elapsed.TotalMilliseconds,
                cache = "uncontrolled", installation = "linked-service-test", diagnostic, correct = true });
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

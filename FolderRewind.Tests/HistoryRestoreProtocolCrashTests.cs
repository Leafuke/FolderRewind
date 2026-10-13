using System.Diagnostics;
using FolderRewind.History.Application;
using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass]
[DoNotParallelize]
public sealed class HistoryRestoreProtocolCrashTests
{
    [TestMethod]
    public async Task BatchedProgressRecoversUnflushedActionsAroundSixtyFourBoundary()
    {
        var probe = Environment.GetEnvironmentVariable("FOLDERREWIND_RESTORE_PROTOCOL_PROBE");
        if (string.IsNullOrEmpty(probe)) Assert.Inconclusive("Restore protocol probe is required.");
        foreach (var point in new[] { "after-move-62", "after-move-63", "after-progress-flush", "after-move-64", "after-move-141", "after-workspace" })
        {
            var root = Path.Combine(Path.GetTempPath(), "fr-batch-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                await StopAtAsync(probe, "apply", root, point, "batch");
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    using var recovery = Start(probe, "recover", root, "none", "batch");
                    var error = recovery.StandardError.ReadToEndAsync(); await recovery.WaitForExitAsync();
                    Assert.AreEqual(0, recovery.ExitCode, point + ": " + await error);
                }
                bool committed = point == "after-workspace";
                for (int i = 0; i < 70; i++) Assert.AreEqual((committed ? "restored-" : "original-") + i, File.ReadAllText(Path.Combine(root, "world", "batch-" + i)));
                Assert.IsFalse(Directory.EnumerateDirectories(root, ".folderrewind-restore-*").Any());
            }
            finally { Directory.Delete(root, true); }
        }
    }
    [TestMethod]
    public async Task UnknownObjectsAndDamagedProgressArePreservedWhileTornTailCanRecover()
    {
        var probe = Environment.GetEnvironmentVariable("FOLDERREWIND_RESTORE_PROTOCOL_PROBE");
        if (string.IsNullOrEmpty(probe)) Assert.Inconclusive("Restore protocol probe is required.");
        foreach (var scenario in new[] { "unregistered", "external-target", "external-rollback", "unknown-protocol", "unknown-phase", "corrupt-plan", "corrupt-progress", "corrupt-length", "half-progress" })
        {
            var root = Path.Combine(Path.GetTempPath(), "fr-ambiguous-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                var point = scenario == "unregistered" ? "after-container-before-register" : scenario == "external-target" ? "after-move-0"
                    : scenario == "external-rollback" ? "after-workspace" : "after-progress-flush";
                await StopAtAsync(probe, "apply", root, point);
                var container = Directory.GetDirectories(root, ".folderrewind-restore-*").Single();
                if (scenario == "external-target") { Directory.CreateDirectory(Path.Combine(root, "world")); File.WriteAllText(Path.Combine(root, "world", "external"), "foreign"); }
                if (scenario == "external-rollback") File.WriteAllText(Path.Combine(container, "rollback", "external"), "foreign");
                if (scenario is "unknown-protocol" or "unknown-phase")
                {
                    var path = Directory.GetFiles(Path.Combine(root, "history"), "restore-journal.json", SearchOption.AllDirectories).Single();
                    var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
                    json[scenario == "unknown-protocol" ? "protocolVersion" : "phase"] = 99;
                    File.WriteAllText(path, json.ToJsonString());
                }
                if (scenario == "corrupt-plan")
                {
                    var path = Directory.GetFiles(Path.Combine(root, "history"), "restore-plan.json", SearchOption.AllDirectories).Single();
                    var bytes = File.ReadAllBytes(path); bytes[^2] ^= 1; File.WriteAllBytes(path, bytes);
                }
                if (scenario is "corrupt-progress" or "corrupt-length" or "half-progress")
                {
                    var path = Directory.GetFiles(Path.Combine(root, "history"), "restore-progress.bin", SearchOption.AllDirectories).Single();
                    var bytes = File.ReadAllBytes(path);
                    if (scenario == "half-progress") bytes = bytes[..^5];
                    else if (scenario == "corrupt-length") bytes[0] = 128;
                    else bytes[13] ^= 1;
                    File.WriteAllBytes(path, bytes);
                }
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    using var recovery = Start(probe, "recover", root, "none", "swap");
                    var error = recovery.StandardError.ReadToEndAsync(); await recovery.WaitForExitAsync();
                    if (scenario == "half-progress") Assert.AreEqual(0, recovery.ExitCode, await error);
                    else Assert.AreNotEqual(0, recovery.ExitCode, scenario);
                }
                if (scenario == "half-progress") Assert.AreEqual("original", File.ReadAllText(Path.Combine(root, "world", "file.txt")));
                else if (scenario == "unregistered") Assert.AreEqual("original", File.ReadAllText(Path.Combine(root, "world", "file.txt")));
                else Assert.AreEqual("original", File.ReadAllText(Path.Combine(container, "rollback", "file.txt")));
                if (scenario == "external-target") Assert.AreEqual("foreign", File.ReadAllText(Path.Combine(root, "world", "external")));
                if (scenario == "external-rollback") Assert.AreEqual("foreign", File.ReadAllText(Path.Combine(container, "rollback", "external")));
            }
            finally { Directory.Delete(root, true); }
        }
    }
    [TestMethod]
    public async Task RealTerminationDuringReverseMoveAndRollbackCleanupIsIdempotent()
    {
        var probe = Environment.GetEnvironmentVariable("FOLDERREWIND_RESTORE_PROTOCOL_PROBE");
        if (string.IsNullOrEmpty(probe)) Assert.Inconclusive("Restore protocol probe is required.");
        foreach (var point in new[] { "before-undo-1", "after-undo-1", "before-undo-0", "after-undo-0", "after-cleanup-object" })
        {
            var root = Path.Combine(Path.GetTempPath(), "fr-reverse-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                await StopAtAsync(probe, "apply", root, "after-progress-flush");
                File.Delete(Path.Combine(root, "ready"));
                await StopAtAsync(probe, "recover", root, point);
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    using var recovery = Start(probe, "recover", root, "none", "swap");
                    var error = recovery.StandardError.ReadToEndAsync(); await recovery.WaitForExitAsync();
                    Assert.AreEqual(0, recovery.ExitCode, await error);
                }
                Assert.AreEqual("original", File.ReadAllText(Path.Combine(root, "world", "file.txt")));
            }
            finally { Directory.Delete(root, true); }
        }
    }
    private static async Task StopAtAsync(string probe, string command, string root, string point, string strategy = "swap")
    {
        using var child = Start(probe, command, root, point, strategy);
        var errors = child.StandardError.ReadToEndAsync(); var timer = Stopwatch.StartNew();
        try
        {
            while (!File.Exists(Path.Combine(root, "ready")) && !child.HasExited && timer.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(10);
            Assert.IsTrue(File.Exists(Path.Combine(root, "ready")), point + ": " + (child.HasExited ? await errors : "timed out"));
        }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
    }
    [TestMethod]
    public async Task RealProcessTerminationRecoversBothMoveStrategiesRepeatedly()
    {
        var probe = Environment.GetEnvironmentVariable("FOLDERREWIND_RESTORE_PROTOCOL_PROBE");
        if (string.IsNullOrEmpty(probe)) Assert.Inconclusive("Build tools/RestoreProtocolProbe and set FOLDERREWIND_RESTORE_PROTOCOL_PROBE.");
        var points = new[] { "before-container", "after-container-register", "before-plan", "after-plan-before-prepared", "after-prepared", "before-move-0", "after-move-0", "before-move-1", "after-move-1",
            "after-progress-flush", "before-workspace", "after-workspace", "before-cleanup", "after-cleanup-object", "after-complete" };
        foreach (var strategy in new[] { "swap", "delta", "merge" })
        foreach (var point in strategy == "merge" ? points.Concat(["before-pack", "after-pack"]) : points)
        {
            var root = Path.Combine(Path.GetTempPath(), "fr-crash-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                using var child = Start(probe, "apply", root, point, strategy);
                var errors = child.StandardError.ReadToEndAsync();
                var timer = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(root, "ready")) && !child.HasExited && timer.Elapsed < TimeSpan.FromSeconds(20))
                    await Task.Delay(10);
                if (!File.Exists(Path.Combine(root, "ready")))
                {
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                    Assert.Fail("Probe did not reach " + point + ": " + await errors);
                }
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    using var recovery = Start(probe, "recover", root, "none", strategy);
                    var diagnostic = recovery.StandardError.ReadToEndAsync(); await recovery.WaitForExitAsync();
                    Assert.AreEqual(0, recovery.ExitCode, point + ": " + await diagnostic);
                }
                bool committed = point is "after-workspace" or "before-cleanup" or "after-cleanup-object" or "after-complete"
                    || strategy == "merge" && point is "after-pack" or "before-workspace";
                Assert.AreEqual(committed ? "restored" : "original", File.ReadAllText(Path.Combine(root, "world", "file.txt")), point);
                Assert.AreEqual(!committed || strategy == "delta", File.Exists(Path.Combine(root, "world", "untouched.txt")));
                Assert.IsFalse(Directory.EnumerateDirectories(root, ".folderrewind-restore-*").Any());
            }
            finally { Directory.Delete(root, true); }
        }
    }
    private static Process Start(string probe, params string[] args)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add(probe); foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }
}

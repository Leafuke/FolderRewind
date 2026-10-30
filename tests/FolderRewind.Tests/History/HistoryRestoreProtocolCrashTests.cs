using System.Diagnostics;
using System.Text;

namespace FolderRewind.Tests;

[TestClass]
[TestCategory("ExternalTool")]
[DoNotParallelize]
public sealed class HistoryRestoreProtocolCrashTests
{
    [TestMethod]
    [DataRow("new", "after-partial-materialization")]
    [DataRow("new", "after-preparation-cleanup-record")]
    [DataRow("new", "after-preparation-handoff")]
    [DataRow("new", "after-preparation-marker-cleanup")]
    [DataRow("legacy", "after-partial-materialization")]
    [DataRow("legacy", "after-preparation-cleanup-record")]
    [DataRow("legacy", "after-preparation-handoff")]
    [DataRow("legacy", "after-preparation-marker-cleanup")]
    public async Task PartialMaterializationAndLegacyPreparationSurviveTermination(string strategy, string point)
    {
        var probe = Environment.GetEnvironmentVariable("FOLDERREWIND_RESTORE_PROTOCOL_PROBE");
        if (string.IsNullOrEmpty(probe)) Assert.Inconclusive("Restore protocol probe is required.");
        var root = Path.Combine(Path.GetTempPath(), "fr-partial-crash-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await StopAtAsync(probe, "partial", root, point, strategy);
            for (int repeat = 0; repeat < 2; repeat++) await RunRecoveryProbeAsync(probe, root, point, strategy, repeat);
            Assert.AreEqual("original world", File.ReadAllText(Path.Combine(root, "world", "original.txt")));
            Assert.IsEmpty(Directory.GetDirectories(root, ".folderrewind-restore-*"));
            Assert.IsEmpty(Directory.GetFiles(Path.Combine(root, "history"), "restore-cleanup.json", SearchOption.AllDirectories));
            Assert.IsEmpty(Directory.GetFiles(Path.Combine(root, "history"), "restore-preparation.json", SearchOption.AllDirectories));
        }
        finally { CleanupRoot(root); }
    }

    [TestMethod]
    [DataRow("preparation-drift", "after-snapshot-before-rebind")]
    [DataRow("repair", "repair-after-preservation")]
    [DataRow("repair", "repair-before-publication")]
    [DataRow("repair", "repair-after-publication")]
    public async Task StageOneTerminationPreservesCommittedStateAndRepairMaterials(string scenario, string point)
    {
        var probe = Environment.GetEnvironmentVariable("FOLDERREWIND_RESTORE_PROTOCOL_PROBE");
        if (string.IsNullOrEmpty(probe)) Assert.Inconclusive("Restore protocol probe is required.");
        var root = Path.Combine(Path.GetTempPath(), "fr-stage-one-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var command = scenario == "repair" ? "repair" : "apply";
            await StopAtAsync(probe, command, root, point, scenario);
            for (int repeat = 0; repeat < 2; repeat++)
            {
                var result = await RunProbeAsync(probe, scenario == "repair" ? "repair" : "recover", root,
                    "none", scenario, "stage-one-recovery=" + repeat, false, TimeSpan.FromSeconds(30));
                Assert.AreEqual(0, result.ExitCode, result.Diagnostic);
            }
            var statePath = Path.Combine(root, "history", "local-state", "workspace.json");
            if (scenario == "repair")
            {
                var copies = Directory.GetFiles(Path.GetDirectoryName(statePath)!, "*.original");
                Assert.IsNotEmpty(copies);
                Assert.IsTrue(copies.All(p => File.ReadAllText(p) == "damaged original"));
                using var workspace = new FolderRewind.History.LocalState.HistoryWorkspaceStore(new("protocol-probe"), statePath);
                var state = (await workspace.LoadAsync()).Value!;
                Assert.IsTrue(state.SourceBaselines.All(s => s.RequiresFullCapture && s.BaseVersionId is null && s.ActiveBranchId is null));
            }
            else
            {
                CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(root, "committed-workspace.json")), File.ReadAllBytes(statePath));
                Assert.AreEqual("original", File.ReadAllText(Path.Combine(root, "world", "file.txt")));
                await using var history = new FolderRewind.History.Application.HistoryRuntime(new(new("protocol-probe"), new(Path.Combine(root, "history"))));
                await history.InitializeReadableAsync();
                Assert.HasCount(1, await history.Query.GetSafetySnapshotsAsync());
            }
        }
        finally { CleanupRoot(root); }
    }

    public TestContext TestContext { get; set; } = null!;
    private readonly HashSet<string> _rootsWithLiveProbes = [];

    [TestMethod]
    [DataRow("execute", "before-container")]
    [DataRow("execute", "after-container-before-register")]
    [DataRow("execute", "after-container-register")]
    [DataRow("execute", "after-plan-before-prepared")]
    [DataRow("execute", "after-prepared")]
    [DataRow("execute", "after-move-0")]
    [DataRow("execute", "after-workspace")]
    [DataRow("execute", "after-cleanup-record-before-complete")]
    [DataRow("execute", "after-cleanup-handoff")]
    [DataRow("execute", "before-cleanup")]
    [DataRow("execute", "after-cleanup-object")]
    [DataRow("abandon", "after-snapshot-before-rebind")]
    [DataRow("abandon", "after-preparation-cleanup-record")]
    public async Task NewPreparationAndCleanupHandoffSurviveRealTermination(string strategy, string point)
    {
        var probe = Environment.GetEnvironmentVariable("FOLDERREWIND_RESTORE_PROTOCOL_PROBE");
        if (string.IsNullOrEmpty(probe)) Assert.Inconclusive("Restore protocol probe is required.");
        var root = Path.Combine(Path.GetTempPath(), "fr-new-protocol-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await StopAtAsync(probe, "new", root, point, strategy);
            for (int repeat = 0; repeat < 2; repeat++) await RunRecoveryProbeAsync(probe, root, point, strategy, repeat);
            bool committed = point is "after-workspace" or "after-cleanup-record-before-complete" or "after-cleanup-handoff" or "before-cleanup" or "after-cleanup-object";
            Assert.AreEqual(committed ? "restored" : "original", File.ReadAllText(Path.Combine(root, "world", "file.txt")));
            Assert.HasCount(point == "after-container-before-register" ? 1 : 0, Directory.GetDirectories(root, ".folderrewind-restore-*"));
            if (strategy == "abandon") CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(root, "committed-workspace.json")),
                File.ReadAllBytes(Path.Combine(root, "history", "local-state", "workspace.json")));
        }
        finally { CleanupRoot(root); }
    }

    [TestMethod]
    [DataRow("after-move-62")]
    [DataRow("after-move-63")]
    [DataRow("after-progress-flush")]
    [DataRow("after-move-64")]
    [DataRow("after-move-141")]
    [DataRow("after-workspace")]
    public async Task BatchedProgressRecoversUnflushedActionsAroundSixtyFourBoundary(string point)
    {
        var probe = Environment.GetEnvironmentVariable("FOLDERREWIND_RESTORE_PROTOCOL_PROBE");
        if (string.IsNullOrEmpty(probe)) Assert.Inconclusive("Restore protocol probe is required.");
        var root = Path.Combine(Path.GetTempPath(), "fr-batch-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await StopAtAsync(probe, "apply", root, point, "batch");
            for (int repeat = 0; repeat < 2; repeat++)
            {
                await RunRecoveryProbeAsync(probe, root, point, "batch", repeat);
            }
            bool committed = point == "after-workspace";
            for (int i = 0; i < 70; i++) Assert.AreEqual((committed ? "restored-" : "original-") + i, File.ReadAllText(Path.Combine(root, "world", "batch-" + i)));
            Assert.IsFalse(Directory.EnumerateDirectories(root, ".folderrewind-restore-*").Any());
        }
        finally { CleanupRoot(root); }
    }
    [TestMethod]
    [DataRow("unregistered")]
    [DataRow("external-target")]
    [DataRow("external-rollback")]
    [DataRow("unknown-protocol")]
    [DataRow("unknown-phase")]
    [DataRow("corrupt-plan")]
    [DataRow("corrupt-progress")]
    [DataRow("corrupt-length")]
    [DataRow("half-progress")]
    public async Task UnknownObjectsAndDamagedProgressArePreservedWhileTornTailCanRecover(string scenario)
    {
        var probe = Environment.GetEnvironmentVariable("FOLDERREWIND_RESTORE_PROTOCOL_PROBE");
        if (string.IsNullOrEmpty(probe)) Assert.Inconclusive("Restore protocol probe is required.");
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
                await RunRecoveryProbeAsync(probe, root, point + "/" + scenario, "swap", repeat, scenario is "half-progress" or "unregistered" or "external-rollback");
            }
            if (scenario == "half-progress") Assert.AreEqual("original", File.ReadAllText(Path.Combine(root, "world", "file.txt")));
            else if (scenario == "unregistered") Assert.AreEqual("original", File.ReadAllText(Path.Combine(root, "world", "file.txt")));
            else Assert.AreEqual("original", File.ReadAllText(Path.Combine(container, "rollback", "file.txt")));
            if (scenario == "external-target") Assert.AreEqual("foreign", File.ReadAllText(Path.Combine(root, "world", "external")));
            if (scenario == "external-rollback") Assert.AreEqual("foreign", File.ReadAllText(Path.Combine(container, "rollback", "external")));
        }
        finally { CleanupRoot(root); }
    }
    [TestMethod]
    [DataRow("before-undo-1")]
    [DataRow("after-undo-1")]
    [DataRow("before-undo-0")]
    [DataRow("after-undo-0")]
    [DataRow("after-cleanup-object")]
    public async Task RealTerminationDuringReverseMoveAndRollbackCleanupIsIdempotent(string point)
    {
        var probe = Environment.GetEnvironmentVariable("FOLDERREWIND_RESTORE_PROTOCOL_PROBE");
        if (string.IsNullOrEmpty(probe)) Assert.Inconclusive("Restore protocol probe is required.");
        var root = Path.Combine(Path.GetTempPath(), "fr-reverse-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await StopAtAsync(probe, "apply", root, "after-progress-flush");
            await StopAtAsync(probe, "recover", root, point);
            for (int repeat = 0; repeat < 2; repeat++)
            {
                await RunRecoveryProbeAsync(probe, root, point, "swap", repeat);
            }
            Assert.AreEqual("original", File.ReadAllText(Path.Combine(root, "world", "file.txt")));
        }
        finally { CleanupRoot(root); }
    }
    public static IEnumerable<object[]> CrashCases()
    {
        var points = new[] { "before-container", "after-container-register", "before-plan", "after-plan-before-prepared", "after-prepared", "before-move-0", "after-move-0", "before-move-1", "after-move-1",
            "after-progress-flush", "before-workspace", "after-workspace", "before-cleanup", "after-cleanup-object", "after-complete" };
        foreach (var strategy in new[] { "swap", "delta", "merge" })
        foreach (var point in strategy == "merge" ? points.Concat(["before-pack", "after-pack"]) : points)
            yield return [strategy, point];
    }

    [TestMethod]
    [DynamicData(nameof(CrashCases))]
    public async Task RealProcessTerminationRecoversBothMoveStrategiesRepeatedly(string strategy, string point)
    {
        var probe = Environment.GetEnvironmentVariable("FOLDERREWIND_RESTORE_PROTOCOL_PROBE");
        if (string.IsNullOrEmpty(probe)) Assert.Inconclusive("Build tools/RestoreProtocolProbe and set FOLDERREWIND_RESTORE_PROTOCOL_PROBE.");
        var root = Path.Combine(Path.GetTempPath(), "fr-crash-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await StopAtAsync(probe, "apply", root, point, strategy);
            for (int repeat = 0; repeat < 2; repeat++)
            {
                await RunRecoveryProbeAsync(probe, root, point, strategy, repeat);
            }
            bool committed = point is "after-workspace" or "before-cleanup" or "after-cleanup-object" or "after-complete"
                || strategy == "merge" && point is "after-pack" or "before-workspace";
            Assert.AreEqual(committed ? "restored" : "original", File.ReadAllText(Path.Combine(root, "world", "file.txt")), point);
            Assert.AreEqual(!committed || strategy == "delta", File.Exists(Path.Combine(root, "world", "untouched.txt")));
            Assert.IsFalse(Directory.EnumerateDirectories(root, ".folderrewind-restore-*").Any());
        }
        finally { CleanupRoot(root); }
    }

    [TestMethod]
    public async Task RecoveryTimeoutFailsWithDiagnosticsAndReapsProbeBeforeRetry()
    {
        var probe = Environment.GetEnvironmentVariable("FOLDERREWIND_RESTORE_PROTOCOL_PROBE");
        if (string.IsNullOrEmpty(probe)) Assert.Inconclusive("Restore protocol probe is required.");
        var root = Path.Combine(Path.GetTempPath(), "fr-timeout-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await StopAtAsync(probe, "apply", root, "after-progress-flush");
            File.Delete(Path.Combine(root, "ready"));
            var error = await Assert.ThrowsExactlyAsync<AssertFailedException>(() => RunProbeAsync(
                probe, "recover", root, "before-undo-1", "swap", "recovery=1", false, TimeSpan.FromSeconds(10)));
            StringAssert.Contains(error.Message, "Timed out");
            StringAssert.Contains(error.Message, "strategy=swap; point=before-undo-1; stage=recovery=1; pid=");
            StringAssert.Contains(error.Message, "exited=True; exitCode=");
            StringAssert.Contains(error.Message, "Reached restore fault point: before-undo-1");
            Assert.AreEqual("before-undo-1", File.ReadAllText(Path.Combine(root, "ready")));
            for (int repeat = 0; repeat < 2; repeat++)
                await RunRecoveryProbeAsync(probe, root, "before-undo-1", "swap", repeat);
            Assert.AreEqual("original", File.ReadAllText(Path.Combine(root, "world", "file.txt")));
            Assert.IsFalse(Directory.EnumerateDirectories(root, ".folderrewind-restore-*").Any());
        }
        finally { CleanupRoot(root); }
    }

    private async Task StopAtAsync(string probe, string command, string root, string point, string strategy = "swap")
    {
        // A recovery fault must not accept the marker left by the previous apply process.
        File.Delete(Path.Combine(root, "ready"));
        await RunProbeAsync(probe, command, root, point, strategy, command + "-crash", true, TimeSpan.FromSeconds(20));
    }

    private async Task RunRecoveryProbeAsync(string probe, string root, string point, string strategy, int repeat, bool shouldSucceed = true)
    {
        var result = await RunProbeAsync(probe, "recover", root, "none", strategy, $"recovery={repeat + 1}; crashPoint={point}", false, TimeSpan.FromSeconds(30));
        if (shouldSucceed) Assert.AreEqual(0, result.ExitCode, result.Diagnostic);
        else Assert.AreNotEqual(0, result.ExitCode, result.Diagnostic);
    }

    private async Task<(int ExitCode, string Diagnostic)> RunProbeAsync(string probe, string command, string root,
        string point, string strategy, string stage, bool stopAtFault, TimeSpan timeout)
    {
        using var child = Start(probe, command, root, point, strategy);
        var context = $"strategy={strategy}; point={point}; stage={stage}; pid={child.Id}; root={root}";
        TestContext.WriteLine("Starting probe: " + context);
        var errors = new StringBuilder();
        child.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (errors) errors.AppendLine(e.Data); } };
        child.BeginErrorReadLine();
        var exit = child.WaitForExitAsync();
        var timer = Stopwatch.StartNew();
        string? failure = null;
        try
        {
            if (stopAtFault)
            {
                while (!File.Exists(Path.Combine(root, "ready")) && !child.HasExited && timer.Elapsed < timeout)
                    await Task.Delay(10);
                if (!File.Exists(Path.Combine(root, "ready")) || child.HasExited)
                    failure = "Probe did not reach the fault point while alive.";
            }
            else
            {
                try { await exit.WaitAsync(timeout); }
                catch (TimeoutException) { failure = $"Timed out after {timeout.TotalSeconds:F0}s waiting for probe exit."; }
            }
        }
        finally
        {
            // WaitAsync only bounds the caller. Always reap the process we started as well.
            try
            {
                if (!child.HasExited)
                {
                    try { child.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (child.HasExited) { }
                }
                await exit.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                if (!child.HasExited) _rootsWithLiveProbes.Add(root);
                failure = $"{failure} Probe cleanup failed: {ex.Message}";
            }
        }
        string stderr;
        lock (errors) stderr = errors.ToString();
        var diagnostic = $"{context}; elapsed={timer.Elapsed}; exited={child.HasExited}; exitCode={(child.HasExited ? child.ExitCode.ToString() : "running")}; stderr={stderr}";
        TestContext.WriteLine(diagnostic);
        if (failure is not null) Assert.Fail(failure + Environment.NewLine + diagnostic);
        return (child.ExitCode, diagnostic);
    }

    private void CleanupRoot(string root)
    {
        if (_rootsWithLiveProbes.Contains(root))
        {
            TestContext.WriteLine("Retaining fixture because probe exit was not confirmed: " + root);
            return;
        }
        Directory.Delete(root, true);
    }
    private static Process Start(string probe, params string[] args)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add(probe); foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }
}

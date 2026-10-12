using FolderRewind.History.Application;
using FolderRewind.History.Capture;
using FolderRewind.History.Cloud;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using FolderRewind.Services;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace FolderRewind.Tests;

// 复用当前测试程序集作为受控 Host，不构造第二套 History/传输 Harness。
[TestClass]
public sealed class CloudHostCrashBootstrap
{
    [AssemblyInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        var root = Environment.GetEnvironmentVariable("FOLDERREWIND_CLOUD_CRASH_ROOT");
        var mode = Environment.GetEnvironmentVariable("FOLDERREWIND_CLOUD_CRASH_MODE");
        if (root is null || mode is null) return;
        var parent = Path.Combine(Path.GetTempPath(), "FolderRewindCloudCrash");
        if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid controlled probe root.");
        var id = new HistoryConfigId(File.ReadAllText(Path.Combine(root, "config-id")));
        if (mode == "oauth")
        {
            var source = Path.Combine(root, "rclone.conf");
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cscript.exe");
            using var connection = new RcloneExecutionContext(exe, source, root, "box:backup", Path.Combine(root, "tasks"), true);
            var start = connection.CreateStartInfo(["lsf", "box:backup"]);
            start.ArgumentList.Insert(0, Path.Combine(root, "child.js")); start.ArgumentList.Insert(0, "//nologo");
            await using var refresh = await RcloneCredentialRefresh.BeginAsync(start, default);
            await refresh!.StartingAsync(default);
            using var child = new Process { StartInfo = start }; child.Start();
            await refresh.StartedAsync(child, default);
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(Path.Combine(root, "payload-ready"))) await Task.Delay(50, budget.Token);
            Ready(root, new { host = Environment.ProcessId, hostStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                child = child.Id, started = child.StartTime.ToUniversalTime().Ticks, snapshot = connection.SnapshotPath });
            await Task.Delay(Timeout.Infinite); // 父测试强杀 Host，故意不经过 Dispose/CAS。
            return;
        }
        await using var history = new HistoryRuntime(new FileHistoryRepository(id, new(Path.Combine(root, "repository"))));
        await history.InitializeAsync();
        history.CloudJobs.ConfigureAdmission(new("profile", "profile-revision", "binding", "original:/backup", id.Value));
        history.CloudJobs.Checkpoint = point =>
        {
            if (point != mode) return;
            Ready(root, new { host = Environment.ProcessId, hostStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks });
            Thread.Sleep(Timeout.Infinite);
        };
        var sourceId = SourceId.New(); var representation = RepresentationId.New();
        var payload = Path.Combine(root, "payload.bin"); File.WriteAllText(payload, "controlled-payload");
        var bytes = File.ReadAllBytes(payload); var now = DateTimeOffset.UtcNow;
        var capture = new SourceCaptureResult(sourceId, SourceCaptureOutcome.Captured, CaptureScope.FullSource, "state-probe", null,
            new RepresentationCandidate(representation, RepresentationKind.CoreFull, "7z", [], MaterializationFidelity.Exact, null, "state-probe", null),
            new LocalReplicaCandidate(LocalReplicaId.New(), representation, LocalReplicaLocator.ControlledAbsolute(payload), CapturePayloadState.VerifiedFinal, now),
            new CapturePayloadCandidate(payload, CapturePayloadState.VerifiedFinal, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))),
            -1, null, null, []);
        await history.Commit.CommitAsync(new(new(id, [new(sourceId, new("probe", root))]),
            new(RunId.New(), now, now, BackupInvocationKind.Manual, HistoryProvenance.Native("controlled-crash")), null, [capture]));
        throw new InvalidOperationException("The controlled crash checkpoint was not reached.");
    }
    private static void Ready(string root, object value) => AtomicFileService.Write(Path.Combine(root, "ready.json"),
        s => JsonSerializer.Serialize(s, value));
}

[TestClass]
[DoNotParallelize]
public sealed class CloudHostCrashTests
{
    [TestMethod]
    [DataRow("prepared", false)]
    [DataRow("before-activate", true)]
    public async Task ForcedHostTerminationReplaysPreparedEvidenceBeforeCleanup(string checkpoint, bool committed)
    {
        await ProbeAsync(checkpoint, async (root, _) =>
        {
            var id = new HistoryConfigId(File.ReadAllText(Path.Combine(root, "config-id")));
            await using var restarted = new HistoryRuntime(new FileHistoryRepository(id, new(Path.Combine(root, "repository"))));
            await restarted.InitializeAsync();
            var job = restarted.CloudJobs.Read().Jobs.Single();
            Assert.AreEqual("original:/backup", job.Target.RemoteRoot);
            Assert.AreEqual(committed ? CloudJobPhase.Pending : CloudJobPhase.Aborted, job.Phase);
            Assert.AreEqual(committed ? CloudProtectionDisposition.Held : CloudProtectionDisposition.Released, job.Protection);
            if (committed) Assert.ThrowsExactly<InvalidOperationException>(() => restarted.CloudJobs.RequireCleanupSafe());
            Assert.IsTrue(File.Exists(Path.Combine(root, "payload.bin")));
        });
    }
    [TestMethod]
    public async Task LiveRefreshChildBlocksReplayAfterHostDeathAndExitedChildAllowsSafeMerge()
    {
        await ProbeAsync("oauth", async (root, ready) =>
        {
            var source = Path.Combine(root, "rclone.conf"); var tasks = Path.Combine(root, "tasks");
            var snapshot = ready.GetProperty("snapshot").GetString()!;
            StringAssert.Contains(File.ReadAllText(snapshot), "\"new\"");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => RcloneCredentialRefresh.PrepareStoreAsync(source, tasks, true, default));
            StringAssert.Contains(File.ReadAllText(source), "\"old\"");
            Assert.IsTrue(File.Exists(snapshot));
            await StopRecordedChildAsync(ready);
            await RcloneCredentialRefresh.PrepareStoreAsync(source, tasks, true, default);
            StringAssert.Contains(File.ReadAllText(source), "\"new\"");
            Assert.IsFalse(File.Exists(snapshot));
        });
    }
    private static async Task ProbeAsync(string mode, Func<string, JsonElement, Task> verify)
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindCloudCrash", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "config-id"), Guid.NewGuid().ToString("N"));
        File.WriteAllText(Path.Combine(root, "rclone.conf"), "[box]\ntype = box\ntoken = {\"access_token\":\"old\"}\n");
        File.WriteAllText(Path.Combine(root, "child.js"), "var fs = new ActiveXObject('Scripting.FileSystemObject'); var p = WScript.Arguments.Item(1); var f = fs.OpenTextFile(p, 1); var t = f.ReadAll(); f.Close(); f = fs.OpenTextFile(p, 2); f.Write(t.replace('old', 'new')); f.Close(); fs.CreateTextFile(" + JsonSerializer.Serialize(Path.Combine(root, "payload-ready")) + ", true).Close(); WScript.Sleep(60000);");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("vstest"); start.ArgumentList.Add(typeof(CloudHostCrashTests).Assembly.Location);
        start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=FolderRewind.Tests.CloudConnectionModelTests.SharedConnectionHasIndependentProjectRootsAndStableTargetRevision");
        start.Environment["FOLDERREWIND_CLOUD_CRASH_ROOT"] = root; start.Environment["FOLDERREWIND_CLOUD_CRASH_MODE"] = mode;
        using var driver = Process.Start(start)!;
        var output = driver.StandardOutput.ReadToEndAsync(); var error = driver.StandardError.ReadToEndAsync();
        JsonElement ready = default;
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(Path.Combine(root, "ready.json")))
            {
                if (driver.HasExited) Assert.Fail("Controlled host failed to reach its checkpoint: " + await output + await error);
                await Task.Delay(50, budget.Token);
            }
            ready = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "ready.json"))).RootElement.Clone();
            using var host = Process.GetProcessById(ready.GetProperty("host").GetInt32());
            Assert.AreEqual(ready.GetProperty("hostStarted").GetInt64(), host.StartTime.ToUniversalTime().Ticks);
            host.Kill(); await host.WaitForExitAsync();
            await driver.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await verify(root, ready);
        }
        finally
        {
            if (!driver.HasExited) { driver.Kill(entireProcessTree: true); await driver.WaitForExitAsync(); }
            if (ready.ValueKind == JsonValueKind.Object) await StopRecordedChildAsync(ready);
            await Task.WhenAll(output, error);
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try { Directory.Delete(root, true); break; }
                catch (IOException) when (attempt < 19) { await Task.Delay(50); }
            }
        }
    }
    private static async Task StopRecordedChildAsync(JsonElement ready)
    {
        if (!ready.TryGetProperty("child", out var childId)) return;
        try
        {
            using var child = Process.GetProcessById(childId.GetInt32());
            if (child.StartTime.ToUniversalTime().Ticks != ready.GetProperty("started").GetInt64()) return;
            child.Kill(); await child.WaitForExitAsync();
        }
        catch (ArgumentException) { }
    }
}

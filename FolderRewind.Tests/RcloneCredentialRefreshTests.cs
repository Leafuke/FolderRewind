using FolderRewind.Services;
using System.Diagnostics;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class RcloneCredentialRefreshTests
{
    private string _root = null!;
    private string _config = null!;
    private string _script = null!;
    private string _exe = null!;
    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindRefresh", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cscript.exe");
        _config = Path.Combine(_root, "rclone.conf");
        File.WriteAllText(_config, "# keep comment\n[outer]\ntype = crypt\nremote = inner:/backup\n[inner]\ntype = box\ntoken = {\"access_token\":\"old\"}\n[other]\ntype = webdav\nurl = https://unchanged.test/\n");
        _script = Path.Combine(_root, "controlled-rclone.js");
        File.WriteAllText(_script, "var fs = new ActiveXObject('Scripting.FileSystemObject'); var path = WScript.Arguments.Item(1); var f = fs.OpenTextFile(path, 1); var text = f.ReadAll(); f.Close(); text = text.replace('old', 'new'); f = fs.OpenTextFile(path, 2); f.Write(text); f.Close(); WScript.Echo('done');");
    }
    [TestCleanup] public void Cleanup() => Directory.Delete(_root, true);
    private RcloneExecutionContext Context() => new(_exe, _config, _root, "outer:/backup", _root, allowTokenWriteback: true);
    private ProcessStartInfo Start(RcloneExecutionContext context)
    {
        var start = context.CreateStartInfo(["lsf", "outer:/backup"]);
        start.ArgumentList.Insert(0, _script); start.ArgumentList.Insert(0, "//nologo"); return start;
    }

    [TestMethod]
    public async Task ControlledChildRefreshIsMergedWithoutChangingOtherSections()
    {
        using var context = Context();
        await RcloneConnectionService.RunAsync(Start(context), TimeSpan.FromSeconds(10), default);
        StringAssert.Contains(File.ReadAllText(_config), "\"access_token\":\"new\"");
        StringAssert.Contains(File.ReadAllText(_config), "# keep comment");
        StringAssert.Contains(File.ReadAllText(_config), "url = https://unchanged.test/");
        context.RequireUnchanged();
        await RcloneConnectionService.RunAsync(Start(context), TimeSpan.FromSeconds(10), default);
        Assert.IsFalse(File.Exists(Path.Combine(context.TaskDirectory, "attempt.json")));
    }

    [TestMethod]
    public async Task PersistedRefreshIsRecoveredBeforeTheNextCommand()
    {
        string snapshot;
        using (var original = Context())
        {
            snapshot = original.SnapshotPath;
            var start = Start(original);
            await using var lease = await RcloneCredentialRefresh.BeginAsync(start, default);
            Assert.IsNotNull(lease);
            await lease.StartingAsync(default);
            using var process = new Process { StartInfo = start };
            process.Start(); await lease.StartedAsync(process, default);
            await process.WaitForExitAsync();
            // 模拟 Host 在子进程写盘后、CAS 之前退出：只释放门闩，保留完整 Attempt。
        }
        StringAssert.Contains(File.ReadAllText(_config), "\"access_token\":\"old\"");
        using var next = Context();
        await RcloneConnectionService.RunAsync(Start(next), TimeSpan.FromSeconds(10), default);
        StringAssert.Contains(File.ReadAllText(_config), "\"access_token\":\"new\"");
        Assert.IsFalse(File.Exists(snapshot));
    }

    [TestMethod]
    public async Task SourceConflictPreservesNewTokenAndBlocksFurtherCalls()
    {
        using var context = Context(); var start = Start(context);
        await using (var lease = await RcloneCredentialRefresh.BeginAsync(start, default))
        {
            Assert.IsNotNull(lease);
            File.WriteAllText(context.SnapshotPath, File.ReadAllText(context.SnapshotPath).Replace("old", "new"));
            File.AppendAllText(_config, "user = external-change\n");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => lease.CompleteAsync());
        }
        Assert.IsTrue(File.Exists(context.SnapshotPath));
        StringAssert.Contains(File.ReadAllText(context.SnapshotPath), "\"access_token\":\"new\"");
        using var next = Context();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => RcloneConnectionService.RunAsync(Start(next), TimeSpan.FromSeconds(10), default));
    }

    [TestMethod]
    public async Task TwoProfilesUsingOneStorageSerializeBeforeRefresh()
    {
        using var first = Context(); using var second = Context();
        var firstStart = Start(first); var secondStart = Start(second);
        await Task.WhenAll(RcloneConnectionService.RunAsync(firstStart, TimeSpan.FromSeconds(10), default),
            RcloneConnectionService.RunAsync(secondStart, TimeSpan.FromSeconds(10), default));
        first.RequireUnchanged(); second.RequireUnchanged();
        StringAssert.Contains(File.ReadAllText(_config), "\"access_token\":\"new\"");
    }

    [TestMethod]
    public void ExternalOAuthIsRejectedBeforeStartingAChild()
        => Assert.ThrowsExactly<InvalidOperationException>(() => new RcloneExecutionContext(_exe, _config, _root, "outer:/backup", _root));
}

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
    public void UnknownSnapshotRetainsEvidenceAndBlocksCredentialReferenceDeletion()
    {
        var directory = Path.Combine(_root, "task-orphan"); Directory.CreateDirectory(directory);
        var snapshot = Path.Combine(directory, "rclone.conf"); File.Copy(_config, snapshot);
        Assert.IsTrue(RcloneCredentialRefresh.HasPendingEvidence(_root, _config));
        Assert.IsTrue(File.Exists(snapshot));
        using var connection = Context();
        Assert.IsTrue(File.Exists(snapshot));
    }

    [TestMethod]
    public async Task IdleSnapshotFromStoppedContextIsRecoveredBeforeRetry()
    {
        string snapshot;
        using (var original = Context())
        {
            snapshot = original.SnapshotPath;
            original.PreserveSnapshot = true; // A kill between context construction and its first command.
        }
        await RcloneCredentialRefresh.PrepareStoreAsync(_config, _root, true, default);
        Assert.IsFalse(File.Exists(snapshot));
        await RcloneCredentialRefresh.PrepareStoreAsync(_config, _root, true, default);
        using var next = Context();
        await RcloneConnectionService.RunAsync(Start(next), TimeSpan.FromSeconds(10), default);
    }

    [TestMethod]
    public async Task SettledSnapshotAfterRefreshIsRecoveredWithoutLosingTokens()
    {
        string snapshot;
        using (var original = Context())
        {
            snapshot = original.SnapshotPath;
            await RcloneConnectionService.RunAsync(Start(original), TimeSpan.FromSeconds(10), default);
            original.PreserveSnapshot = true; // A kill between successfully completed commands.
        }
        await RcloneCredentialRefresh.PrepareStoreAsync(_config, _root, true, default);
        Assert.IsFalse(File.Exists(snapshot));
        StringAssert.Contains(File.ReadAllText(_config), "\"access_token\":\"new\"");
    }

    [TestMethod]
    public async Task UnrecordedSnapshotChangeIsRetainedAndBlocksRetry()
    {
        string snapshot;
        using (var original = Context())
        {
            snapshot = original.SnapshotPath;
            File.WriteAllText(snapshot, File.ReadAllText(snapshot).Replace("old", "unsettled"));
            original.PreserveSnapshot = true;
        }
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => RcloneCredentialRefresh.PrepareStoreAsync(_config, _root, true, default));
        StringAssert.Contains(File.ReadAllText(snapshot), "unsettled");
        StringAssert.Contains(File.ReadAllText(_config), "\"access_token\":\"old\"");
    }

    [TestMethod]
    public async Task LiveIdleSnapshotIsNeverRecoveredAsAnOrphan()
    {
        using var active = Context();
        await RcloneCredentialRefresh.PrepareStoreAsync(_config, _root, true, default);
        Assert.IsTrue(File.Exists(active.SnapshotPath));
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
    public async Task TrustedRevisionChainAcceptsNormalRefreshAndRejectsExternalAccountReplacement()
    {
        using var context = Context(); var frozen = context.RevisionEvidence;
        await RcloneConnectionService.RunAsync(Start(context), TimeSpan.FromSeconds(10), default);
        Assert.IsTrue(RcloneCredentialRefresh.AcceptsFrozenRevision(_config, frozen, File.ReadAllBytes(_config)));
        File.WriteAllText(_config, File.ReadAllText(_config).Replace("\"new\"", "\"other-account\""));
        Assert.IsFalse(RcloneCredentialRefresh.AcceptsFrozenRevision(_config, frozen, File.ReadAllBytes(_config)));
        using var changed = Context();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => RcloneConnectionService.RunAsync(Start(changed), TimeSpan.FromSeconds(10), default));
    }

    [TestMethod]
    public void ExternalOAuthIsRejectedBeforeStartingAChild()
        => Assert.ThrowsExactly<InvalidOperationException>(() => new RcloneExecutionContext(_exe, _config, _root, "outer:/backup", _root));

    [TestMethod]
    public async Task FreshOriginFollowsTrustedTokenRevisionsWhileKeepingUnattributedLegacyBytes()
    {
        RcloneCredentialStore.RegisterFresh(_config, File.ReadAllBytes(_config));
        var legacy = Path.Combine(_root, "task-unknown"); Directory.CreateDirectory(legacy);
        var evidence = Path.Combine(legacy, "rclone.conf"); File.WriteAllText(evidence, "unique unrelated legacy token");
        using (var context = Context())
            await RcloneConnectionService.RunAsync(Start(context), TimeSpan.FromSeconds(10), default);
        Assert.IsTrue(RcloneCredentialStore.HasFreshOrigin(_config, true));
        await RcloneCredentialRefresh.PrepareStoreAsync(_config, _root, true, default);
        StringAssert.Contains(File.ReadAllText(_config), "\"access_token\":\"new\"");
        Assert.AreEqual("unique unrelated legacy token", File.ReadAllText(evidence));
        Assert.IsFalse(RcloneCredentialRefresh.HasPendingEvidence(_root, _config, true));
    }

    [TestMethod]
    public async Task ReplayCompletesOriginProofBeforeJudgingUnattributedLegacyEvidence()
    {
        RcloneCredentialStore.RegisterFresh(_config, File.ReadAllBytes(_config));
        var legacy = Path.Combine(_root, "task-unknown"); Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "rclone.conf"), "unique legacy evidence");
        using (var context = Context())
        {
            await using var lease = await RcloneCredentialRefresh.BeginAsync(Start(context), default);
            var refreshed = File.ReadAllText(_config).Replace("old", "new");
            File.WriteAllText(context.SnapshotPath, refreshed);
            File.WriteAllText(_config, refreshed); // Durable source write, before the trusted ledger/terminal cleanup.
            var path = Path.Combine(context.TaskDirectory, "attempt.json");
            var attempt = JsonSerializer.Deserialize(File.ReadAllBytes(path), CredentialRefreshJsonContext.Default.CredentialRefreshAttempt)!;
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(attempt with { Phase = "Exited" }, CredentialRefreshJsonContext.Default.CredentialRefreshAttempt));
            context.PreserveSnapshot = true;
        }
        Assert.IsFalse(RcloneCredentialStore.HasFreshOrigin(_config, true));
        await RcloneCredentialRefresh.PrepareStoreAsync(_config, _root, true, default);
        Assert.IsTrue(RcloneCredentialStore.HasFreshOrigin(_config, true));
        Assert.AreEqual("unique legacy evidence", File.ReadAllText(Path.Combine(legacy, "rclone.conf")));
        StringAssert.Contains(File.ReadAllText(_config), "\"access_token\":\"new\"");
    }
}

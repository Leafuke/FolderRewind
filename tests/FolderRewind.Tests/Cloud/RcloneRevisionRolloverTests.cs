using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class RcloneRevisionRolloverTests
{
    private string _root = null!, _source = null!, _tasks = null!;
    private string _frozen = null!;
    private string Ledger => _source + ".folderrewind-revisions.json";
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    [TestInitialize] public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "fr-revisions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root); _source = Path.Combine(_root, "rclone.conf"); _tasks = Path.Combine(_root, "tasks");
        File.WriteAllText(_source, "[test]\ntype = box\ntoken = {\"access_token\":\"old\"}\n");
        RcloneCredentialStore.RegisterFresh(_source, File.ReadAllBytes(_source));
        _frozen = Hash("first persisted job's synthetic revision");
        SeedFullLedger(new(1, Hash(File.ReadAllText(_source)), RcloneCredentialRefresh.TargetRevision(File.ReadAllBytes(_source)), []));
    }
    [TestCleanup] public void Cleanup() => Directory.Delete(_root, true);
    private void SeedFullLedger(CredentialRevisionLedger ledger)
        => File.WriteAllBytes(Ledger, JsonSerializer.SerializeToUtf8Bytes(ledger with
        {
            Revisions = new[] { _frozen }.Concat(Enumerable.Range(0, 998).Select(i => Hash("revision-" + i))).Append(ledger.CurrentRevision).ToArray()
        }, CredentialRefreshJsonContext.Default.CredentialRevisionLedger));
    private RcloneExecutionContext Context() => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cscript.exe"),
        _source, _root, "test:backup", _tasks, true);
    private CredentialRevisionLedger ReadLedger() => JsonSerializer.Deserialize(File.ReadAllBytes(Ledger), CredentialRefreshJsonContext.Default.CredentialRevisionLedger)!;

    [TestMethod]
    public async Task MultipleRolloversRetainOriginAndOldFrozenJobsAndRejectUnknownRevisions()
    {
        for (var i = 0; i < 2; i++)
        {
            using var context = Context();
            await using var lease = await RcloneCredentialRefresh.BeginAsync(context.CreateStartInfo(["version"]), default);
            File.WriteAllText(context.SnapshotPath, File.ReadAllText(_source).Replace("old", "old-next"));
            await lease!.CompleteAsync();
            var ledger = ReadLedger();
            Assert.AreEqual(2, ledger.Version); Assert.IsNotNull(ledger.PreviousBlock);
            Assert.HasCount(1, ledger.Revisions);
            Assert.IsTrue(RcloneCredentialStore.HasFreshOrigin(_source, true));
            Assert.IsTrue(RcloneCredentialRefresh.AcceptsFrozenRevision(_source, _frozen, File.ReadAllBytes(_source)));
            Assert.IsFalse(RcloneCredentialRefresh.AcceptsFrozenRevision(_source, Hash("untrusted"), File.ReadAllBytes(_source)));
            if (i == 0) SeedFullLedger(ledger);
        }
        Assert.HasCount(2, Directory.GetFiles(_source + ".folderrewind-revision-blocks"));
        await RcloneCredentialRefresh.PrepareStoreAsync(_source, _tasks, true, default);
    }

    [TestMethod]
    public async Task ReplayAtTheOldLimitCompletesAfterSourceWriteBeforeLedgerPublication()
    {
        string snapshot;
        using (var context = Context())
        {
            snapshot = context.SnapshotPath;
            await using var lease = await RcloneCredentialRefresh.BeginAsync(context.CreateStartInfo(["version"]), default);
            var updated = File.ReadAllText(_source).Replace("old", "new");
            File.WriteAllText(snapshot, updated); File.WriteAllText(_source, updated);
            var attemptPath = Path.Combine(context.TaskDirectory, "attempt.json");
            var attempt = JsonSerializer.Deserialize(File.ReadAllBytes(attemptPath), CredentialRefreshJsonContext.Default.CredentialRefreshAttempt)!;
            File.WriteAllBytes(attemptPath, JsonSerializer.SerializeToUtf8Bytes(attempt with { Phase = "Exited" }, CredentialRefreshJsonContext.Default.CredentialRefreshAttempt));
        }
        await RcloneCredentialRefresh.PrepareStoreAsync(_source, _tasks, true, default);
        Assert.IsFalse(File.Exists(snapshot)); Assert.AreEqual(2, ReadLedger().Version);
        Assert.IsTrue(RcloneCredentialStore.HasFreshOrigin(_source, true));
        Assert.IsTrue(RcloneCredentialRefresh.AcceptsFrozenRevision(_source, _frozen, File.ReadAllBytes(_source)));
    }

    [TestMethod]
    public async Task CheckpointFailureKeepsOriginalCredentialsAndUniqueNewSnapshot()
    {
        var before = File.ReadAllBytes(_source);
        File.WriteAllText(_source + ".folderrewind-revision-blocks", "occupied");
        using var context = Context();
        await using var lease = await RcloneCredentialRefresh.BeginAsync(context.CreateStartInfo(["version"]), default);
        File.WriteAllText(context.SnapshotPath, File.ReadAllText(_source).Replace("old", "new"));
        await Assert.ThrowsExactlyAsync<IOException>(() => lease!.CompleteAsync());
        CollectionAssert.AreEqual(before, File.ReadAllBytes(_source));
        StringAssert.Contains(File.ReadAllText(context.SnapshotPath), "new");
    }
}

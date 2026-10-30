using System.Diagnostics;
using System.Text.Json;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryJournalReadScopeTests
{
    private string _root = null!;
    private HistoryRepositoryPaths _paths = null!;
    private HistoryTransactionJournalStore _store = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindJournalScope", Guid.NewGuid().ToString("N"));
        _paths = new(_root);
        _paths.CreateDirectories();
        _store = new(_paths);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    private static HistoryTransactionJournal Completed()
        => HistoryTransactionJournal.Prepared(HistoryTransactionId.New(), PackId.New()) with { Phase = HistoryTransactionPhase.Complete };

    [TestMethod]
    public async Task CompletedEvidenceIsReusedOnlyWithinScopeAndReleasesReadLocks()
    {
        var journal = Completed(); _store.Save(journal);
        var path = _paths.GetJournalPath(journal.TransactionId);
        var diagnostics = HistoryOperationDiagnostics.Begin("journal-test");
        using (var scope = _store.BeginReadScope())
        {
            _store.RequireRecovered();
            await Task.Yield();
            _store.Load(Path.GetRelativePath(Environment.CurrentDirectory, path));
            if (OperatingSystem.IsWindows())
                Assert.ThrowsExactly<IOException>(() => { using var write = File.OpenWrite(path); });
        }
        diagnostics.Dispose();
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual(1L, diagnostics.Summary!.Counters["history-journal-reads"]);
            Assert.AreEqual(1L, diagnostics.Summary.Counters["history-journal-read-reuses"]);
        }
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [TestMethod]
    public void NewIncompleteJournalIsNotHiddenByEarlierSuccessfulHealthCheck()
    {
        _store.Save(Completed());
        using var scope = _store.BeginReadScope();
        _store.RequireRecovered();
        _store.Save(HistoryTransactionJournal.Prepared(HistoryTransactionId.New(), PackId.New()));
        Assert.ThrowsExactly<InvalidOperationException>(_store.RequireRecovered);
    }

    [TestMethod]
    public async Task SaveInvalidatesBothNestedAndParentEvidence()
    {
        var journal = Completed(); _store.Save(journal);
        using var outer = _store.BeginReadScope();
        _store.RequireRecovered();
        using (var inner = _store.BeginReadScope())
        {
            _store.RequireRecovered();
            _store.Save(journal with { Phase = HistoryTransactionPhase.Prepared });
            Assert.ThrowsExactly<InvalidOperationException>(_store.RequireRecovered);
        }
        Assert.ThrowsExactly<InvalidOperationException>(_store.RequireRecovered);
        int applied = 0, rolledBack = 0;
        await _store.RecoverAsync((_, _) => { applied++; return Task.CompletedTask; },
            (_, _) => { rolledBack++; return Task.CompletedTask; });
        _store.RequireRecovered();
        await _store.RecoverAsync((_, _) => { applied++; return Task.CompletedTask; },
            (_, _) => { rolledBack++; return Task.CompletedTask; });
        Assert.AreEqual(0, applied);
        Assert.AreEqual(1, rolledBack); // Missing final pack still rolls back exactly once.
    }

    [TestMethod]
    public void SameLengthAndTimestampCorruptionBetweenScopesIsDetected()
    {
        var journal = Completed(); _store.Save(journal);
        var path = _paths.GetJournalPath(journal.TransactionId);
        using (var scope = _store.BeginReadScope()) _store.RequireRecovered();
        var timestamp = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path); bytes[0] = (byte)'!';
        File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, timestamp);
        using var next = _store.BeginReadScope();
        Assert.ThrowsExactly<JsonException>(_store.RequireRecovered);
    }

    [TestMethod]
    public void ExceptionalExitReleasesLeases()
    {
        var journal = Completed(); _store.Save(journal);
        Assert.ThrowsExactly<OperationCanceledException>(() =>
        {
            using var scope = _store.BeginReadScope();
            _store.RequireRecovered();
            throw new OperationCanceledException();
        });
        using var exclusive = new FileStream(_paths.GetJournalPath(journal.TransactionId), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [TestMethod]
    public void ParentJunctionIsProtectedDuringReuseAndRevalidatedAfterRelease()
    {
        if (!OperatingSystem.IsWindows()) return;
        var oldDirectory = Path.Combine(_root, "old"); var newDirectory = Path.Combine(_root, "new");
        Directory.CreateDirectory(oldDirectory); Directory.CreateDirectory(newDirectory);
        var oldPaths = new HistoryRepositoryPaths(oldDirectory); var newPaths = new HistoryRepositoryPaths(newDirectory);
        oldPaths.CreateDirectories(); newPaths.CreateDirectories();
        var journal = Completed();
        new HistoryTransactionJournalStore(oldPaths).Save(journal);
        new HistoryTransactionJournalStore(newPaths).Save(journal);
        File.WriteAllText(newPaths.GetJournalPath(journal.TransactionId), "{ invalid new journal }");
        var link = Path.Combine(_root, "link");
        void Link(string target)
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            process.WaitForExit(); Assert.AreEqual(0, process.ExitCode);
        }
        Link(oldDirectory);
        try
        {
            var aliasPaths = new HistoryRepositoryPaths(link);
            var aliasStore = new HistoryTransactionJournalStore(aliasPaths);
            var path = aliasPaths.GetJournalPath(journal.TransactionId);
            using (var scope = aliasStore.BeginReadScope())
            {
                Assert.AreSame(aliasStore.Load(path), aliasStore.Load(path));
                Assert.ThrowsExactly<IOException>(() => Directory.Delete(link));
            }
            Directory.Delete(link); Link(newDirectory);
            using var next = aliasStore.BeginReadScope();
            Assert.ThrowsExactly<JsonException>(() => aliasStore.Load(path));
        }
        finally { Directory.Delete(link); }
    }
}

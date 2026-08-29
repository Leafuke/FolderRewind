using FolderRewind.History.Application;
using FolderRewind.History.Domain;

namespace FolderRewind.Tests;

[TestClass]
public sealed class FileSystemHistoryRestoreMutationBackendTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindRestoreMutationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(entry, FileAttributes.Normal); } catch { }
        }
        Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task CommitDeletesRollbackTreeContainingReadOnlyFilesAndDirectories()
    {
        var (backend, snapshot, file, subdirectory) = await PrepareSnapshotAsync();
        File.SetAttributes(file, FileAttributes.ReadOnly);
        File.SetAttributes(subdirectory, File.GetAttributes(subdirectory) | FileAttributes.ReadOnly);

        await backend.CommitAsync(snapshot, CancellationToken.None);

        Assert.IsFalse(Directory.Exists(snapshot.RollbackDirectory));
    }

    [TestMethod]
    public async Task CommitRetriesUntilTransientFileLockIsReleased()
    {
        var (backend, snapshot, file, _) = await PrepareSnapshotAsync();
        using var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);

        var commit = backend.CommitAsync(snapshot, CancellationToken.None);
        await Task.Delay(80);
        locked.Dispose();
        await commit;

        Assert.IsFalse(Directory.Exists(snapshot.RollbackDirectory));
    }

    [TestMethod]
    public async Task PermanentFileLockLeavesRollbackTreeForJournalRecovery()
    {
        var (backend, snapshot, file, _) = await PrepareSnapshotAsync();
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Exception? failure = null;
            try { await backend.CommitAsync(snapshot, CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure = ex; }
            Assert.IsNotNull(failure);
            Assert.IsTrue(Directory.Exists(snapshot.RollbackDirectory));
        }

        await backend.CommitAsync(snapshot, CancellationToken.None);
        Assert.IsFalse(Directory.Exists(snapshot.RollbackDirectory));
    }

    private async Task<(FileSystemHistoryRestoreMutationBackend Backend,
        HistoryRestoreRollbackSnapshot Snapshot, string File, string Subdirectory)> PrepareSnapshotAsync()
    {
        var target = Path.Combine(_root, "target-" + Guid.NewGuid().ToString("N"));
        var subdirectory = Path.Combine(target, "sub");
        Directory.CreateDirectory(subdirectory);
        var file = Path.Combine(subdirectory, "locked.txt");
        await File.WriteAllTextAsync(file, "content");
        var backend = new FileSystemHistoryRestoreMutationBackend();
        var snapshot = backend.PlanRollback(
            new HistoryRestoreSourceBinding(SourceId.New(), target),
            HistoryTransactionId.New());
        await backend.PrepareRollbackAsync(snapshot, CancellationToken.None);
        return (backend, snapshot, Path.Combine(snapshot.RollbackDirectory, "sub", "locked.txt"),
            Path.Combine(snapshot.RollbackDirectory, "sub"));
    }
}

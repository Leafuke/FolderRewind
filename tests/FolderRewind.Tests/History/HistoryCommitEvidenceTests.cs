using FolderRewind.History.Application;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryCommitEvidenceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InstalledPackFailureNeverReopensArchiveCleanup(bool unreadablePack)
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-commit-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new HistoryConfigId(Guid.NewGuid().ToString("N"));
            var source = SourceId.New();
            var repository = new FileHistoryRepository(config, new(Path.Combine(root, "history")));
            await using var runtime = new HistoryRuntime(repository);
            await runtime.InitializeAsync();
            var payload = Path.Combine(root, "capture.7z");
            File.WriteAllText(payload, "verified capture bytes");
            var capture = VerifiedArchiveCaptureFactory.Create(source, CaptureScope.FullSource, payload,
                RepresentationKind.CoreFull, "7z", new Dictionary<string, SourceCaptureFileState>(), null, [], 0, MaterializationFidelity.Exact);
            var callback = repository.PacksInstalled;
            FileStream? held = null;
            PackId installed = default;
            repository.PacksInstalled = async (old, added, token) =>
            {
                if (callback is not null) await callback(old, added, token);
                var pack = added.Single().Pack; installed = pack.PackId;
                held = new FileStream(unreadablePack ? repository.Paths.GetPackPath(pack.PackId)
                    : repository.Paths.GetJournalPath(pack.TransactionId), FileMode.Open, FileAccess.Read,
                    unreadablePack ? FileShare.None : FileShare.Read);
                if (unreadablePack) throw new IOException("Post-install fault with unreadable evidence");
            };
            var now = DateTimeOffset.UtcNow;
            var request = new HistoryCommitRequest(new(config, [new(source, new("source", root))]),
                new(RunId.New(), now, now, BackupInvocationKind.Manual, HistoryProvenance.Native("test")), null, [capture]);
            try
            {
                if (unreadablePack) await Assert.ThrowsExactlyAsync<HistoryCommitOutcomeUnknownException>(() => runtime.Commit.CommitAsync(request));
                else await Assert.ThrowsExactlyAsync<HistoryCommitRecoveryRequiredException>(() => runtime.Commit.CommitAsync(request));
                // Even a defensive/duplicated caller cleanup cannot delete transferred bytes.
                await capture.CleanupHandle!.CleanupAsync(default);
                Assert.IsTrue(File.Exists(payload));
            }
            finally { held?.Dispose(); repository.PacksInstalled = callback; }
            for (int repeat = 0; repeat < 2; repeat++) await runtime.InitializeAsync();
            Assert.HasCount(1, await runtime.Query.GetAllVersionsAsync());
            Assert.AreEqual(installed, (await repository.ReadAllPacksAsync()).Single().Pack.PackId);
            Assert.IsTrue(File.Exists(payload));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RejectedCaptureStillOwnsAndRemovesItsArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-rejected-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new HistoryConfigId(Guid.NewGuid().ToString("N"));
            await using var runtime = new HistoryRuntime(new(config, new(Path.Combine(root, "history"))));
            await runtime.InitializeAsync();
            var path = Path.Combine(root, "capture.7z"); File.WriteAllText(path, "candidate");
            var source = SourceId.New();
            var capture = VerifiedArchiveCaptureFactory.Create(source, CaptureScope.FullSource, path, RepresentationKind.CoreFull,
                "7z", new Dictionary<string, SourceCaptureFileState>(), null, [], 0, MaterializationFidelity.Exact);
            var now = DateTimeOffset.UtcNow;
            await Assert.ThrowsExactlyAsync<HistoryCommitConflictException>(() => runtime.Commit.CommitAsync(new(
                new(new HistoryConfigId("wrong-config"), [new(source, new("source", root))]),
                new(RunId.New(), now, now, BackupInvocationKind.Manual, HistoryProvenance.Native("test")), null, [capture])));
            Assert.IsFalse(File.Exists(path));
        }
        finally { Directory.Delete(root, true); }
    }
}

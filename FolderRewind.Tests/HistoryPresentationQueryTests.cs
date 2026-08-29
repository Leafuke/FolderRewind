using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryPresentationQueryTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindPresentationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task TimelineKeepsParentlessAndDivergentVersionsWhileMultiTipCommandsAreDisabled()
    {
        var configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        await using var runtime = new HistoryRuntime(new FileHistoryRepository(
            configId, new HistoryRepositoryPaths(Path.Combine(_root, "repository"))));
        await runtime.InitializeAsync();
        var sourceId = SourceId.New();
        var first = Version(configId, sourceId, "first");
        var second = Version(configId, sourceId, "second");
        var checkpointOne = Checkpoint(configId, sourceId, first);
        var checkpointTwo = Checkpoint(configId, sourceId, second);
        var branchId = BranchId.New();
        var tipOne = new BranchUpdate(
            BranchUpdateId.New(), branchId, [], "main", checkpointOne.CheckpointId, false,
            DateTimeOffset.UtcNow, BranchUpdateReason.Created);
        var tipTwo = new BranchUpdate(
            BranchUpdateId.New(), branchId, [], "main", checkpointTwo.CheckpointId, false,
            DateTimeOffset.UtcNow.AddTicks(1), BranchUpdateReason.Backup);
        var codec = new HistoryPackCodec();
        await runtime.Repository.CommitAsync(new HistoryCommitPack(
            PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            new object[] { first, second, checkpointOne, checkpointTwo, tipOne, tipTwo }
                .Select(item => codec.CreateObject(item))));

        var snapshot = await new HistoryPresentationQueryService(runtime).QueryAsync();

        CollectionAssert.AreEquivalent(
            new[] { first.VersionId, second.VersionId },
            snapshot.Timeline.Select(item => item.VersionId).ToArray());
        Assert.IsTrue(snapshot.Timeline.All(item => item.ParentVersionIds.IsEmpty));
        var branch = snapshot.Branches.Single();
        Assert.IsTrue(branch.IsMultiTip);
        Assert.IsFalse(branch.CanRename);
        Assert.IsFalse(branch.CanDelete);
        Assert.IsTrue(branch.CanCheckout);
    }

    [TestMethod]
    public async Task CompletedRunStillReportsPartialCaptureFromItsSourceVersion()
    {
        var configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        await using var runtime = new HistoryRuntime(new FileHistoryRepository(
            configId, new HistoryRepositoryPaths(Path.Combine(_root, "partial-run-repository"))));
        await runtime.InitializeAsync();
        var sourceId = SourceId.New();
        var version = new SourceVersion(
            VersionId.New(), configId, sourceId, [], DateTimeOffset.UtcNow, null,
            CaptureScope.PartialSource, CaptureOutcome.Captured, [],
            new SourceDescriptorSnapshot("partial", "partial"), null, HistoryProvenance.Native("test"));
        var run = new BackupRun(
            RunId.New(), configId, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow,
            BackupInvocationKind.Manual, BackupRunOutcome.Completed,
            [new BackupRunSourceResult(sourceId, BackupRunSourceOutcome.Captured, version.VersionId, [])],
            resultCheckpointId: null,
            diagnostics: []);
        var codec = new HistoryPackCodec();
        await runtime.Repository.CommitAsync(new HistoryCommitPack(
            PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            [codec.CreateObject(version), codec.CreateObject(run)]));

        var snapshot = await new HistoryPresentationQueryService(runtime).QueryAsync();

        Assert.IsTrue(snapshot.Runs.Single().HasPartialCapture);
    }

    private static SourceVersion Version(HistoryConfigId configId, SourceId sourceId, string name)
        => new(
            VersionId.New(), configId, sourceId, [], DateTimeOffset.UtcNow, null,
            CaptureScope.FullSource, CaptureOutcome.Recovered, [],
            new SourceDescriptorSnapshot(name, name), name, HistoryProvenance.Native("test"));

    private static ConfigurationCheckpoint Checkpoint(
        HistoryConfigId configId,
        SourceId sourceId,
        SourceVersion version)
        => new(
            CheckpointId.New(), configId, DateTimeOffset.UtcNow, null, HistoryProvenance.Native("test"),
            [new CheckpointSource(
                sourceId, version.SourceDescriptorSnapshot, version.VersionId,
                CheckpointSourceDisposition.CarriedForward)]);
}

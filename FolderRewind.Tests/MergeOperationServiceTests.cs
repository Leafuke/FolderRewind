using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MergeOperationServiceTests
{
    private AppConfig _previous = null!;
    [TestInitialize]
    public void Initialize() => _previous = ConfigService.CurrentConfig;
    [TestCleanup]
    public void Cleanup() => ConfigService.CurrentConfig = _previous;

    private static BackupConfig Register(HistoryMergeArchiveFixture f)
    {
        var config = new BackupConfig { Id = f.Config.Value, DestinationPath = Path.Combine(f.Root, "backups"),
            SourceFolders = [new() { Path = f.Target }] };
        ConfigService.CurrentConfig = new() { BackupConfigs = [config] };
        NativeHistoryCoreGateway.Runtime = f.History;
        NativeHistoryApplicationService.RestoreFactory = _ => f.Restore();
        NativeHistoryApplicationService.Start = (_, _, _) => Task.FromResult<MergeSession?>(null);
        NativeHistoryApplicationService.ReviewFactory = async (_, session, token) =>
            new(await f.Builder().BuildAsync(session, token), [], [], [], false);
        NativeHistoryApplicationService.Recompute = (current, session, token) =>
            new HistoryMergeService(f.History, f.Restore()).RecomputeAsync(session, NativeHistoryConfigLease.Signature(current),
                session.Plan.Bindings.Select(b => b with { TargetDirectory = current.SourceFolders.Single().Path }).ToArray(), token);
        return config;
    }

    [TestMethod]
    public async Task ReplacedConfigurationInvalidatesSessionAndRecomputeUsesNewPath()
    {
        await using var f = new HistoryMergeArchiveFixture();
        await f.SeedAsync();
        var original = Register(f);
        var session = (await new HistoryMergeService(f.History, f.Restore()).StartAsync(f.Session.Plan.Theirs.BranchId,
            NativeHistoryConfigLease.Signature(original), f.Session.Plan.Bindings))!;
        var restoredWith = new List<BackupConfig>();
        NativeHistoryApplicationService.RestoreFactory = config => { restoredWith.Add(config); return f.Restore(); };
        var service = MergeOperationService.Get(original, f.Source);
        await service.LoadAsync(session.Id);
        Assert.IsNull(service.Snapshot.Error);
        Assert.AreEqual(MergeSessionState.Ready, service.Snapshot.Session!.State);
        await service.GenerateReviewAsync();
        Assert.IsNull(service.Snapshot.Error);
        Assert.IsNotNull(service.Review);

        var replacement = new BackupConfig { Id = original.Id, DestinationPath = Path.Combine(f.Root, "new-backups"),
            SourceFolders = [new() { Path = Path.Combine(f.Root, "new-source") }] };
        ConfigService.CurrentConfig.BackupConfigs[0] = replacement;
        Assert.AreSame(service, MergeOperationService.Get(replacement, f.Source));
        await service.LoadAsync(session.Id);
        Assert.IsNull(service.Snapshot.Error);
        Assert.AreSame(replacement, service.Config);
        Assert.IsNull(service.Review, "A review prepared with the former configuration cannot be applied.");
        Assert.AreEqual(MergeSessionState.Stale, service.Snapshot.Session!.State);
        Assert.AreEqual(MergeSessionState.Stale, service.Sessions.Single(s => s.Id == session.Id).State);
        CollectionAssert.AreEqual(new[] { original, replacement }, restoredWith.ToArray());

        await service.RecomputeAsync();
        Assert.IsNull(service.Snapshot.Error);
        Assert.AreEqual(MergeSessionState.Ready, service.Snapshot.Session!.State);
        Assert.AreEqual(NativeHistoryConfigLease.Signature(replacement), service.Snapshot.Session.Plan.ConfigRevision);
        Assert.AreEqual(replacement.SourceFolders.Single().Path, service.Snapshot.Session.Plan.Bindings.Single().TargetDirectory);
    }

    [TestMethod]
    public async Task InPlaceChangesRecreateRestoreServiceButUnchangedLoadsReuseIt()
    {
        await using var f = new HistoryMergeArchiveFixture();
        await f.SeedAsync();
        var config = Register(f);
        var restores = 0;
        NativeHistoryApplicationService.RestoreFactory = _ => { restores++; return f.Restore(); };
        var service = MergeOperationService.Get(config, f.Source);
        await service.LoadAsync();
        await service.LoadAsync();
        Assert.AreEqual(1, restores);
        config.SourceFolders.Single().Path = Path.Combine(f.Root, "moved-source");
        await service.LoadAsync();
        Assert.IsNull(service.Snapshot.Error);
        Assert.AreEqual(2, restores, "The signature must be captured before an in-place edit.");
    }

    [TestMethod]
    public async Task RefreshKeepsRunningOperationAndUnsavedDecisionsOwnedBySameService()
    {
        await using var f = new HistoryMergeArchiveFixture();
        await f.SeedAsync();
        var original = Register(f);
        var service = MergeOperationService.Get(original, f.Source);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeHistoryApplicationService.Start = async (config, _, _) =>
        { Assert.AreSame(original, config); started.SetResult(); await finish.Task; return null; };
        var running = service.StartAsync(BranchId.New());
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var replacement = new BackupConfig { Id = original.Id, DestinationPath = "changed", SourceFolders = original.SourceFolders };
            ConfigService.CurrentConfig.BackupConfigs[0] = replacement;
            Assert.AreSame(service, MergeOperationService.Get(replacement, f.Source));
            Assert.AreSame(running, service.Tracker.PendingTask);
            Assert.AreSame(original, service.Config, "Refresh must wait until the running operation finishes.");
        }
        finally { finish.TrySetResult(); await running; }
        await service.LoadAsync(f.Session.Id);
        // A failed save retains its exact retry callback. Refresh must not silently discard it.
        await service.ClearAsync(["missing-conflict"]);
        Assert.IsTrue(service.CanRetrySave);
        Assert.IsFalse(service.Snapshot.IsSaved);
        ConfigService.CurrentConfig.BackupConfigs[0] = new() { Id = original.Id, DestinationPath = "changed-again" };
        await service.LoadAsync(f.Session.Id);
        Assert.IsTrue(service.CanRetrySave);
        Assert.IsFalse(service.Snapshot.IsSaved);
    }

    [TestMethod]
    public async Task RemovedConfigurationBlocksNewOperation()
    {
        await using var f = new HistoryMergeArchiveFixture();
        await f.SeedAsync();
        var config = Register(f);
        var started = false;
        NativeHistoryApplicationService.Start = (_, _, _) => { started = true; return Task.FromResult<MergeSession?>(null); };
        var service = MergeOperationService.Get(config, f.Source);
        ConfigService.CurrentConfig.BackupConfigs.Clear();
        await service.StartAsync(BranchId.New());
        Assert.IsFalse(started);
        Assert.IsNotNull(service.Snapshot.Error);
    }
}

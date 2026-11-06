using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Models;
using FolderRewind.Services;
using System.Collections.Concurrent;

namespace FolderRewind.Tests;

[TestClass]
[TestCategory("ExternalTool")]
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

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task ResolveClearUndoAndImportPublishTheirExactChangedIdsWithTheNewSession(int conflictCount)
    {
        await using var f = new HistoryMergeArchiveFixture();
        var (service, _, conflicts) = await LoadConflictsAsync(f, conflictCount);
        using var observer = new DecisionObserver(service);
        var conflict = conflicts[0];
        var before = service.Snapshot.Session!;
        var mark = observer.Count;
        await service.ResolveAsync([new(before.Plan.Revision, conflict.Id, conflict.InputSignature, MergeResolutionChoice.Theirs)]);
        AssertDecisionChange(service, before, conflict.Id);
        observer.AssertDecisionPublication(mark, before, service.Snapshot.Session!);
        Assert.AreEqual(MergeResolutionChoice.Theirs, f.History.MergeSessions.GetConflict(service.Snapshot.Session!, conflict.Id).Resolution!.Choice);

        before = service.Snapshot.Session!; mark = observer.Count;
        await service.ClearAsync([conflict.Id, conflict.Id]);
        AssertDecisionChange(service, before, conflict.Id);
        observer.AssertDecisionPublication(mark, before, service.Snapshot.Session!);
        Assert.IsNull(f.History.MergeSessions.GetConflict(service.Snapshot.Session!, conflict.Id).Resolution);

        before = service.Snapshot.Session!; mark = observer.Count;
        await service.UndoAsync();
        AssertDecisionChange(service, before, conflict.Id);
        observer.AssertDecisionPublication(mark, before, service.Snapshot.Session!);
        Assert.IsFalse(service.CanUndo);
        Assert.AreEqual(MergeResolutionChoice.Theirs, f.History.MergeSessions.GetConflict(service.Snapshot.Session!, conflict.Id).Resolution!.Choice);

        var path = Path.Combine(f.Root, "manual-result.txt");
        await File.WriteAllTextAsync(path, "manual result\r\n");
        before = service.Snapshot.Session!; mark = observer.Count;
        await service.ImportAsync(conflict, path);
        AssertDecisionChange(service, before, conflict.Id);
        observer.AssertDecisionPublication(mark, before, service.Snapshot.Session!);
        var manual = f.History.MergeSessions.GetConflict(service.Snapshot.Session!, conflict.Id).Resolution!;
        Assert.AreEqual(MergeResolutionChoice.Manual, manual.Choice);
        CollectionAssert.AreEqual(await File.ReadAllBytesAsync(path), await File.ReadAllBytesAsync(manual.Manual!.Handle));
        Assert.AreEqual(conflictCount == 1 ? MergeSessionState.Ready : MergeSessionState.Resolving, service.Snapshot.Session!.State);
        if (conflictCount > 1)
        {
            Assert.IsNull(f.History.MergeSessions.GetConflict(service.Snapshot.Session!, conflicts[1].Id).Resolution);
            before = service.Snapshot.Session!; mark = observer.Count;
            await service.ResolveAsync(conflicts.Select(c => new MergeResolution(before.Plan.Revision, c.Id, c.InputSignature, MergeResolutionChoice.Ours)).ToArray());
            AssertDecisionChange(service, before, conflicts.Select(c => c.Id).ToArray());
            observer.AssertDecisionPublication(mark, before, service.Snapshot.Session!);
            before = service.Snapshot.Session!; mark = observer.Count;
            await service.UndoAsync();
            AssertDecisionChange(service, before, conflicts.Select(c => c.Id).ToArray());
            observer.AssertDecisionPublication(mark, before, service.Snapshot.Session!);
            Assert.AreEqual(MergeResolutionChoice.Manual, f.History.MergeSessions.GetConflict(service.Snapshot.Session!, conflict.Id).Resolution!.Choice);
            Assert.IsNull(f.History.MergeSessions.GetConflict(service.Snapshot.Session!, conflicts[1].Id).Resolution);
        }
        observer.AssertAtomic();
    }

    [TestMethod]
    public async Task SameRevisionLoadsRetainDecisionMetadataWhileRecomputeAndSessionSwitchDiscardIt()
    {
        await using var f = new HistoryMergeArchiveFixture();
        var (service, _, conflicts) = await LoadConflictsAsync(f);
        using var observer = new DecisionObserver(service);
        var conflict = conflicts[0]; var before = service.Snapshot.Session!;
        await service.ResolveAsync([new(before.Plan.Revision, conflict.Id, conflict.InputSignature, MergeResolutionChoice.Ours)]);
        AssertDecisionChange(service, before, conflict.Id);
        var change = service.Snapshot.DecisionChange;
        await service.LoadAsync();
        Assert.IsNull(service.Snapshot.Error); Assert.AreSame(change, service.Snapshot.DecisionChange);
        await service.LoadAsync(service.Snapshot.Session!.Id);
        Assert.IsNull(service.Snapshot.Error); Assert.AreSame(change, service.Snapshot.DecisionChange);

        var oldPlan = service.Snapshot.Session!.Plan.Revision;
        await service.RecomputeAsync();
        Assert.IsNull(service.Snapshot.Error); Assert.IsNull(service.Snapshot.DecisionChange);
        Assert.AreNotEqual(oldPlan, service.Snapshot.Session!.Plan.Revision);
        before = service.Snapshot.Session!;
        conflict = f.History.MergeSessions.GetConflict(before, conflict.Id).Conflict;
        await service.ResolveAsync([new(before.Plan.Revision, conflict.Id, conflict.InputSignature, MergeResolutionChoice.Theirs)]);
        AssertDecisionChange(service, before, conflict.Id);
        var other = await StartSignedSessionAsync(f, service.Config);
        await service.LoadAsync(other.Id);
        Assert.IsNull(service.Snapshot.Error); Assert.IsNull(service.Snapshot.DecisionChange);
        Assert.AreEqual(other.Id, service.Snapshot.Session!.Id);
        observer.AssertAtomic();
    }

    [TestMethod]
    public async Task ExternalResolutionRevisionInvalidatesThePreviouslyPublishedDecisionMetadata()
    {
        await using var f = new HistoryMergeArchiveFixture();
        var (service, _, conflicts) = await LoadConflictsAsync(f);
        using var observer = new DecisionObserver(service);
        var conflict = conflicts[0]; var before = service.Snapshot.Session!;
        await service.ResolveAsync([new(before.Plan.Revision, conflict.Id, conflict.InputSignature, MergeResolutionChoice.Ours)]);
        AssertDecisionChange(service, before, conflict.Id);
        var published = service.Snapshot.Session!;
        var external = f.History.MergeSessions.Resolve(published,
            new(published.Plan.Revision, conflict.Id, conflict.InputSignature, MergeResolutionChoice.Theirs));
        await service.LoadAsync();
        Assert.IsNull(service.Snapshot.Error); Assert.IsNull(service.Snapshot.DecisionChange);
        AssertSessionVersion(external, service.Snapshot.Session!);
        observer.AssertAtomic();
    }

    [TestMethod]
    public async Task FailedSaveAndFailedRetryPreserveValidMetadataAndUnsavedOperationState()
    {
        await using var f = new HistoryMergeArchiveFixture();
        var (service, _, conflicts) = await LoadConflictsAsync(f);
        using var observer = new DecisionObserver(service);
        var conflict = conflicts[0]; var before = service.Snapshot.Session!;
        await service.ResolveAsync([new(before.Plan.Revision, conflict.Id, conflict.InputSignature, MergeResolutionChoice.Ours)]);
        AssertDecisionChange(service, before, conflict.Id);
        var change = service.Snapshot.DecisionChange;
        var saved = service.Snapshot.Session!;
        var mark = observer.Count;
        await service.ClearAsync(["missing-conflict"]);
        Assert.IsNotNull(service.Snapshot.Error); Assert.IsFalse(service.Snapshot.IsSaved); Assert.IsTrue(service.CanRetrySave);
        Assert.AreSame(change, service.Snapshot.DecisionChange); AssertSessionVersion(saved, service.Snapshot.Session!);
        Assert.IsTrue(observer.Since(mark).Any(s => s.Stage == MergeOperationStage.Saving && !s.IsSaved));
        await service.RetrySaveAsync();
        Assert.IsNotNull(service.Snapshot.Error); Assert.IsFalse(service.Snapshot.IsSaved); Assert.IsTrue(service.CanRetrySave);
        Assert.AreSame(change, service.Snapshot.DecisionChange); AssertSessionVersion(saved, service.Snapshot.Session!);
        await service.LoadAsync(saved.Id);
        Assert.IsNull(service.Snapshot.Error); Assert.IsFalse(service.Snapshot.IsSaved); Assert.IsTrue(service.CanRetrySave);
        Assert.AreSame(change, service.Snapshot.DecisionChange);
        observer.AssertAtomic();
    }

    [TestMethod]
    public async Task StartingAnotherSessionClearsOldDecisionMetadataInTheSamePublication()
    {
        await using var f = new HistoryMergeArchiveFixture();
        var (service, _, conflicts) = await LoadConflictsAsync(f);
        using var observer = new DecisionObserver(service);
        var conflict = conflicts[0]; var before = service.Snapshot.Session!;
        await service.ResolveAsync([new(before.Plan.Revision, conflict.Id, conflict.InputSignature, MergeResolutionChoice.Ours)]);
        AssertDecisionChange(service, before, conflict.Id);
        var other = await StartSignedSessionAsync(f, service.Config);
        NativeHistoryApplicationService.Start = (_, _, _) => Task.FromResult<MergeSession?>(other);
        var mark = observer.Count;
        await service.StartAsync(other.Plan.Theirs.BranchId);
        Assert.IsNull(service.Snapshot.Error); Assert.AreEqual(other.Id, service.Snapshot.Session!.Id);
        Assert.IsNull(service.Snapshot.DecisionChange);
        Assert.IsTrue(observer.Since(mark).Any(s => s.Session is null && s.DecisionChange is null));
        observer.AssertAtomic();
    }

    private static async Task<(MergeOperationService Service, MergeSession Session, MergeConflict[] Conflicts)> LoadConflictsAsync(
        HistoryMergeArchiveFixture fixture, int count = 1)
    {
        await fixture.SeedAsync((root, side) =>
        {
            for (var i = 0; i < count; i++) File.WriteAllText(Path.Combine(root, $"shared-{i}.txt"), $"side-{side}-content-{i}");
        }, expectedState: MergeSessionState.Resolving);
        var config = Register(fixture);
        var session = await StartSignedSessionAsync(fixture, config);
        var service = MergeOperationService.Get(config, fixture.Source);
        await service.LoadAsync(session.Id);
        Assert.IsNull(service.Snapshot.Error); Assert.AreEqual(MergeSessionState.Resolving, service.Snapshot.Session!.State);
        var conflicts = new HistoryMergeService(fixture.History, fixture.Restore()).AllConflicts(session)
            .Select(r => r.Conflict).OrderBy(c => c.Subject.Paths[0], StringComparer.Ordinal).ToArray();
        Assert.HasCount(count, conflicts);
        return (service, service.Snapshot.Session!, conflicts);
    }

    private static async Task<MergeSession> StartSignedSessionAsync(HistoryMergeArchiveFixture fixture, BackupConfig config)
        => (await new HistoryMergeService(fixture.History, fixture.Restore()).StartAsync(fixture.Session.Plan.Theirs.BranchId,
            NativeHistoryConfigLease.Signature(config), fixture.Session.Plan.Bindings))!;

    private static void AssertDecisionChange(MergeOperationService service, MergeSession before, params string[] ids)
    {
        Assert.IsNull(service.Snapshot.Error); Assert.IsTrue(service.Snapshot.IsSaved);
        var after = service.Snapshot.Session!;
        var change = service.Snapshot.DecisionChange;
        Assert.IsNotNull(change); AssertSessionVersion(before, change.Before); AssertSessionVersion(after, change.After);
        Assert.AreEqual(before.Id, after.Id); Assert.AreEqual(before.Plan.Revision, after.Plan.Revision);
        Assert.AreEqual(before.Revision + 1, after.Revision);
        CollectionAssert.AreEquivalent(ids, change.Ids.ToArray());
    }

    private static void AssertSessionVersion(MergeSession expected, MergeSession actual)
    {
        Assert.AreEqual(expected.Id, actual.Id); Assert.AreEqual(expected.Plan.Revision, actual.Plan.Revision);
        Assert.AreEqual(expected.Revision, actual.Revision); Assert.AreEqual(expected.State, actual.State);
    }

    // Tracker deliberately isolates observer exceptions. Capture snapshots first and assert on
    // the test thread so a failed atomic-publication assertion cannot be swallowed by production.
    private sealed class DecisionObserver : IDisposable
    {
        private readonly MergeOperationService _service;
        private readonly ConcurrentQueue<MergeOperationSnapshot> _snapshots = new();
        internal DecisionObserver(MergeOperationService service)
        { _service = service; service.Tracker.Changed += Capture; Capture(); }
        private void Capture() => _snapshots.Enqueue(_service.Snapshot);
        internal int Count => _snapshots.Count;
        internal MergeOperationSnapshot[] Since(int mark) => _snapshots.ToArray().Skip(mark).ToArray();
        internal void AssertAtomic()
        {
            foreach (var snapshot in _snapshots)
                if (snapshot.DecisionChange is { } change)
                { Assert.IsNotNull(snapshot.Session); AssertSessionVersion(snapshot.Session, change.After); }
        }
        internal void AssertDecisionPublication(int mark, MergeSession before, MergeSession after)
        {
            var published = Since(mark).Where(s => s.Session is { } current && current.Revision == after.Revision && current.Id == after.Id).ToArray();
            Assert.IsNotEmpty(published);
            foreach (var snapshot in published)
            { Assert.IsNotNull(snapshot.DecisionChange); AssertSessionVersion(before, snapshot.DecisionChange.Before); AssertSessionVersion(after, snapshot.DecisionChange.After); }
        }
        public void Dispose() => _service.Tracker.Changed -= Capture;
    }
}

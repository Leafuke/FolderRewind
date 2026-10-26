using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SemanticMergePlanDataTests
{
    [TestMethod]
    [DataRow(true, false, false, false)]
    [DataRow(true, true, false, true)]
    [DataRow(false, false, true, false)]
    [DataRow(false, false, false, true)]
    public void ManualImportAndPersistedDecisionUseTheSameEligibility(bool automatic, bool canOverride, bool blocked, bool allowed)
    {
        var text = new FolderRewind.Plugin.Abstractions.LocalizedText("subject", new Dictionary<string, string>());
        var subject = new FolderRewind.Plugin.Abstractions.MergeSubject("s", "g", "input", text, text,
            [FolderRewind.Plugin.Abstractions.MergeDecisionKind.Manual], automatic, System.Text.Json.JsonSerializer.SerializeToElement(new { }))
            { CanOverrideAutomatic = canOverride, IsBlocked = blocked };
        var tree = MergeTreeManifest.Empty;
        var conflict = new MergeConflict("c", new(SourceId.New(), []), MergeConflictKind.ModifyModify, "input", tree.Files, tree.Files, tree.Files)
            { SemanticSubject = subject };
        Assert.AreEqual(allowed, conflict.CanImportFile);
        Assert.AreEqual(allowed, MergeSessionStore.SemanticChoiceAllowed(subject, MergeResolutionChoice.Manual));
        Assert.IsFalse((conflict with { SemanticSubject = subject with { AllowedDecisions = [] } }).CanImportFile);
    }

    [TestMethod]
    public async Task AutomaticOverridesPersistResetToDefaultAndCannotChangeLockedSubjects()
    {
        using var fixture = new PlanFixture(); using var writer = fixture.Writer();
        var text = new FolderRewind.Plugin.Abstractions.LocalizedText("item", new Dictionary<string, string>());
        var state = System.Text.Json.JsonSerializer.SerializeToElement(new { });
        await writer.AddGroupAsync(new("group", text, state), default);
        await writer.AddSubjectsAsync([
            new("editable", "group", "one", text, text, [FolderRewind.Plugin.Abstractions.MergeDecisionKind.Ours, FolderRewind.Plugin.Abstractions.MergeDecisionKind.Theirs], true, state)
            { CanOverrideAutomatic = true, SpatialLocations = [new(".", "layer", new(-16, -16, 0, 0))] },
            new("locked", "group", "two", text, text, [], true, state)
            { SpatialLocations = [new(".", "layer", new(0, 0, 16, 16))] }
        ], default);
        var session = fixture.Store.Update(fixture.LastSession!, MergeSessionState.Ready);
        var initial = fixture.Store.QuerySemanticChanges(session, "", "Automatic", 0, 100);
        Assert.HasCount(2, initial.Rows);
        var editable = initial.Rows.Single(r => r.Conflict.SemanticSubject!.CanOverrideAutomatic).Conflict;
        var locked = initial.Rows.Single(r => !r.Conflict.SemanticSubject!.CanOverrideAutomatic).Conflict;
        session = fixture.Store.ResolveBatch(session, [new(session.Plan.Revision, editable.Id, editable.InputSignature, MergeResolutionChoice.Theirs)]);
        var reopened = fixture.Store.Load(session.Id);
        Assert.AreEqual(MergeResolutionChoice.Theirs, fixture.Store.GetConflict(reopened, editable.Id).Resolution!.Choice);
        Assert.HasCount(1, fixture.Store.QuerySemanticChanges(reopened, "", "Resolved", 0, 100).Rows);
        Assert.HasCount(1, fixture.Store.SpatialChanges(reopened, ".", "layer", new(-16, -16, 0, 0), 0, 100));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.ResolveBatch(reopened, [
            new(reopened.Plan.Revision, editable.Id, editable.InputSignature, MergeResolutionChoice.Ours),
            new(reopened.Plan.Revision, locked.Id, locked.InputSignature, MergeResolutionChoice.Theirs)]));
        Assert.AreEqual(MergeResolutionChoice.Theirs, fixture.Store.GetConflict(reopened, editable.Id).Resolution!.Choice);
        session = fixture.Store.SetResolutions(reopened, new Dictionary<string, MergeResolution?> { [editable.Id] = null });
        Assert.AreEqual(MergeSessionState.Ready, session.State);
        Assert.AreEqual(MergeResolutionChoice.Automatic, fixture.Store.GetConflict(session, editable.Id).Resolution!.Choice);
        Assert.HasCount(2, fixture.Store.QuerySemanticChanges(session, "", "Automatic", 0, 100).Rows);
    }

    [TestMethod]
    public async Task AnalysisReadbackRequiresClosedOwnedDataAndExpiresWithInvocation()
    {
        using var fixture = new PlanFixture();
        using var cancellation = new CancellationTokenSource();
        var writer = fixture.Writer(cancellation.Token);
        using var other = fixture.Writer();
        try
        {
            await using (var output = await writer.OpenDataWriteAsync("index", default))
            {
                Assert.IsFalse(output.CanRead);
                Assert.ThrowsExactly<NotSupportedException>(() => output.ReadByte());
                await output.WriteAsync(new byte[] { 1, 2, 3 });
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.OpenDataReadAsync("index", default).AsTask());
            }
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => other.OpenDataReadAsync("index", default).AsTask());
            await using (var excessive = await other.OpenDataWriteAsync("budget", default))
            {
                excessive.Position = FolderRewind.Plugin.Abstractions.MergeLimits.Default.MaximumPlanBytes;
                Assert.ThrowsExactly<IOException>(() => excessive.WriteByte(1));
            }
            var input = await writer.OpenDataReadAsync("index", default);
            Assert.IsFalse(input.CanWrite); Assert.AreEqual(1, input.ReadByte());
            cancellation.Cancel();
            Assert.ThrowsExactly<OperationCanceledException>(() => input.ReadByte());
            writer.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(() => input.ReadByte());
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => writer.OpenDataReadAsync("index", default).AsTask());
        }
        finally { writer.Dispose(); }
    }

    [TestMethod]
    public async Task SameLengthIndexMutationIsRejectedBeforeReadbackAndPublication()
    {
        using var fixture = new PlanFixture();
        using var writer = fixture.Writer();
        await using (var output = await writer.OpenDataWriteAsync("index", default)) await output.WriteAsync(new byte[] { 1, 2, 3 });
        await using (var input = await writer.OpenDataReadAsync("index", default)) Assert.AreEqual(1, input.ReadByte());
        var data = Directory.GetFiles(fixture.Store.SessionDirectory(fixture.LastSession!.Id), "*", SearchOption.AllDirectories).Single();
        File.WriteAllBytes(data, [3, 2, 1]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.OpenDataReadAsync("index", default).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.CompleteAsync());
    }

    private sealed class PlanFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "FolderRewindMergePlanTests", Guid.NewGuid().ToString("N"));
        internal MergeSessionStore Store { get; }
        internal MergeSession? LastSession { get; private set; }
        internal PlanFixture() => Store = new(_root);
        internal SemanticMergePlanWriter Writer(CancellationToken token = default)
        {
            var source = SourceId.New(); var version = VersionId.New(); var checkpoint = CheckpointId.New();
            var config = new HistoryConfigId("merge-plan-test");
            var ours = new BranchUpdate(BranchUpdateId.New(), BranchId.New(), [], "ours", checkpoint, false, DateTimeOffset.UtcNow, BranchUpdateReason.Created, sourceId: source);
            var theirs = new BranchUpdate(BranchUpdateId.New(), BranchId.New(), [], "theirs", checkpoint, false, DateTimeOffset.UtcNow, BranchUpdateReason.Created, sourceId: source);
            var identity = new CheckpointSource(source, new("world", "world"), version, CheckpointSourceDisposition.Captured);
            var plan = new HistoryMergePlan(Guid.NewGuid(), HistoryMergeMode.ThreeWay, ours, theirs, checkpoint,
                new HistoryWorkspace(config, 0, Array.Empty<WorkspaceSourceBaseline>()), "config", [],
                [new(source, identity, identity, identity, HistoryMergeSourceAction.MergeFiles, null)]);
            LastSession = Store.Create(plan, [version]);
            return new(Store, LastSession, source, MergeTreeManifest.Empty, MergeTreeManifest.Empty, MergeTreeManifest.Empty, _ => true, token);
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}

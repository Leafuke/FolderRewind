using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryQuickRestoreResolverTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindQuickRestoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task ResolveUsesActiveBranchTipInsteadOfNewestVersionOnAnotherBranch()
    {
        await using var fixture = await CreateFixtureAsync();
        var active = fixture.CreateLineage("active", DateTimeOffset.UtcNow.AddHours(-1));
        var newerOther = fixture.CreateLineage("newer-other", DateTimeOffset.UtcNow);
        await fixture.CommitAsync(
            active.Version, active.Representation, active.Checkpoint, active.Update,
            newerOther.Version, newerOther.Representation, newerOther.Checkpoint, newerOther.Update);
        await fixture.SaveWorkspaceAsync(active.Update);

        var resolution = await fixture.Resolver.ResolveAsync(
            fixture.SourceId,
            AssessmentDepth.Deep);

        Assert.AreEqual(HistoryQuickRestoreResolutionStatus.Ready, resolution.Status);
        Assert.AreEqual(active.Version.VersionId, resolution.VersionId);
        Assert.AreNotEqual(newerOther.Version.VersionId, resolution.VersionId);
    }

    [TestMethod]
    public async Task ResolveRejectsActiveBranchWithMultipleLocalTips()
    {
        await using var fixture = await CreateFixtureAsync();
        var first = fixture.CreateLineage("first", DateTimeOffset.UtcNow.AddMinutes(-1));
        var second = fixture.CreateLineage(
            "second",
            DateTimeOffset.UtcNow,
            branchId: first.Update.BranchId);
        await fixture.CommitAsync(
            first.Version, first.Representation, first.Checkpoint, first.Update,
            second.Version, second.Representation, second.Checkpoint, second.Update);
        await fixture.SaveWorkspaceAsync(first.Update);

        var resolution = await fixture.Resolver.ResolveAsync(
            fixture.SourceId,
            AssessmentDepth.Fast);

        Assert.AreEqual(HistoryQuickRestoreResolutionStatus.BranchReconciliationRequired, resolution.Status);
        Assert.IsNull(resolution.VersionId);
    }

    [TestMethod]
    public async Task ResolveKeepsAnchoredCandidateWhenWorkspaceAlreadyMatchesItExactly()
    {
        await using var fixture = await CreateFixtureAsync();
        var ancestor = fixture.CreateLineage("ancestor", DateTimeOffset.UtcNow.AddMinutes(-1));
        var active = fixture.CreateLineage(
            "active",
            DateTimeOffset.UtcNow,
            branchId: ancestor.Update.BranchId,
            parentUpdateId: ancestor.Update.UpdateId);
        await fixture.CommitAsync(
            ancestor.Version, ancestor.Representation, ancestor.Checkpoint, ancestor.Update,
            active.Version, active.Representation, active.Checkpoint, active.Update);
        await fixture.SaveWorkspaceAsync(active.Update, active.Version.VersionId);

        var resolution = await fixture.Resolver.ResolveAsync(
            fixture.SourceId,
            AssessmentDepth.Deep);

        Assert.AreEqual(HistoryQuickRestoreResolutionStatus.Ready, resolution.Status);
        Assert.AreEqual(active.Version.VersionId, resolution.VersionId);
        Assert.AreNotEqual(ancestor.Version.VersionId, resolution.VersionId);
    }

    [TestMethod]
    public async Task ResolveRejectsCandidateWithoutExactRepresentation()
    {
        await using var fixture = await CreateFixtureAsync();
        var partial = fixture.CreateLineage(
            "partial",
            DateTimeOffset.UtcNow,
            fidelity: MaterializationFidelity.Partial);
        await fixture.CommitAsync(partial.Version, partial.Representation, partial.Checkpoint, partial.Update);
        await fixture.SaveWorkspaceAsync(partial.Update);

        var resolution = await fixture.Resolver.ResolveAsync(
            fixture.SourceId,
            AssessmentDepth.Deep);

        Assert.AreEqual(HistoryQuickRestoreResolutionStatus.RepresentationNotReady, resolution.Status);
        Assert.AreEqual(HistoryReadiness.Blocked, resolution.Readiness);
        Assert.AreEqual(partial.Version.VersionId, resolution.VersionId);
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        var configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        var history = new HistoryRuntime(new FileHistoryRepository(
            configId,
            new HistoryRepositoryPaths(Path.Combine(_root, "repository"))));
        await history.InitializeAsync();
        var restore = new HistoryRestoreService(
            history,
            new RepresentationRuntime([new AlwaysAvailableRepresentationHandler()]),
            _ => Task.FromResult<IRepresentationEnvironment>(new RepresentationEnvironment([], [], [])),
            new FileSystemHistoryRestoreMutationBackend());
        return new Fixture(history, restore, configId, SourceId.New());
    }

    private sealed class Fixture(
        HistoryRuntime history,
        HistoryRestoreService restore,
        HistoryConfigId configId,
        SourceId sourceId) : IAsyncDisposable
    {
        private readonly HistoryPackCodec _codec = new();

        public HistoryRuntime History { get; } = history;
        public HistoryQuickRestoreResolver Resolver { get; } = new(history, restore);
        public HistoryConfigId ConfigId { get; } = configId;
        public SourceId SourceId { get; } = sourceId;

        public Lineage CreateLineage(
            string name,
            DateTimeOffset createdAt,
            BranchId? branchId = null,
            BranchUpdateId? parentUpdateId = null,
            MaterializationFidelity fidelity = MaterializationFidelity.Exact)
        {
            var version = new SourceVersion(
                VersionId.New(), ConfigId, SourceId, [], createdAt, null,
                CaptureScope.FullSource, CaptureOutcome.Captured, [],
                new SourceDescriptorSnapshot(name, name), name, HistoryProvenance.Native("test"));
            var representation = new VersionRepresentation(
                RepresentationId.New(), version.VersionId, RepresentationKind.CoreFull, "quick-test", [],
                fidelity, null, null, null);
            var checkpoint = new ConfigurationCheckpoint(
                CheckpointId.New(), ConfigId, createdAt, null, HistoryProvenance.Native("test"),
                [new CheckpointSource(
                    SourceId,
                    version.SourceDescriptorSnapshot,
                    version.VersionId,
                    CheckpointSourceDisposition.Captured)]);
            var update = new BranchUpdate(
                BranchUpdateId.New(), branchId ?? BranchId.New(),
                parentUpdateId is { } parentId ? [parentId] : [], name,
                checkpoint.CheckpointId, false, createdAt, BranchUpdateReason.Created);
            return new Lineage(version, representation, checkpoint, update);
        }

        public async Task CommitAsync(params object[] facts)
        {
            await History.Repository.CommitAsync(new HistoryCommitPack(
                PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
                facts.Select(fact => _codec.CreateObject(fact))));
        }

        public Task SaveWorkspaceAsync(BranchUpdate activeUpdate, VersionId? exactVersionId = null)
            => History.WorkspaceStore.SaveAsync(
                new HistoryWorkspace(
                    ConfigId,
                    0,
                    activeUpdate.BranchId,
                    activeUpdate.UpdateId,
                    exactVersionId is { } versionId
                        ? [new WorkspaceSourceBaseline(SourceId, versionId, WorkspaceBaselineRelation.Exact)]
                        : []),
                HistoryWorkspaceStore.MissingRevision);

        public ValueTask DisposeAsync() => History.DisposeAsync();
    }

    private sealed record Lineage(
        SourceVersion Version,
        VersionRepresentation Representation,
        ConfigurationCheckpoint Checkpoint,
        BranchUpdate Update);

    private sealed class AlwaysAvailableRepresentationHandler : IRepresentationHandler
    {
        public bool CanHandle(VersionRepresentation representation)
            => representation.Format == "quick-test";

        public ValueTask<RepresentationAssessment> AssessAsync(
            RepresentationAssessmentContext context,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new RepresentationAssessment(
                context.Representation.RepresentationId,
                HistoryReadiness.Ready,
                context.Representation.Fidelity,
                [],
                []));

        public ValueTask MaterializeAsync(
            RepresentationMaterializationContext context,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}

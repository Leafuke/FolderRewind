using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using FolderRewind.History.Storage;
using System.Collections.Immutable;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryRetentionSafetyTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindRetentionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task CompactionFailureNeverDeletesOrUnregistersOldRepresentationClosure()
    {
        var configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        var repository = new FileHistoryRepository(
            configId,
            new HistoryRepositoryPaths(Path.Combine(_root, "repository")));
        await using var history = new HistoryRuntime(repository);
        await history.InitializeAsync();

        var sourceId = SourceId.New();
        var baseVersion = Version(configId, sourceId, [], "base");
        var currentVersion = Version(configId, sourceId, [baseVersion.VersionId], "current");
        var full = new VersionRepresentation(
            RepresentationId.New(), baseVersion.VersionId, RepresentationKind.CoreFull, "test", [],
            RestoreStrategy.Exact, null, null, null);
        var smart = new VersionRepresentation(
            RepresentationId.New(), currentVersion.VersionId, RepresentationKind.CoreSmartDelta, "test",
            [full.RepresentationId], RestoreStrategy.Exact, null, null, null);
        var checkpoint = new ConfigurationCheckpoint(
            CheckpointId.New(), configId, DateTimeOffset.UtcNow, null, HistoryProvenance.Native("test"),
            [new CheckpointSource(
                sourceId, currentVersion.SourceDescriptorSnapshot, currentVersion.VersionId,
                CheckpointSourceDisposition.Captured)]);
        var branch = new BranchUpdate(
            BranchUpdateId.New(), BranchId.New(), [], "main", checkpoint.CheckpointId, false,
            DateTimeOffset.UtcNow, BranchUpdateReason.Created);
        var codec = new HistoryPackCodec();
        await repository.CommitAsync(new HistoryCommitPack(
            PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            new object[] { baseVersion, currentVersion, full, smart, checkpoint, branch }
                .Select(item => codec.CreateObject(item))));

        var fullPath = CreatePayload("base.bin", "base-payload");
        var smartPath = CreatePayload("delta.bin", "delta-payload");
        var fullEntry = Entry(full.RepresentationId, fullPath);
        var smartEntry = Entry(smart.RepresentationId, smartPath);
        await history.LocalReplicaCatalogStore.SaveAsync(
            new LocalReplicaCatalog(configId, 0, [fullEntry, smartEntry]),
            LocalReplicaCatalogStore.MissingRevision);
        var workspace = new HistoryWorkspace(
            configId,
            0,
            branch.BranchId,
            branch.UpdateId,
            [new WorkspaceSourceBaseline(
                sourceId, currentVersion.VersionId, WorkspaceBaselineRelation.Exact)]);
        await history.WorkspaceStore.SaveAsync(workspace, HistoryWorkspaceStore.MissingRevision);

        var representationRuntime = new RepresentationRuntime([new ReadyTestHandler()]);
        var payloads = new TrackingPayloadStore();
        Task<IRepresentationEnvironment> EnvironmentFactory(CancellationToken _)
            => Task.FromResult<IRepresentationEnvironment>(new RepresentationEnvironment([], [], []));
        var planner = new HistoryRetentionPlanner(history, representationRuntime, EnvironmentFactory, payloads);
        var plan = await planner.PlanAsync(new HistoryRetentionRequest(
            0,
            MaterializationFidelity.Exact,
            HistoryRetentionOperationRoots.Empty));
        Assert.HasCount(1, plan.Compactions);
        Assert.IsTrue(plan.LocalPayloadDeletions.All(item => item.RequiresCompaction));
        var executor = new HistoryRetentionExecutor(
            history,
            planner,
            representationRuntime,
            EnvironmentFactory,
            new FailingCompactionBackend(),
            payloads,
            new NullHistoryArtifactGarbageCollector());

        var result = await executor.ExecuteAsync(plan);

        Assert.AreEqual(HistoryRetentionExecutionStatus.Failed, result.Status);
        Assert.AreEqual(0, payloads.DeleteCount);
        Assert.IsTrue(File.Exists(fullPath));
        Assert.IsTrue(File.Exists(smartPath));
        var catalog = (await history.LocalReplicaCatalogStore.LoadAsync()).Value!;
        CollectionAssert.AreEquivalent(
            new[] { fullEntry.LocalReplicaId, smartEntry.LocalReplicaId },
            catalog.Entries.Select(item => item.LocalReplicaId).ToArray());
    }

    private string CreatePayload(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static LocalReplicaCatalogEntry Entry(RepresentationId representationId, string path)
        => new(
            representationId,
            LocalReplicaId.New(),
            LocalReplicaLocator.ControlledAbsolute(path),
            DateTimeOffset.UtcNow);

    private static SourceVersion Version(
        HistoryConfigId configId,
        SourceId sourceId,
        IEnumerable<VersionId> parents,
        string name)
        => new(
            VersionId.New(), configId, sourceId, parents, DateTimeOffset.UtcNow, null,
            CaptureScope.FullSource, CaptureOutcome.Captured, [],
            new SourceDescriptorSnapshot(name, name), name, HistoryProvenance.Native("test"));

    private sealed class ReadyTestHandler : IRepresentationHandler
    {
        public bool CanHandle(VersionRepresentation representation) => representation.Format == "test";

        public ValueTask<RepresentationAssessment> AssessAsync(
            RepresentationAssessmentContext context,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new RepresentationAssessment(
                context.Representation.RepresentationId,
                HistoryReadiness.Ready,
                MaterializationFidelity.Exact,
                [],
                []));

        public ValueTask MaterializeAsync(
            RepresentationMaterializationContext context,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(context.StagingDirectory);
            File.WriteAllText(Path.Combine(context.StagingDirectory, "state.txt"), "materialized");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingPayloadStore : IHistoryLocalPayloadStore
    {
        public int DeleteCount { get; private set; }

        public ValueTask<HistoryLocalPayloadInspection> InspectAsync(
            LocalReplicaCatalogEntry entry,
            CancellationToken cancellationToken)
        {
            var path = entry.Locator.Resolve();
            return ValueTask.FromResult(new HistoryLocalPayloadInspection(
                true,
                File.Exists(path),
                path,
                File.Exists(path) ? new FileInfo(path).Length : 0,
                string.Empty));
        }

        public ValueTask DeleteAsync(string resolvedPath, CancellationToken cancellationToken)
        {
            DeleteCount++;
            File.Delete(resolvedPath);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingCompactionBackend : IHistoryCompactionBackend
    {
        public Task<HistoryCompactionPayload> CreateFullAsync(
            SourceVersion version,
            string materializedDirectory,
            RepresentationId replacementRepresentationId,
            string durableOutputDirectory,
            CancellationToken cancellationToken)
            => throw new IOException("Injected replacement creation failure.");

        public ValueTask<PayloadVerificationResult> DeepVerifyAsync(
            VersionRepresentation representation,
            string payloadPath,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new PayloadVerificationResult(true, string.Empty, string.Empty));
    }
}

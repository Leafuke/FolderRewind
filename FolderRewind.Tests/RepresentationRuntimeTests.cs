using FolderRewind.History.Domain;
using FolderRewind.History.Index;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;

namespace FolderRewind.Tests;

[TestClass]
public sealed class RepresentationRuntimeTests
{
    private string _root = null!;
    private VersionId _versionId;
    private FakeArchiveBackend _archive = null!;
    private FakePluginBackend _plugin = null!;
    private RepresentationRuntime _runtime = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindRepresentationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _versionId = VersionId.New();
        _archive = new FakeArchiveBackend();
        _plugin = new FakePluginBackend();
        _runtime = new RepresentationRuntime(
        [
            new CoreArchiveRepresentationHandler(_archive),
            new SmartDeltaRepresentationHandler(_archive),
            new PluginArtifactRepresentationHandler(_plugin)
        ]);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task DeepAssessment_SelectsAlternateExactRepresentationWhenFirstIsCorrupt()
    {
        var corrupt = CreateRepresentation(RepresentationKind.CoreFull, RestoreStrategy.Exact);
        var good = CreateRepresentation(RepresentationKind.CoreRolling, RestoreStrategy.Exact);
        var corruptPath = CreateFile("corrupt.7z", "bad");
        var goodPath = CreateFile("good.7z", "good");
        _archive.CorruptPaths.Add(corruptPath);
        var environment = Environment(
            Local(corrupt.RepresentationId, corruptPath),
            Local(good.RepresentationId, goodPath));

        var assessment = await _runtime.AssessVersionAsync(
            _versionId,
            [corrupt, good],
            environment,
            AssessmentDepth.Deep,
            MaterializationFidelity.Exact);

        Assert.AreEqual(good.RepresentationId, assessment.Selected!.RepresentationId);
        Assert.AreEqual(HistoryReadiness.Ready, assessment.Readiness);
        Assert.AreEqual(
            HistoryReadiness.Unavailable,
            assessment.Candidates.Single(item => item.RepresentationId == corrupt.RepresentationId).Readiness);
    }

    [TestMethod]
    public async Task CloudOnlyExactRepresentationRequiresExplicitPreparation()
    {
        var representation = CreateRepresentation(RepresentationKind.CoreFull, RestoreStrategy.Exact);
        var replica = new StorageReplica(
            ReplicaId.New(), representation.RepresentationId, ReplicaProviderKind.Cloud,
            $"replicas/{Guid.NewGuid():N}/payload", 10, null, HistoryProvenance.Native("test"));
        var environment = new RepresentationEnvironment(
            [], [replica], [replica.ReplicaId]);

        var assessment = await _runtime.AssessVersionAsync(
            _versionId,
            [representation],
            environment,
            AssessmentDepth.Fast,
            MaterializationFidelity.Exact);

        Assert.AreEqual(HistoryReadiness.PreparationRequired, assessment.Readiness);
        Assert.AreEqual(replica.ObjectKey, assessment.Selected!.Evidence.Single().Detail);
    }

    [TestMethod]
    public async Task SmartRepresentationWithMissingDependencyIsBlocked()
    {
        var delta = new VersionRepresentation(
            RepresentationId.New(), _versionId, RepresentationKind.CoreSmartDelta, "smart-v1",
            [RepresentationId.New()], RestoreStrategy.Exact, null, null, null);
        var environment = Environment(Local(delta.RepresentationId, CreateFile("delta.7z", "delta")));

        var assessment = await _runtime.AssessVersionAsync(
            _versionId,
            [delta],
            environment,
            AssessmentDepth.Fast,
            MaterializationFidelity.Exact);

        Assert.AreEqual(HistoryReadiness.Blocked, assessment.Readiness);
        Assert.IsTrue(assessment.Candidates.Single().Diagnostics.Any(text => text.Contains("not materializable", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task MissingPluginBlocksArtifactWithoutMutatingOrDownloading()
    {
        _plugin.PluginAvailable = false;
        var representation = new VersionRepresentation(
            RepresentationId.New(), _versionId, RepresentationKind.PluginArtifact, "plugin-v1", [],
            RestoreStrategy.Plugin, null, null,
            new Dictionary<string, string>
            {
                [PluginArtifactRepresentationHandler.ArtifactRootIdMetadataKey] = Guid.NewGuid().ToString("D"),
                [PluginArtifactRepresentationHandler.PluginIdMetadataKey] = "plugin.test"
            });

        var assessment = await _runtime.AssessVersionAsync(
            _versionId,
            [representation],
            Environment(),
            AssessmentDepth.Fast,
            MaterializationFidelity.Exact);

        Assert.AreEqual(HistoryReadiness.Blocked, assessment.Readiness);
        Assert.AreEqual(1, _plugin.ProbeCount);
        Assert.AreEqual(0, _plugin.MaterializeCount);
    }

    [TestMethod]
    public async Task ExactRequirementRejectsOverlayButOrdinaryRestoreCanSelectIt()
    {
        var overlay = CreateRepresentation(RepresentationKind.LegacyArchive, RestoreStrategy.Overlay);
        var environment = Environment(Local(overlay.RepresentationId, CreateFile("partial.7z", "partial")));

        var exact = await _runtime.AssessVersionAsync(
            _versionId, [overlay], environment, AssessmentDepth.Fast, MaterializationFidelity.Exact);
        var ordinary = await _runtime.AssessVersionAsync(
            _versionId, [overlay], environment, AssessmentDepth.Fast, MaterializationFidelity.Overlay);

        Assert.AreEqual(HistoryReadiness.Blocked, exact.Readiness);
        Assert.IsNull(exact.Selected);
        Assert.AreEqual(HistoryReadiness.Ready, ordinary.Readiness);
        Assert.AreEqual(MaterializationFidelity.Overlay, ordinary.Selected!.Fidelity);
    }

    [TestMethod]
    public async Task SmartMaterializationPassesDependencyFirstStableRepresentationChain()
    {
        var full = CreateRepresentation(RepresentationKind.CoreFull, RestoreStrategy.Exact);
        var delta = new VersionRepresentation(
            RepresentationId.New(), _versionId, RepresentationKind.CoreSmartDelta, "smart-v1",
            [full.RepresentationId], RestoreStrategy.Exact, null, null, null);
        var environment = Environment(
            Local(full.RepresentationId, CreateFile("base.7z", "base")),
            Local(delta.RepresentationId, CreateFile("delta.7z", "delta")));
        var staging = Path.Combine(_root, "staging");

        await _runtime.MaterializeAsync(
            delta.RepresentationId,
            [delta, full],
            environment,
            MaterializationFidelity.Exact,
            staging);

        CollectionAssert.AreEqual(
            new[] { full.RepresentationId, delta.RepresentationId },
            _archive.LastMaterialization.Select(item => item.Representation.RepresentationId).ToArray());
    }

    private VersionRepresentation CreateRepresentation(RepresentationKind kind, RestoreStrategy strategy)
        => new(RepresentationId.New(), _versionId, kind, "test", [], strategy, null, null, null);

    private string CreateFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static LocalReplicaCatalogEntry Local(RepresentationId representationId, string path)
        => new(
            representationId,
            LocalReplicaId.New(),
            LocalReplicaLocator.ControlledAbsolute(path),
            DateTimeOffset.UtcNow);

    private static RepresentationEnvironment Environment(params LocalReplicaCatalogEntry[] entries)
        => new(entries, [], []);

    private sealed class FakeArchiveBackend : IArchiveRepresentationBackend
    {
        public HashSet<string> CorruptPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<ArchiveMaterializationInput> LastMaterialization { get; private set; } = [];

        public ValueTask<PayloadVerificationResult> VerifyAsync(
            VersionRepresentation representation,
            string localPath,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(CorruptPaths.Contains(localPath)
                ? new PayloadVerificationResult(false, string.Empty, "corrupt")
                : new PayloadVerificationResult(true, "verified", string.Empty));

        public ValueTask MaterializeAsync(
            IReadOnlyList<ArchiveMaterializationInput> dependencyFirstInputs,
            string stagingDirectory,
            CancellationToken cancellationToken)
        {
            LastMaterialization = dependencyFirstInputs.ToArray();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakePluginBackend : IPluginArtifactBackend
    {
        public bool PluginAvailable { get; set; } = true;
        public int ProbeCount { get; private set; }
        public int MaterializeCount { get; private set; }

        public ValueTask<PluginArtifactProbeResult> ProbeAsync(
            Guid artifactRootId,
            string pluginId,
            CancellationToken cancellationToken)
        {
            ProbeCount++;
            return ValueTask.FromResult(new PluginArtifactProbeResult(
                PluginAvailable,
                ArtifactAvailable: true,
                MaterializationFidelity.Exact,
                PluginAvailable ? "available" : "plugin missing"));
        }

        public ValueTask<PayloadVerificationResult> VerifyAsync(
            Guid artifactRootId,
            string pluginId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new PayloadVerificationResult(true, "verified", string.Empty));

        public ValueTask MaterializeAsync(
            Guid artifactRootId,
            string pluginId,
            string stagingDirectory,
            CancellationToken cancellationToken)
        {
            MaterializeCount++;
            return ValueTask.CompletedTask;
        }
    }
}

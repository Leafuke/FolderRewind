using System.Collections.Immutable;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
[TestCategory("ExternalTool")]
public sealed class NativeRestorePreparationTests
{
    [TestMethod]
    public async Task SingleNativeCandidateVerifiesOnlyDuringMaterializationAndExplicitDeepStillVerifies()
    {
        await using var f = new HistoryChainRewriteFixture(); await f.InitializeAsync();
        var target = Path.Combine(f.Root, "live"); Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "file"), "old");
        var (version, rep) = await AddAsync(f, f.Source, target);
        var backend = new InlineBackend(); var engine = new RepresentationRuntime([new PluginArtifactRepresentationHandler(backend)]);
        var environment = new RepresentationEnvironment([], [], []);
        var restore = new HistoryRestoreService(f.History, engine, _ => Task.FromResult<IRepresentationEnvironment>(environment),
            new FileSystemHistoryRestoreMutationBackend()) { UseTargetVolumeWorkspaces = true };
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var result = await restore.RestoreVersionAsync(version.VersionId, new(f.Source, target),
                (await f.History.WorkspaceStore.LoadAsync()).Value!, HistoryRestoreApplyMode.Clean);
            Assert.IsTrue(result.Succeeded, result.Diagnostic);
            Assert.AreEqual(attempt + 1, backend.Materializations); Assert.AreEqual(0, backend.Verifications);
            Assert.AreEqual("verified", File.ReadAllText(Path.Combine(target, "file")));
        }
        await engine.AssessVersionAsync(version.VersionId, [rep], environment, AssessmentDepth.Deep, MaterializationFidelity.Exact);
        Assert.AreEqual(1, backend.Verifications, "Explicit Deep remains a full verification contract.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MultipleCandidatesAndUnsupportedContractKeepDeepSelection(bool unsupported)
    {
        await using var f = new HistoryChainRewriteFixture(); await f.InitializeAsync();
        var target = Path.Combine(f.Root, "live"); Directory.CreateDirectory(target);
        var (version, rep) = await AddAsync(f, f.Source, target, supported: !unsupported);
        if (!unsupported)
        {
            var alternate = new VersionRepresentation(RepresentationId.New(), rep.VersionId, rep.Kind, rep.Format,
                rep.DependencyRepresentationIds, rep.Fidelity, rep.LogicalSha256, rep.StateFingerprint, rep.RepresentationSpecificMetadata);
            var codec = new HistoryPackCodec();
            await f.History.Repository.CommitAsync(new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, [codec.CreateObject(alternate)]));
            await f.History.EnsureIndexCurrentAsync();
        }
        var backend = new InlineBackend(); var engine = new RepresentationRuntime([new PluginArtifactRepresentationHandler(backend)]);
        var restore = new HistoryRestoreService(f.History, engine, _ => Task.FromResult<IRepresentationEnvironment>(new RepresentationEnvironment([], [], [])),
            new FileSystemHistoryRestoreMutationBackend()) { UseTargetVolumeWorkspaces = true };
        var result = await restore.RestoreVersionAsync(version.VersionId, new(f.Source, target),
            (await f.History.WorkspaceStore.LoadAsync()).Value!, HistoryRestoreApplyMode.Clean);
        Assert.IsTrue(result.Succeeded, result.Diagnostic);
        Assert.IsGreaterThanOrEqualTo(2, backend.Verifications);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SecondSourceFailureOrCancellationDoesNotPublishFirstSource(bool cancel)
    {
        await using var f = new HistoryChainRewriteFixture(); await f.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        var other = SourceId.New(); var first = Path.Combine(f.Root, "first"); var second = Path.Combine(f.Root, "second");
        foreach (var path in new[] { first, second }) { Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "file"), "original"); }
        var (a, _) = await AddAsync(f, f.Source, first); var (b, _) = await AddAsync(f, other, second);
        var backend = new InlineBackend { FailAt = cancel ? 0 : 2, BeforeMaterialize = count => { if (cancel && count == 2) cancellation.Cancel(); } };
        var engine = new RepresentationRuntime([new PluginArtifactRepresentationHandler(backend)]);
        var restore = new HistoryRestoreService(f.History, engine, _ => Task.FromResult<IRepresentationEnvironment>(new RepresentationEnvironment([], [], [])),
            new FileSystemHistoryRestoreMutationBackend()) { UseTargetVolumeWorkspaces = true };
        var workspace = (await f.History.WorkspaceStore.LoadAsync()).Value!;
        var result = await restore.RestoreVersionsAsync(new Dictionary<SourceId, VersionId> { [f.Source] = a.VersionId, [other] = b.VersionId },
            [new(f.Source, first), new(other, second)], workspace, HistoryRestoreApplyMode.Clean, cancellation.Token);
        Assert.AreEqual(HistoryRestoreStatus.BlockedBeforeMutation, result.Status);
        Assert.AreEqual(0, backend.Verifications); Assert.AreEqual(2, backend.Materializations);
        foreach (var path in new[] { first, second }) Assert.AreEqual("original", File.ReadAllText(Path.Combine(path, "file")));
        Assert.AreEqual(workspace.StateRevision, (await f.History.WorkspaceStore.LoadAsync()).Value!.StateRevision);
        await f.History.DrainRestoreCleanupAsync();
        Assert.IsEmpty(Directory.GetDirectories(f.Root, ".folderrewind-restore-*"));
    }

    private static async Task<(SourceVersion, VersionRepresentation)> AddAsync(HistoryChainRewriteFixture f, SourceId source, string path, bool supported = true)
    {
        var version = new SourceVersion(VersionId.New(), f.Config, source, [], DateTimeOffset.UtcNow, null, CaptureScope.FullSource,
            CaptureOutcome.Captured, [], new("source", path), null, HistoryProvenance.Native("test"));
        var rep = new VersionRepresentation(RepresentationId.New(), version.VersionId, RepresentationKind.PluginArtifact, "native-test", [],
            MaterializationFidelity.Exact, null, null, new Dictionary<string, string> { ["artifactRootId"] = Guid.NewGuid().ToString(),
                ["pluginId"] = "test.native", ["nativeArtifactContractVersion"] = supported ? "2" : "1", ["incrementalEvidence"] = "2" }.ToImmutableDictionary());
        var codec = new HistoryPackCodec();
        await f.History.Repository.CommitAsync(new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, [codec.CreateObject(version), codec.CreateObject(rep)]));
        await f.History.EnsureIndexCurrentAsync(); return (version, rep);
    }
    private sealed class InlineBackend : IPluginArtifactBackend, IInlineVerifiedMaterializer
    {
        internal int Verifications, Materializations;
        internal int FailAt;
        internal Action<int>? BeforeMaterialize;
        public bool CanVerifyDuringMaterialization(VersionRepresentation representation) => NativePluginArtifactBackend.IsSupported(representation);
        public ValueTask<PluginArtifactProbeResult> ProbeAsync(Guid root, string pluginId, CancellationToken token)
            => ValueTask.FromResult(new PluginArtifactProbeResult(true, true, MaterializationFidelity.Exact, "available"));
        public ValueTask<PayloadVerificationResult> VerifyAsync(Guid root, string pluginId, CancellationToken token)
        { Verifications++; return ValueTask.FromResult(new PayloadVerificationResult(true, "full", "")); }
        public ValueTask MaterializeAsync(Guid root, string pluginId, string staging, CancellationToken token)
        {
            Materializations++; BeforeMaterialize?.Invoke(Materializations); token.ThrowIfCancellationRequested();
            if (Materializations == FailAt) throw new InvalidDataException("Final source-byte verification failed.");
            Directory.CreateDirectory(staging); File.WriteAllText(Path.Combine(staging, "file"), "verified");
            return ValueTask.CompletedTask;
        }
    }
}

using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Tests;

[TestClass]
public sealed class NativeArtifactCaptureTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingTransformerHonorsFailurePolicyWithoutPublishingHistory(bool required)
    {
        var root = Path.Combine(Path.GetTempPath(), "NativeCaptureTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "core"); File.WriteAllText(file, "verified primary");
            var source = SourceId.New();
            var capture = VerifiedArchiveCaptureFactory.Create(source, CaptureScope.FullSource, file,
                RepresentationKind.CoreFull, "test", new Dictionary<string, SourceCaptureFileState> { ["file"] = new(1, DateTime.UnixEpoch) },
                null, [], 0, MaterializationFidelity.Exact);
            var folder = new FolderSnapshot(source.Value, "live-must-not-be-read", "world", new Dictionary<StateOwnerId, ProviderStateSnapshot>());
            var config = new ConfigSnapshot("config", new("revision"), new(new("plugin.missing"), "world"), "test", [folder], new Dictionary<StateOwnerId, ProviderStateSnapshot>());
            var store = new NativeArtifactStore(Path.Combine(root, "store"), new UnusedCodec());
            var service = new NativeArtifactIncrementalCaptureService(new PluginRuntimeManager(), store, false);
            var policy = new ArtifactTransformPolicy(new(new("plugin.missing"), "full"), new Dictionary<string,System.Text.Json.JsonElement>(),
                required ? ArtifactTransformFailureBehavior.RequireTransform : ArtifactTransformFailureBehavior.KeepPrimaryWithWarnings);
            var states = capture.BaselineCandidate!.FileStates;
            var plan = FileChangeCapturePlan.Create(states, null, capture.EffectiveSourceBoundary, changesOnly: false);
            Task<SourceCaptureResult> Capture(CancellationToken token) => service.CaptureAsync(plan, config, folder, policy,
                root, capture.EffectiveSourceBoundary, capture.BaselineCandidate, token);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Capture(default));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => Capture(cancel.Token));
            Assert.IsEmpty((await store.Ledger.LoadAsync()).Artifacts);
            Assert.IsEmpty(store.Pending()); Assert.IsTrue(File.Exists(file));

        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class UnusedCodec : IArtifactEnvelopeCodec
    {
        public Task PackAsync(string source, string target, bool encrypted, CancellationToken token) => throw new AssertFailedException("Unexpected envelope capture.");
        public Task UnpackAsync(string source, string target, bool encrypted, CancellationToken token) => throw new AssertFailedException("Unexpected envelope restore.");
    }
}

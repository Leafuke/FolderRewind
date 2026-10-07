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
            var service = new NativeArtifactCaptureService(new PluginRuntimeManager(), store, new PrimaryBackend(), false);
            var policy = new ArtifactTransformPolicy(new(new("plugin.missing"), "full"), new Dictionary<string,System.Text.Json.JsonElement>(),
                required ? ArtifactTransformFailureBehavior.RequireTransform : ArtifactTransformFailureBehavior.KeepPrimaryWithWarnings);
            if (required)
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.TransformAsync(capture, config, folder, policy, default));
            else
            {
                var result = await service.TransformAsync(capture, config, folder, policy, default);
                Assert.IsEmpty(result.AdditionalRepresentations); Assert.IsNull(result.ReservedVersionId);
                Assert.IsTrue(result.Diagnostics.Any(d => d.Code == "plugin.artifact_primary_kept"));
                Assert.AreEqual(capture.PayloadCandidate, result.PayloadCandidate);
            }
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.TransformAsync(capture, config, folder, policy, cancel.Token));
            Assert.IsEmpty((await store.Ledger.LoadAsync()).Artifacts);
            Assert.IsEmpty(store.Pending()); Assert.IsTrue(File.Exists(file));
            var reused = SourceCaptureResult.NoChanges(source, CaptureScope.FullSource, 0, VersionId.New(), "unchanged");
            Assert.AreSame(reused, await service.TransformAsync(reused, config, folder, policy, default));
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class UnusedCodec : IArtifactEnvelopeCodec
    {
        public Task PackAsync(string source, string target, bool encrypted, CancellationToken token) => throw new AssertFailedException("Unexpected envelope capture.");
        public Task UnpackAsync(string source, string target, bool encrypted, CancellationToken token) => throw new AssertFailedException("Unexpected envelope restore.");
    }
    private sealed class PrimaryBackend : IArchiveRepresentationBackend
    {
        public ValueTask<PayloadVerificationResult> VerifyAsync(VersionRepresentation representation, string path, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new PayloadVerificationResult(true, "test-primary", "")); }
        public ValueTask MaterializeAsync(IReadOnlyList<ArchiveMaterializationInput> inputs, string target, CancellationToken token)
        { Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "file"), "x"); return ValueTask.CompletedTask; }
    }
}

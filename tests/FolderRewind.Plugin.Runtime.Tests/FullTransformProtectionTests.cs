using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Plugin.Runtime.Tests;

public sealed partial class NativeArtifactStoreTests
{
    [TestMethod]
    public async Task BothFullEntryPointsProtectInputAndReleaseItAfterFailure()
    {
        using var fixture = new Fixture();
        var input = Path.Combine(fixture.Root, "primary", "input");
        IArtifactReadService? retained = null;
        ArtifactContentHandle handle = default;
        var plugin = new GraphPlugin(false)
        {
            BeforeTransform = request =>
            {
                retained = request.ArtifactRead; handle = request.Primary.Content;
                Assert.ThrowsExactly<IOException>(() => File.WriteAllText(input, "same length"));
                Assert.ThrowsExactly<IOException>(() => File.Move(input, input + ".moved"));
            }
        };
        await fixture.CaptureAsync(suppliedPlugin: plugin);
        Assert.IsNotNull(retained);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => retained.OpenReadAsync(handle, "input", default).AsTask());
        File.WriteAllText(input, "released");
        var ledger = new FileArtifactLedgerStore(fixture.Root);
        var core = await ledger.RegisterDetachedCoreArtifactAsync("config", fixture.Folder.FolderId, "primary",
            ArtifactCompleteness.Complete, CoreCaptureMode.Full, "register");
        var config = new ConfigSnapshot("config", new("revision"), Kind, "world", [fixture.Folder], new Dictionary<StateOwnerId, ProviderStateSnapshot>());
        var coordinator = new ArtifactTransformCoordinator(fixture.Runtime!, ledger);
        plugin.BeforeTransform = request =>
        {
            Assert.ThrowsExactly<IOException>(() => File.WriteAllText(input, "mutated"));
            throw new IOException("Input digest mismatch cannot become semantic fallback.");
        };
        await Assert.ThrowsExactlyAsync<IOException>(() => coordinator.TransformAsync(Id, config, fixture.Folder, "version", core.ArtifactRootId,
            new(new(Id, "full"), new Dictionary<string, JsonElement>(), ArtifactTransformFailureBehavior.KeepPrimaryWithWarnings), []).AsTask());
        File.WriteAllText(input, "released again");
        Assert.AreEqual(0, fixture.Runtime!.GetSnapshot(Id).ActiveLeases);
        await fixture.Runtime.DeactivateAsync(Id);
    }
}

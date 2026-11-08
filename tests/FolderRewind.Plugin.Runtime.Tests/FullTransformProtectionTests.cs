using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Plugin.Runtime.Tests;

public sealed partial class NativeArtifactStoreTests
{
    [TestMethod]
    public async Task DirectFullCaptureProtectsInputAndReleasesReadSession()
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
        Assert.AreEqual(0, fixture.Runtime!.GetSnapshot(Id).ActiveLeases);
        await fixture.Runtime.DeactivateAsync(Id);
    }
}

using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SpatialPreviewViewportTests
{
    [TestMethod]
    public void NegativeViewportAndFarOverviewHaveBoundedTileWork()
    {
        var camera = new SpatialPreviewViewport { CenterX = -1, CenterY = -1 };
        var near = camera.Tiles(1024, 768).ToArray();
        Assert.IsTrue(near.Any(k => k.X < 0 && k.Y < 0));
        Assert.IsTrue(near.All(k => k.UnitsPerPixel == 1));
        Assert.AreEqual((-1d, -1d), camera.ToWorld(512, 384, 1024, 768));
        camera.SetScale(1d / 4096);
        var far = camera.Tiles(1024, 768).ToArray();
        Assert.IsLessThanOrEqualTo(25, far.Length);
        Assert.IsTrue(far.All(k => k.UnitsPerPixel == 4096));
        Assert.IsLessThanOrEqualTo(256, camera.Tiles(100000, 100000).Count());
    }

    [TestMethod]
    public async Task LateProviderResultsCannotCrossViewGeneration()
    {
        using var epoch = new PreviewRequestEpoch();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = epoch.Revision; var token = epoch.Token;
        var pending = ReceiveAsync();
        var next = epoch.Advance();
        completion.SetResult(42); // A provider deliberately ignores its cancellation token.
        Assert.IsNull(await pending);
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.IsTrue(epoch.IsCurrent(next));
        async Task<int?> ReceiveAsync()
        {
            var result = await completion.Task;
            return epoch.IsCurrent(old) ? result : null;
        }
    }
}

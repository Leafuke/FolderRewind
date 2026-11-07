using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Operations;

namespace FolderRewind.Plugin.Runtime.Tests;

public sealed partial class PluginVerticalSliceTests
{
    [TestMethod]
    public async Task PreviewDemandRevisionsAreAtomicAndClosedGenerationsRejectLateUpdates()
    {
        var plugin = new FakeVerticalPlugin([]);
        var fixture = await ActivateFakeAsync([], plugin);
        var (config, folder) = Snapshot(FakeKind, "C:\\Data");
        var source = new SpatialPreviewSource(config, folder, Guid.NewGuid());
        var dispatcher = new SpatialPreviewDispatcher(fixture.Manager);
        await dispatcher.DescribeAsync(source, default);
        var view = Guid.NewGuid();
        var tile = new SpatialPreviewTileRequest(source, "layer", 0, 0, 1, null) { ViewId = view, DemandRevision = 2 };
        await dispatcher.UpdateDemandAsync(new(source, view, 2, [tile]), default);
        await dispatcher.UpdateDemandAsync(new(source, view, 2, [tile]), default);
        await dispatcher.UpdateDemandAsync(new(source, view, 1, []), default);
        Assert.AreEqual(1, plugin.AppliedDemands);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => dispatcher.UpdateDemandAsync(new(source, view, 2, []), default).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => dispatcher.RenderAsync(tile with { DemandRevision = 3 }, default).AsTask());
        await dispatcher.UpdateDemandAsync(new(source, view, 3, []), default);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => dispatcher.RenderAsync(tile, default).AsTask());
        await dispatcher.CloseAsync(source);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => dispatcher.DescribeAsync(source, default).AsTask());
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => dispatcher.UpdateDemandAsync(new(source, view, 4, []), default).AsTask());
        Assert.AreEqual(0, fixture.Manager.GetSnapshot(FakePluginId).ActiveLeases);
    }

    [TestMethod]
    public async Task SharedTileRemainsAdmittedUntilLastViewReleasesAndViewQuotaIsEnforced()
    {
        var fixture = await ActivateFakeAsync([]);
        var (config, folder) = Snapshot(FakeKind, "C:\\Data");
        var source = new SpatialPreviewSource(config, folder, Guid.NewGuid()); var dispatcher = new SpatialPreviewDispatcher(fixture.Manager);
        await dispatcher.DescribeAsync(source, default);
        var views = Enumerable.Range(0, 9).Select(_ => Guid.NewGuid()).ToArray();
        var tile = new SpatialPreviewTileRequest(source, "layer", 0, 0, 1, null) { DemandRevision = 1 };
        for (var i = 0; i < 8; i++) await dispatcher.UpdateDemandAsync(new(source, views[i], 1, [tile with { ViewId = views[i] }]), default);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => dispatcher.UpdateDemandAsync(new(source, views[8], 1, []), default).AsTask());
        await dispatcher.UpdateDemandAsync(new(source, views[0], 2, []), default);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => dispatcher.RenderAsync(tile with { ViewId = views[1] }, canceled.Token).AsTask());
        // The fake blocks Render until cancellation; admission succeeded if the provider is reached.
        using var timeout = new CancellationTokenSource();
        var rendering = dispatcher.RenderAsync(tile with { ViewId = views[1] }, timeout.Token).AsTask();
        timeout.Cancel(); await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => rendering);
        await dispatcher.CloseAsync(source);
    }

    [TestMethod]
    public async Task PreviewDemandRejectsDuplicatesForeignSourcesAndGenerationQuota()
    {
        var fixture = await ActivateFakeAsync([]);
        var (config, folder) = Snapshot(FakeKind, "C:\\Data");
        var source = new SpatialPreviewSource(config, folder, Guid.NewGuid());
        var dispatcher = new SpatialPreviewDispatcher(fixture.Manager);
        await dispatcher.DescribeAsync(source, default);
        var view = Guid.NewGuid();
        var tile = new SpatialPreviewTileRequest(source, "layer", 0, 0, 1, null) { ViewId = view, DemandRevision = 1 };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => dispatcher.UpdateDemandAsync(new(source, view, 1, [tile, tile]), default).AsTask());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => dispatcher.UpdateDemandAsync(new(source, view, 1,
            [tile with { Source = source with { Generation = Guid.NewGuid() } }]), default).AsTask());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => dispatcher.UpdateDemandAsync(new(source, view, 1,
            [tile with { Source = source with { WorldId = "foreign" } }]), default).AsTask());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => dispatcher.UpdateDemandAsync(new(source, view, 1, [tile with { OriginX = double.NaN }]), default).AsTask());
        var all = Enumerable.Range(0, 256).Select(i => tile with { OriginX = i * 256 }).ToArray();
        await dispatcher.UpdateDemandAsync(new(source, view, 1, all), default);
        var another = Guid.NewGuid();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => dispatcher.UpdateDemandAsync(new(source, another, 1,
            [tile with { ViewId = another, OriginX = -256 }]), default).AsTask());
        await dispatcher.CloseAsync(source);
    }

    [TestMethod]
    public async Task DemandPublisherCoalescesAndAwaitsFinalRelease()
    {
        var (config, folder) = Snapshot(FakeKind, "C:\\Data");
        var source = new SpatialPreviewSource(config, folder, Guid.NewGuid());
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new List<SpatialPreviewDemand>();
        var publisher = new SpatialPreviewDemandPublisher(source, async demand =>
        {
            lock (applied) applied.Add(demand);
            if (demand.Revision == 1) { first.SetResult(); await release.Task; }
        }, _ => { });
        var tile = new SpatialPreviewTileRequest(source, "layer", 0, 0, 1, null);
        publisher.Set([tile]); await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 1; i <= 100; i++) publisher.Set([tile with { OriginX = i * 256 }]);
        var closing = publisher.CloseAsync();
        Assert.IsFalse(closing.IsCompleted);
        release.SetResult(); await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.HasCount(2, applied);
        Assert.IsEmpty(applied[^1].Tiles);
        Assert.IsFalse(publisher.IsReady);
    }
    [TestMethod]
    public void FrozenRasterTransientAllocationIsSeparateFromProviderRetainedCache()
    {
        var input = new SpatialPreviewTile(256, 256, new byte[262144], []);
        _ = SpatialPreviewDispatcher.FreezeTile(input);
        var start = GC.GetAllocatedBytesForCurrentThread();
        var frozen = SpatialPreviewDispatcher.FreezeTile(input);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.AreEqual(262144, frozen.BgraPremultiplied.Length);
        Assert.IsGreaterThanOrEqualTo(262144L, allocated);
        Assert.IsLessThan(300000L, allocated);
        Console.WriteLine($"Runtime freeze cumulative allocation={allocated}; raster bytes={frozen.BgraPremultiplied.Length}; this is additional to the provider ledger.");
    }
}

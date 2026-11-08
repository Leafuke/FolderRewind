using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;

namespace FolderRewind.Plugin.Runtime.Tests;

public sealed partial class PluginRuntimeManagerTests
{
    [TestMethod]
    public async Task PreviewWithoutDemandCapabilityIsRejectedBeforeActivationCommit()
    {
        var plugin = new FakePlugin(context =>
        {
            context.RegisterCapability<ISpatialPreviewCapability>(new LegacyPreview());
            return PluginActivationResult.Empty;
        });
        var store = new FakeStore(); var manager = new PluginRuntimeManager();
        Assert.IsFalse((await manager.ActivateAsync(Candidate(PluginOneId, plugin, store))).Success);
        Assert.IsEmpty(store.Commits);
        Assert.IsNull(manager.TryAcquire<ISpatialPreviewCapability>(PluginOneId));
        StringAssert.Contains(manager.GetSnapshot(PluginOneId).LastError, "Upgrade");
    }
    private sealed class LegacyPreview : ISpatialPreviewCapability
    {
        public ConfigKindRef Kind => new(new(PluginOneId.Value), "world");
        public ValueTask<SpatialPreviewDescription> DescribeAsync(SpatialPreviewSource source, PluginInvocationContext context) => throw new NotSupportedException();
        public ValueTask<SpatialPreviewTile> RenderAsync(SpatialPreviewTileRequest request, PluginInvocationContext context) => throw new NotSupportedException();
        public ValueTask<SpatialPreviewPoint> InspectAsync(SpatialPreviewPointRequest request, PluginInvocationContext context) => throw new NotSupportedException();
    }
}

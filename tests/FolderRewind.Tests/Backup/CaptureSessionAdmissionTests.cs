using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Services.Plugins.V3;

namespace FolderRewind.Tests;

[TestClass]
public sealed class CaptureSessionAdmissionTests
{
    [TestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public async Task PinnedSessionAdmitsOnlyCoordinationProvidersWhileDraining(bool supported, bool prefer)
    {
        var id = new PluginId("test.coordination"); var plugin = new Provider(id, supported); var runtime = new PluginRuntimeManager();
        var folder = new FolderSnapshot(Guid.NewGuid(), "unused", "world", new Dictionary<StateOwnerId, ProviderStateSnapshot>());
        var config = new ConfigSnapshot("config", new("revision"), plugin.Kind, "world", [folder], new Dictionary<StateOwnerId, ProviderStateSnapshot>());
        var activated = await runtime.ActivateAsync(new(id, () => plugin, new(id, new Dictionary<string, System.Text.Json.JsonElement>()), [config], new Host(), new Store()));
        Assert.IsTrue(activated.Success, System.Text.Json.JsonSerializer.Serialize(activated.Diagnostics));
        using var operation = runtime.TryAcquireOperation(id)!;
        var drain = runtime.DeactivateAsync(id);
        Assert.IsFalse(drain.IsCompleted);
        var owner = new PluginV3CaptureLeaseOwner();
        var diagnostics = new List<PluginDiagnostic>();
        async Task Acquire() => await owner.AcquireAsync(runtime, id, config, folder, prefer ? ConsistencyIntent.Prefer : ConsistencyIntent.Require, diagnostics,
            sourceViewLimits: new(1024, 10), operation: operation);
        if (supported)
        {
            await Acquire();
            Assert.IsNull(owner.StableSourcePath);
            Assert.AreEqual(folder.Path, owner.SourcePath);
            Assert.AreEqual(1, plugin.Calls);
            Assert.IsTrue(plugin.Request!.CoordinationOnly);
            Assert.IsNull(plugin.Request.SourceViewLimits);
            await owner.CompleteAsync();
            Assert.IsTrue(plugin.Released);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<NotSupportedException>(Acquire);
            Assert.AreEqual(0, plugin.Calls, "An older provider must be rejected before it can allocate a snapshot.");
        }
        Assert.IsFalse(drain.IsCompleted, "Consistency disposal must not release the operation's transformer/materializer ownership.");
        operation.Dispose(); await drain;
    }

    private sealed class Provider(PluginId id, bool supported) : IFolderRewindPlugin, IBackupConsistencyCapability
    {
        public ConfigKindRef Kind => new(new(id.Value), "world");
        public bool SupportsCoordinationOnly => supported;
        public int Calls; public bool Released; public BackupConsistencyRequest? Request;
        public ValueTask<PluginActivationResult> ActivateAsync(IPluginActivationContext context, CancellationToken token)
        { context.RegisterCapability<IPluginCapability>(this); return ValueTask.FromResult(PluginActivationResult.Empty); }
        public ValueTask DeactivateAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<IConsistencyLease> AcquireAsync(BackupConsistencyRequest request, PluginInvocationContext context)
        { Calls++; Request = request; return ValueTask.FromResult<IConsistencyLease>(new Lease(request.Folder.Path, () => Released = true)); }
    }
    private sealed class Lease(string path, Action release) : IConsistencyLease
    {
        public string SourcePath => path;
        public bool IsStableSourceView => false;
        public IReadOnlyList<PluginDiagnostic> Diagnostics => [];
        public ValueTask DisposeAsync() { release(); return ValueTask.CompletedTask; }
    }
    private sealed class Store : IPluginActivationStore
    { public ValueTask CommitAsync(PluginActivationCommit commit, CancellationToken token) => ValueTask.CompletedTask; }
    private sealed class Host : IPluginHostServices
    {
        public IReadOnlyConfigQueryService Configs => null!;
        public IBackupRequestService Backups => throw new NotSupportedException();
        public IRestoreRequestService Restores => throw new NotSupportedException();
        public IHistoryQueryService History => null!;
        public IPluginNotificationService Notifications => throw new NotSupportedException();
        public IKnotLinkHostService KnotLink => throw new NotSupportedException();
        public IPluginDataStore DataStore => throw new NotSupportedException();
        public IPluginTemporaryStorage TemporaryStorage => throw new NotSupportedException();
        public IPluginLogger Logger => null!;
    }
}

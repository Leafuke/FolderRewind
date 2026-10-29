using System.Text;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Settings;
using FolderRewind.Services.Plugins.V3;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class PluginRuntimeManagerTests
{
    private static readonly PluginId PluginOneId = new("com.folderrewind.test-one");
    private static readonly PluginId PluginTwoId = new("com.folderrewind.test-two");
    private static readonly DiscoveryProviderId SharedDiscoveryId = new("com.folderrewind.shared-discovery");

    [TestMethod]
    public async Task ConsistencyCleanupExceptionStillReleasesRuntimeLease()
    {
        var kind = new ConfigKindRef(new OwnerId(PluginOneId.Value), "consistency");
        var consistency = new FakeConsistencyLease("C:\\Data", throwOnDispose: true);
        var plugin = new FakePlugin(context =>
        {
            context.RegisterCapability<IBackupConsistencyCapability>(
                new FakeConsistencyCapability(kind, consistency));
            return PluginActivationResult.Empty;
        });
        var manager = new PluginRuntimeManager();
        await manager.ActivateAsync(Candidate(PluginOneId, plugin, new FakeStore()));
        var owner = new PluginV3CaptureLeaseOwner();
        var diagnostics = new List<PluginDiagnostic>();
        var (config, folder) = ConsistencySnapshots(kind);

        await owner.AcquireAsync(
            manager,
            PluginOneId,
            config,
            folder,
            ConsistencyIntent.Prefer,
            diagnostics);
        Assert.AreEqual(1, manager.GetSnapshot(PluginOneId).ActiveLeases);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => owner.CompleteAsync().AsTask());

        Assert.AreEqual(0, manager.GetSnapshot(PluginOneId).ActiveLeases);
        var deactivation = await manager.DeactivateAsync(PluginOneId);
        Assert.IsTrue(deactivation.Success);
        Assert.AreEqual(PluginRuntimeState.Inactive, deactivation.State);
    }

    [TestMethod]
    public async Task RequiredConsistencyAcquireExceptionStillReleasesRuntimeLease()
    {
        var kind = new ConfigKindRef(new OwnerId(PluginOneId.Value), "consistency");
        var plugin = new FakePlugin(context =>
        {
            context.RegisterCapability<IBackupConsistencyCapability>(
                new FakeConsistencyCapability(
                    kind,
                    new FakeConsistencyLease("C:\\Data"),
                    throwOnAcquire: true));
            return PluginActivationResult.Empty;
        });
        var manager = new PluginRuntimeManager();
        await manager.ActivateAsync(Candidate(PluginOneId, plugin, new FakeStore()));
        var owner = new PluginV3CaptureLeaseOwner();
        var diagnostics = new List<PluginDiagnostic>();
        var (config, folder) = ConsistencySnapshots(kind);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => owner.AcquireAsync(
                manager,
                PluginOneId,
                config,
                folder,
                ConsistencyIntent.Require,
                diagnostics).AsTask());

        StringAssert.Contains(error.Message, "consistency acquire failed");
        Assert.AreEqual(0, manager.GetSnapshot(PluginOneId).ActiveLeases);
        var deactivation = await manager.DeactivateAsync(PluginOneId);
        Assert.IsTrue(deactivation.Success);
        Assert.AreEqual(PluginRuntimeState.Inactive, deactivation.State);
    }

    private static PluginActivationCandidate Candidate(
        PluginId id,
        FakePlugin plugin,
        FakeStore store,
        IReadOnlyList<ConfigSnapshot>? configs = null,
        string settingsValue = "value")
        => Candidate(id, () => plugin, store, configs, settingsValue);

    private static PluginActivationCandidate Candidate(
        PluginId id,
        Func<IFolderRewindPlugin> factory,
        FakeStore store,
        IReadOnlyList<ConfigSnapshot>? configs = null,
        string settingsValue = "value")
        => new(
            id,
            factory,
            new PluginSettingsSnapshot(id, new Dictionary<string, JsonElement> { ["setting"] = Json($"\"{settingsValue}\"") }),
            configs ?? Array.Empty<ConfigSnapshot>(),
            new FakeHostServices(),
            store);

    private static FakePlugin PluginWithDiscovery(
        string marker,
        Func<CancellationToken, ValueTask>? deactivate = null)
        => new(context =>
        {
            context.RegisterCapability<IDiscoveryCapability>(new FakeDiscovery(marker));
            return PluginActivationResult.Empty;
        }, deactivate);

    private static ConfigSnapshot ConfigWithState(ProviderStateSnapshot state)
        => new(
            state.Location.ConfigId,
            new ConfigRevision("revision-1"),
            new ConfigKindRef(new OwnerId("folderrewind.core"), "default"),
            "Config",
            Array.Empty<FolderSnapshot>(),
            new Dictionary<StateOwnerId, ProviderStateSnapshot> { [state.StateOwnerId] = state });

    private static (ConfigSnapshot Config, FolderSnapshot Folder) ConsistencySnapshots(ConfigKindRef kind)
    {
        var folder = new FolderSnapshot(
            Guid.NewGuid(),
            "C:\\Data",
            "Data",
            new Dictionary<StateOwnerId, ProviderStateSnapshot>());
        return (
            new ConfigSnapshot(
                "config",
                new ConfigRevision("revision-1"),
                kind,
                "Config",
                [folder],
                new Dictionary<StateOwnerId, ProviderStateSnapshot>()),
            folder);
    }

    private static JsonElement Json(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task WaitForStateAsync(
        PluginRuntimeManager manager,
        PluginId id,
        PluginRuntimeState state)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (manager.GetSnapshot(id).State == state) return;
            await Task.Delay(5);
        }
        Assert.Fail($"Plugin did not enter state {state}.");
    }

    private sealed class FakePlugin : IFolderRewindPlugin
    {
        private readonly Func<IPluginActivationContext, PluginActivationResult> _activate;
        private readonly Func<CancellationToken, ValueTask>? _deactivate;

        public FakePlugin(
            Func<IPluginActivationContext, PluginActivationResult> activate,
            Func<CancellationToken, ValueTask>? deactivate = null)
        {
            _activate = activate;
            _deactivate = deactivate;
        }

        public int DeactivationCount { get; private set; }

        public ValueTask<PluginActivationResult> ActivateAsync(IPluginActivationContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(_activate(context));

        public ValueTask DeactivateAsync(CancellationToken cancellationToken)
        {
            DeactivationCount++;
            return _deactivate?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
        }
    }

    private sealed class FakeDiscovery(string marker, DiscoveryProviderId? providerId = null) : IDiscoveryCapability
    {
        public string Marker { get; } = marker;
        public DiscoveryProviderId ProviderId { get; } = providerId ?? SharedDiscoveryId;

        public ValueTask<DiscoveryResult> DiscoverAsync(DiscoveryRequest request, PluginInvocationContext context)
            => ValueTask.FromResult(new DiscoveryResult(Array.Empty<DiscoveryCandidate>(), Array.Empty<PluginDiagnostic>()));
    }

    private sealed class FakeConfigReconciliation(ConfigKindRef kind) : IConfigReconciliationCapability
    {
        public ConfigKindRef Kind { get; } = kind;

        public ValueTask<ConfigChangeProposal?> ProposeAsync(
            ConfigReconciliationRequest request,
            PluginInvocationContext context)
            => ValueTask.FromResult<ConfigChangeProposal?>(null);
    }

    private sealed class FakeConsistencyCapability(
        ConfigKindRef kind,
        FakeConsistencyLease lease,
        bool throwOnAcquire = false) : IBackupConsistencyCapability
    {
        public ConfigKindRef Kind { get; } = kind;
        public bool SupportsCoordinationOnly => true;

        public ValueTask<IConsistencyLease> AcquireAsync(
            BackupConsistencyRequest request,
            PluginInvocationContext context)
        {
            Assert.IsTrue(request.CoordinationOnly);
            return throwOnAcquire
                ? ValueTask.FromException<IConsistencyLease>(new InvalidOperationException("consistency acquire failed"))
                : ValueTask.FromResult<IConsistencyLease>(lease);
        }
    }

    private sealed class FakeConsistencyLease(string sourcePath, bool throwOnDispose = false) : IConsistencyLease
    {
        public string SourcePath { get; } = sourcePath;
        public bool IsStableSourceView => false;
        public IReadOnlyList<PluginDiagnostic> Diagnostics { get; } = Array.Empty<PluginDiagnostic>();

        public ValueTask DisposeAsync()
            => throwOnDispose
                ? ValueTask.FromException(new InvalidOperationException("consistency cleanup failed"))
                : ValueTask.CompletedTask;
    }

    private sealed class FakeMigration(StateOwnerId owner, ProviderStateLocation location) : IProviderStateMigrationCapability
    {
        public StateOwnerId StateOwnerId => owner;
        public int CurrentSchemaVersion => 1;
        public bool DataStoreWasBlocked { get; private set; }
        public bool ReadOnlyServicesWereAvailable { get; private set; }
        public bool MutationServicesWereBlocked { get; private set; }

        public async ValueTask<ProviderStatePatch> MigrateAsync(ProviderStateSnapshot state, PluginInvocationContext context)
        {
            await context.HostServices.Configs.FindAsync("config", context.OperationCancellation);
            await context.HostServices.History.QueryAsync("config", null, context.OperationCancellation);
            context.HostServices.Logger.Log(DiagnosticSeverity.Information, "staging");
            ReadOnlyServicesWereAvailable = true;

            var blocked = 0;
            try
            {
                await context.HostServices.DataStore.OpenReadAsync("state.json", context.OperationCancellation);
            }
            catch (InvalidOperationException)
            {
                DataStoreWasBlocked = true;
                blocked++;
            }

            try
            {
                await context.HostServices.Backups.RequestAsync("config", null, context.OperationCancellation);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("host_service.unavailable_during_activation", StringComparison.Ordinal))
            {
                blocked++;
            }
            try
            {
                await context.HostServices.Restores.RequestAsync("config", Guid.NewGuid(), "history", context.OperationCancellation);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("host_service.unavailable_during_activation", StringComparison.Ordinal))
            {
                blocked++;
            }
            try
            {
                await context.HostServices.Notifications.ShowAsync("title", "message", DiagnosticSeverity.Information, context.OperationCancellation);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("host_service.unavailable_during_activation", StringComparison.Ordinal))
            {
                blocked++;
            }
            try
            {
                await context.HostServices.KnotLink.SendAsync("event", new Dictionary<string, string>(), context.OperationCancellation);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("host_service.unavailable_during_activation", StringComparison.Ordinal))
            {
                blocked++;
            }
            try
            {
                await context.HostServices.TemporaryStorage.CreateDirectoryAsync(context.OperationCancellation);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("host_service.unavailable_during_activation", StringComparison.Ordinal))
            {
                blocked++;
            }
            MutationServicesWereBlocked = blocked == 6;

            return new ProviderStatePatch(location, owner, state.SchemaVersion, 1, Json("{\"migrated\":true}"));
        }
    }

    private sealed class FakeStore : IPluginActivationStore
    {
        public bool FailNextCommit { get; set; }
        public List<PluginActivationCommit> Commits { get; } = new();

        public ValueTask CommitAsync(PluginActivationCommit commit, CancellationToken cancellationToken)
        {
            if (FailNextCommit)
            {
                FailNextCommit = false;
                throw new IOException("Simulated atomic commit failure.");
            }
            Commits.Add(commit);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeHostServices : IPluginHostServices
    {
        public IReadOnlyConfigQueryService Configs { get; } = new FakeConfigQuery();
        public IBackupRequestService Backups { get; } = new FakeBackupRequests();
        public IRestoreRequestService Restores { get; } = new FakeRestoreRequests();
        public IHistoryQueryService History { get; } = new FakeHistory();
        public IPluginNotificationService Notifications { get; } = new FakeNotifications();
        public IKnotLinkHostService KnotLink { get; } = new FakeKnotLink();
        public IPluginDataStore DataStore { get; } = new FakeDataStore();
        public IPluginTemporaryStorage TemporaryStorage { get; } = new FakeTemporaryStorage();
        public IPluginLogger Logger { get; } = new FakeLogger();
    }

    private sealed class FakeConfigQuery : IReadOnlyConfigQueryService
    {
        public ValueTask<ConfigSnapshot?> FindAsync(string configId, CancellationToken cancellationToken) => ValueTask.FromResult<ConfigSnapshot?>(null);
    }

    private sealed class FakeBackupRequests : IBackupRequestService
    {
        public ValueTask<OperationOutcome> RequestAsync(string configId, Guid? folderId, CancellationToken cancellationToken) => ValueTask.FromResult(OperationOutcome.Success);
    }

    private sealed class FakeRestoreRequests : IRestoreRequestService
    {
        public ValueTask<OperationOutcome> RequestQuickAsync(string configId, Guid folderId, CancellationToken cancellationToken) => ValueTask.FromResult(OperationOutcome.Success);
        public ValueTask<OperationOutcome> RequestAsync(string configId, Guid folderId, string versionId, CancellationToken cancellationToken) => ValueTask.FromResult(OperationOutcome.Success);
    }

    private sealed class FakeHistory : IHistoryQueryService
    {
        public ValueTask<IReadOnlyList<HistoryVersionSnapshot>> QueryAsync(string configId, Guid? folderId, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<HistoryVersionSnapshot>>(Array.Empty<HistoryVersionSnapshot>());
    }

    private sealed class FakeNotifications : IPluginNotificationService
    {
        public ValueTask ShowAsync(string title, string message, DiagnosticSeverity severity, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class FakeKnotLink : IKnotLinkHostService
    {
        public bool IsAvailable => false;
        public ValueTask SendAsync(string eventName, IReadOnlyDictionary<string, string> arguments, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class FakeDataStore : IPluginDataStore
    {
        public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
            => ValueTask.FromResult<Stream>(new MemoryStream());
        public ValueTask<Stream> OpenWriteAsync(string relativePath, CancellationToken cancellationToken)
            => ValueTask.FromResult<Stream>(new MemoryStream());
    }

    private sealed class FakeTemporaryStorage : IPluginTemporaryStorage
    {
        public ValueTask<string> CreateDirectoryAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Path.GetTempPath());
    }

    private sealed class FakeLogger : IPluginLogger
    {
        public void Log(DiagnosticSeverity severity, string message, Exception? exception = null) { }
    }
}

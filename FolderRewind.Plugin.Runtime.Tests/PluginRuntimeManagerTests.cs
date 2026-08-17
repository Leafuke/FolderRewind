using System.Text;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Settings;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class PluginRuntimeManagerTests
{
    private static readonly PluginId PluginOneId = new("com.folderrewind.test-one");
    private static readonly PluginId PluginTwoId = new("com.folderrewind.test-two");
    private static readonly DiscoveryProviderId SharedDiscoveryId = new("com.folderrewind.shared-discovery");

    [TestMethod]
    public async Task ActivationCommitsBeforeCapabilitiesBecomeAvailable()
    {
        var store = new FakeStore();
        var plugin = PluginWithDiscovery("one");
        var manager = new PluginRuntimeManager();

        var result = await manager.ActivateAsync(Candidate(PluginOneId, plugin, store));

        Assert.IsTrue(result.Success);
        Assert.HasCount(1, store.Commits);
        Assert.AreEqual(PluginRuntimeState.Active, manager.GetSnapshot(PluginOneId).State);
        using var lease = manager.TryAcquire<IDiscoveryCapability>(PluginOneId);
        Assert.IsNotNull(lease);
        Assert.AreEqual("one", ((FakeDiscovery)lease.Capability).Marker);
    }

    [TestMethod]
    public async Task CommitFailureRollsBackActivationAndLeavesNoCapabilityResidue()
    {
        var failingStore = new FakeStore { FailNextCommit = true };
        var failedPlugin = PluginWithDiscovery("failed");
        var manager = new PluginRuntimeManager();

        var failed = await manager.ActivateAsync(Candidate(PluginOneId, failedPlugin, failingStore));

        Assert.IsFalse(failed.Success);
        Assert.AreEqual(PluginRuntimeState.Failed, manager.GetSnapshot(PluginOneId).State);
        Assert.AreEqual(1, failedPlugin.DeactivationCount);
        Assert.IsNull(manager.TryAcquire<IDiscoveryCapability>(PluginOneId));

        var replacement = PluginWithDiscovery("replacement");
        var succeeded = await manager.ActivateAsync(Candidate(PluginTwoId, replacement, new FakeStore()));
        Assert.IsTrue(succeeded.Success, "The failed activation must not reserve its capability key.");
    }

    [TestMethod]
    public async Task CapabilityConflictRollsBackSecondPlugin()
    {
        var manager = new PluginRuntimeManager();
        var first = PluginWithDiscovery("first");
        var second = PluginWithDiscovery("second");
        await manager.ActivateAsync(Candidate(PluginOneId, first, new FakeStore()));
        var secondStore = new FakeStore();

        var result = await manager.ActivateAsync(Candidate(PluginTwoId, second, secondStore));

        Assert.IsFalse(result.Success);
        Assert.IsEmpty(secondStore.Commits);
        Assert.AreEqual(1, second.DeactivationCount);
        using var lease = manager.TryAcquire<IDiscoveryCapability>(PluginOneId);
        Assert.IsNotNull(lease);
        Assert.AreEqual("first", ((FakeDiscovery)lease.Capability).Marker);
    }

    [TestMethod]
    public async Task SafeModeNeverInvokesPluginFactory()
    {
        var factoryCalls = 0;
        var manager = new PluginRuntimeManager(safeMode: true);
        var candidate = Candidate(
            PluginOneId,
            () =>
            {
                factoryCalls++;
                return PluginWithDiscovery("never");
            },
            new FakeStore());

        var result = await manager.ActivateAsync(candidate);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(OperationOutcome.Blocked, result.Outcome);
        Assert.AreEqual(0, factoryCalls);
        Assert.AreEqual(PluginRuntimeState.Inactive, manager.GetSnapshot(PluginOneId).State);
        Assert.IsTrue(SafeModePolicy.IsRequested(["FolderRewind.exe", "--SAFE-MODE"]));
    }

    [TestMethod]
    public async Task DrainingRejectsNewLeasesAndWaitsForExistingLease()
    {
        var manager = new PluginRuntimeManager();
        await manager.ActivateAsync(Candidate(PluginOneId, PluginWithDiscovery("one"), new FakeStore()));
        var lease = manager.TryAcquire<IDiscoveryCapability>(PluginOneId);
        Assert.IsNotNull(lease);

        var deactivation = manager.DeactivateAsync(PluginOneId).AsTask();
        await WaitForStateAsync(manager, PluginOneId, PluginRuntimeState.Draining);
        Assert.IsNull(manager.TryAcquire<IDiscoveryCapability>(PluginOneId));
        Assert.IsFalse(deactivation.IsCompleted);

        lease.Dispose();
        var result = await deactivation;
        Assert.IsTrue(result.Success);
        Assert.AreEqual(PluginRuntimeState.Inactive, manager.GetSnapshot(PluginOneId).State);
    }

    [TestMethod]
    public async Task CancelingDrainRestoresExistingSession()
    {
        var manager = new PluginRuntimeManager();
        await manager.ActivateAsync(Candidate(PluginOneId, PluginWithDiscovery("one"), new FakeStore()));
        using var activeLease = manager.TryAcquire<IDiscoveryCapability>(PluginOneId);
        Assert.IsNotNull(activeLease);
        using var cancellation = new CancellationTokenSource();

        var deactivation = manager.DeactivateAsync(PluginOneId, cancellation.Token).AsTask();
        await WaitForStateAsync(manager, PluginOneId, PluginRuntimeState.Draining);
        cancellation.Cancel();
        var result = await deactivation;

        Assert.IsFalse(result.Success);
        Assert.AreEqual(OperationOutcome.Canceled, result.Outcome);
        Assert.AreEqual(PluginRuntimeState.Active, manager.GetSnapshot(PluginOneId).State);
        using var newLease = manager.TryAcquire<IDiscoveryCapability>(PluginOneId);
        Assert.IsNotNull(newLease);
    }

    [TestMethod]
    public async Task DeactivationTimeoutLogicallyIsolatesPluginAndReleasesTransitionGate()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = PluginWithDiscovery(
            "hanging",
            _ => new ValueTask(never.Task));
        var manager = new PluginRuntimeManager(deactivationGracePeriod: TimeSpan.FromMilliseconds(25));
        await manager.ActivateAsync(Candidate(PluginOneId, plugin, new FakeStore()));

        var result = await manager.DeactivateAsync(PluginOneId);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(OperationOutcome.SuccessWithWarnings, result.Outcome);
        Assert.AreEqual(PluginRuntimeState.Failed, result.State);
        Assert.IsTrue(result.RequiresRestart);
        Assert.IsTrue(manager.GetSnapshot(PluginOneId).RequiresRestart);
        Assert.IsNull(manager.TryAcquire<IDiscoveryCapability>(PluginOneId));
        Assert.AreEqual(1, manager.RestartRetainedCount);

        var repeated = await manager.DeactivateAsync(PluginOneId);
        Assert.IsTrue(repeated.RequiresRestart);
        Assert.IsTrue(manager.GetSnapshot(PluginOneId).RequiresRestart);
        var samePlugin = await manager.ActivateAsync(
            Candidate(PluginOneId, PluginWithDiscovery("unsafe-reload"), new FakeStore()));
        Assert.IsFalse(samePlugin.Success);
        Assert.AreEqual(OperationOutcome.Blocked, samePlugin.Outcome);
        Assert.IsTrue(samePlugin.RequiresRestart);

        var other = await manager.ActivateAsync(
            Candidate(PluginTwoId, PluginWithDiscovery("other"), new FakeStore()));
        Assert.IsTrue(other.Success, "A timed-out plugin must not retain the transition gate.");
    }

    [TestMethod]
    public async Task ReplacementKeepsNewSessionActiveWhenOldDeactivationTimesOut()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldPlugin = PluginWithDiscovery(
            "old",
            _ => new ValueTask(never.Task));
        var manager = new PluginRuntimeManager(deactivationGracePeriod: TimeSpan.FromMilliseconds(25));
        await manager.ActivateAsync(Candidate(PluginOneId, oldPlugin, new FakeStore()));

        var result = await manager.ReplaceAsync(
            Candidate(PluginOneId, PluginWithDiscovery("new"), new FakeStore()));

        Assert.IsTrue(result.Success);
        Assert.AreEqual(OperationOutcome.SuccessWithWarnings, result.Outcome);
        Assert.AreEqual(PluginRuntimeState.Active, result.State);
        Assert.IsTrue(result.RequiresRestart);
        Assert.IsTrue(manager.GetSnapshot(PluginOneId).RequiresRestart);
        using (var lease = manager.TryAcquire<IDiscoveryCapability>(PluginOneId))
        {
            Assert.IsNotNull(lease);
            Assert.AreEqual("new", ((FakeDiscovery)lease.Capability).Marker);
        }

        var deactivate = await manager.DeactivateAsync(PluginOneId);
        Assert.IsTrue(deactivate.Success);
        Assert.AreEqual(OperationOutcome.SuccessWithWarnings, deactivate.Outcome);
        Assert.IsTrue(deactivate.RequiresRestart);
        Assert.IsTrue(manager.GetSnapshot(PluginOneId).RequiresRestart);
    }

    [TestMethod]
    public async Task DeactivationExceptionDoesNotUndoLogicalIsolationOrRequireRestart()
    {
        var plugin = PluginWithDiscovery(
            "throwing",
            _ => ValueTask.FromException(new InvalidOperationException("cleanup failed")));
        var manager = new PluginRuntimeManager(deactivationGracePeriod: TimeSpan.FromMilliseconds(25));
        await manager.ActivateAsync(Candidate(PluginOneId, plugin, new FakeStore()));

        var result = await manager.DeactivateAsync(PluginOneId);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(OperationOutcome.SuccessWithWarnings, result.Outcome);
        Assert.AreEqual(PluginRuntimeState.Failed, result.State);
        Assert.IsFalse(result.RequiresRestart);
        Assert.IsNull(manager.TryAcquire<IDiscoveryCapability>(PluginOneId));
    }

    [TestMethod]
    public async Task ActivationRollbackTimeoutIsRetainedAndReported()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = PluginWithDiscovery(
            "candidate",
            _ => new ValueTask(never.Task));
        var manager = new PluginRuntimeManager(deactivationGracePeriod: TimeSpan.FromMilliseconds(25));
        var store = new FakeStore { FailNextCommit = true };

        var result = await manager.ActivateAsync(Candidate(PluginOneId, plugin, store));

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.RequiresRestart);
        Assert.IsTrue(manager.GetSnapshot(PluginOneId).RequiresRestart);
        Assert.AreEqual(1, manager.RestartRetainedCount);
        var other = await manager.ActivateAsync(
            Candidate(PluginTwoId, PluginWithDiscovery("other"), new FakeStore()));
        Assert.IsTrue(other.Success);
    }

    [TestMethod]
    public async Task ProviderMigrationIsStagedWithDataStoreDisabledThenCommittedOnce()
    {
        var owner = new StateOwnerId("com.folderrewind.state");
        var location = new ProviderStateLocation("config", null);
        var state = new ProviderStateSnapshot(location, owner, 0, Json("{\"legacy\":true}"));
        var migration = new FakeMigration(owner, location);
        var plugin = new FakePlugin(context =>
        {
            context.RegisterCapability<IProviderStateMigrationCapability>(migration);
            return PluginActivationResult.Empty;
        });
        var config = ConfigWithState(state);
        var store = new FakeStore();
        var manager = new PluginRuntimeManager();

        var result = await manager.ActivateAsync(Candidate(PluginOneId, plugin, store, [config]));

        Assert.IsTrue(result.Success);
        Assert.IsTrue(migration.DataStoreWasBlocked);
        Assert.HasCount(1, store.Commits);
        var patch = store.Commits[0].ProviderStatePatches.Single();
        Assert.AreEqual(location, patch.Location);
        Assert.AreEqual(0, patch.ExpectedSchemaVersion);
        Assert.AreEqual(1, patch.SchemaVersion);
    }

    [TestMethod]
    public async Task FailedReplacementKeepsOldSettingsStateAndRuntimeInstance()
    {
        var manager = new PluginRuntimeManager();
        var oldPlugin = PluginWithDiscovery("old");
        await manager.ActivateAsync(Candidate(PluginOneId, oldPlugin, new FakeStore(), settingsValue: "old"));
        var sawDrainingDuringActivation = false;
        var newPlugin = new FakePlugin(context =>
        {
            sawDrainingDuringActivation = manager.GetSnapshot(PluginOneId).State == PluginRuntimeState.Draining;
            context.RegisterCapability<IDiscoveryCapability>(new FakeDiscovery("new"));
            return PluginActivationResult.Empty;
        });
        var failingStore = new FakeStore { FailNextCommit = true };

        var result = await manager.ReplaceAsync(
            Candidate(PluginOneId, newPlugin, failingStore, settingsValue: "new"));

        Assert.IsFalse(result.Success);
        Assert.AreEqual(PluginRuntimeState.Active, manager.GetSnapshot(PluginOneId).State);
        Assert.AreEqual(0, oldPlugin.DeactivationCount);
        Assert.AreEqual(1, newPlugin.DeactivationCount);
        Assert.IsTrue(sawDrainingDuringActivation);
        using var lease = manager.TryAcquire<IDiscoveryCapability>(PluginOneId);
        Assert.IsNotNull(lease);
        Assert.AreEqual("old", ((FakeDiscovery)lease.Capability).Marker);
    }

    [TestMethod]
    public async Task InvalidSettingsCandidateNeverCreatesOrCommitsPluginInstance()
    {
        var schema = PluginSettingsSchema.Parse(Encoding.UTF8.GetBytes("""
            {
              "schemaVersion": 1,
              "settings": [{ "key": "setting", "type": "boolean", "required": true }]
            }
            """));
        var factoryCalls = 0;
        var store = new FakeStore();
        var manager = new PluginRuntimeManager();
        var coordinator = new PluginSettingsTransactionCoordinator(manager);
        var candidate = Candidate(
            PluginOneId,
            () =>
            {
                factoryCalls++;
                return PluginWithDiscovery("invalid");
            },
            store);

        var result = await coordinator.ApplyAsync(schema, candidate);

        Assert.IsFalse(result.Success);
        Assert.IsNull(result.Transition);
        Assert.AreEqual(0, factoryCalls);
        Assert.IsEmpty(store.Commits);
        Assert.AreEqual(PluginRuntimeState.Inactive, manager.GetSnapshot(PluginOneId).State);
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

    private sealed class FakeDiscovery(string marker) : IDiscoveryCapability
    {
        public string Marker { get; } = marker;
        public DiscoveryProviderId ProviderId => SharedDiscoveryId;

        public ValueTask<DiscoveryResult> DiscoverAsync(DiscoveryRequest request, PluginInvocationContext context)
            => ValueTask.FromResult(new DiscoveryResult(Array.Empty<DiscoveryCandidate>(), Array.Empty<PluginDiagnostic>()));
    }

    private sealed class FakeMigration(StateOwnerId owner, ProviderStateLocation location) : IProviderStateMigrationCapability
    {
        public StateOwnerId StateOwnerId => owner;
        public int CurrentSchemaVersion => 1;
        public bool DataStoreWasBlocked { get; private set; }

        public async ValueTask<ProviderStatePatch> MigrateAsync(ProviderStateSnapshot state, PluginInvocationContext context)
        {
            try
            {
                await context.HostServices.DataStore.OpenReadAsync("state.json", context.OperationCancellation);
            }
            catch (InvalidOperationException)
            {
                DataStoreWasBlocked = true;
            }

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
        public ValueTask<OperationOutcome> RequestAsync(string configId, Guid folderId, string historyItemId, CancellationToken cancellationToken) => ValueTask.FromResult(OperationOutcome.Success);
    }

    private sealed class FakeHistory : IHistoryQueryService
    {
        public ValueTask<IReadOnlyList<HistoryItemSnapshot>> QueryAsync(string configId, Guid? folderId, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<HistoryItemSnapshot>>(Array.Empty<HistoryItemSnapshot>());
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

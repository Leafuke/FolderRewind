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
    public async Task StagingFailureKeepsPrimaryAndCleanupDiagnosticsAndRequiresRestart()
    {
        var plugin = new FakePlugin(
            _ => throw new InvalidOperationException("activation primary failure"),
            _ => ValueTask.FromException(new InvalidOperationException("cleanup failure")));
        var manager = new PluginRuntimeManager();

        var result = await manager.ActivateAsync(Candidate(PluginOneId, plugin, new FakeStore()));

        Assert.IsFalse(result.Success);
        Assert.AreEqual(OperationOutcome.Failed, result.Outcome);
        Assert.IsTrue(result.RequiresRestart);
        CollectionAssert.Contains(result.Diagnostics.Select(value => value.Code).ToArray(), "runtime.activation_failed");
        CollectionAssert.Contains(result.Diagnostics.Select(value => value.Code).ToArray(), "runtime.candidate_cleanup_failed");
        StringAssert.Contains(manager.GetSnapshot(PluginOneId).LastError, "activation primary failure");
        StringAssert.Contains(manager.GetSnapshot(PluginOneId).LastError, "cleanup failure");
        Assert.AreEqual(1, manager.RestartRetainedCount);
    }

    [TestMethod]
    public async Task PhysicalUnloadFailureMarksRestartAndBlocksReactivation()
    {
        var manager = new PluginRuntimeManager();
        await manager.ActivateAsync(Candidate(PluginOneId, PluginWithDiscovery("one"), new FakeStore()));

        manager.MarkPhysicalUnloadFailed(PluginOneId, "simulated physical unload failure");

        var snapshot = manager.GetSnapshot(PluginOneId);
        Assert.AreEqual(PluginRuntimeState.Active, snapshot.State);
        Assert.IsTrue(snapshot.RequiresRestart);
        StringAssert.Contains(snapshot.LastError, "simulated physical unload failure");
        var retry = await manager.ReplaceAsync(
            Candidate(PluginOneId, PluginWithDiscovery("two"), new FakeStore()));
        Assert.IsFalse(retry.Success);
        Assert.AreEqual(OperationOutcome.Blocked, retry.Outcome);
        Assert.IsTrue(retry.RequiresRestart);
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
    public async Task StableSelectorCanAcquireTheSecondCapabilityIdentity()
    {
        var kindA = new ConfigKindRef(new OwnerId(PluginOneId.Value), "kind-a");
        var kindB = new ConfigKindRef(new OwnerId(PluginOneId.Value), "kind-b");
        var plugin = new FakePlugin(context =>
        {
            context.RegisterCapability<IConfigReconciliationCapability>(new FakeConfigReconciliation(kindA));
            context.RegisterCapability<IConfigReconciliationCapability>(new FakeConfigReconciliation(kindB));
            return PluginActivationResult.Empty;
        });
        var manager = new PluginRuntimeManager();

        var result = await manager.ActivateAsync(Candidate(PluginOneId, plugin, new FakeStore()));

        Assert.IsTrue(result.Success);
        using var lease = manager.TryAcquire<IConfigReconciliationCapability>(
            PluginOneId,
            capability => capability.Kind == kindB);
        Assert.IsNotNull(lease);
        Assert.AreEqual(kindB, lease.Capability.Kind);
        Assert.AreEqual(1, manager.GetSnapshot(PluginOneId).ActiveLeases);
    }

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

    [TestMethod]
    public async Task DuplicateAggregateCapabilityInstancesAreRejected()
    {
        var plugin = new FakePlugin(context =>
        {
            context.RegisterCapability<IDiscoveryCapability>(
                new FakeDiscovery("one", new DiscoveryProviderId("com.folderrewind.discovery.one")));
            context.RegisterCapability<IDiscoveryCapability>(
                new FakeDiscovery("two", new DiscoveryProviderId("com.folderrewind.discovery.two")));
            return PluginActivationResult.Empty;
        });
        var store = new FakeStore();
        var manager = new PluginRuntimeManager();

        var result = await manager.ActivateAsync(Candidate(PluginOneId, plugin, store));

        Assert.IsFalse(result.Success);
        Assert.AreEqual(PluginRuntimeState.Failed, result.State);
        Assert.IsEmpty(store.Commits);
        Assert.IsNull(manager.TryAcquire<IDiscoveryCapability>(PluginOneId));
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
    public async Task DeactivationExceptionDoesNotUndoLogicalIsolationAndRequiresRestart()
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
        Assert.IsTrue(result.RequiresRestart);
        Assert.AreEqual(1, manager.RestartRetainedCount);
        Assert.IsTrue(manager.GetSnapshot(PluginOneId).RequiresRestart);
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
        Assert.IsTrue(migration.ReadOnlyServicesWereAvailable);
        Assert.IsTrue(migration.MutationServicesWereBlocked);
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

        public ValueTask<IConsistencyLease> AcquireAsync(
            BackupConsistencyRequest request,
            PluginInvocationContext context)
            => throwOnAcquire
                ? ValueTask.FromException<IConsistencyLease>(
                    new InvalidOperationException("consistency acquire failed"))
                : ValueTask.FromResult<IConsistencyLease>(lease);
    }

    private sealed class FakeConsistencyLease(string sourcePath, bool throwOnDispose = false) : IConsistencyLease
    {
        public string SourcePath { get; } = sourcePath;
        public bool IsStableSourceView => true;
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

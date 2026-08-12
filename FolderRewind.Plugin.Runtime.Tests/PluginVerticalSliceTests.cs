using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Operations;
using MineRewind;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class PluginVerticalSliceTests
{
    private static readonly PluginId FakePluginId = new("com.folderrewind.vertical-fake");
    private static readonly PluginId MineRewindPluginId = new(MinecraftSavesPlugin.PluginIdentity);
    private static readonly ConfigKindRef FakeKind = new(new OwnerId(FakePluginId.Value), "test-data");
    private static readonly ConfigKindRef MinecraftKind = new(
        new OwnerId(MinecraftSavesPlugin.PluginIdentity),
        MinecraftSavesPlugin.MinecraftKindIdentity);

    [TestMethod]
    public async Task FakeDiscoveryRunsOnlyAfterRuntimeActivationCommit()
    {
        var events = new List<string>();
        var fixture = await ActivateFakeAsync(events);
        var draftStore = new RecordingDiscoveryDraftStore(events);
        var coordinator = new PluginDiscoveryCoordinator(fixture.Manager, draftStore);

        var run = await coordinator.DiscoverAsync(
            FakePluginId,
            new DiscoveryRequest(["C:\\Users\\Test"]),
            autoCreateConfigs: true);

        CollectionAssert.AreEqual(new[] { "commit", "discover", "draft-commit" }, events);
        Assert.IsTrue(run.DraftsCommitted);
        Assert.HasCount(1, run.Discovery.Candidates);
        Assert.AreEqual(FakeKind, run.Discovery.Candidates[0].ConfigDrafts.Single().Kind);
        Assert.HasCount(1, draftStore.Commits);
    }

    [TestMethod]
    public async Task FakeConsistencySuppliesOneSourceToDiffAndArchiveAndReceivesCancellation()
    {
        var events = new List<string>();
        var fixture = await ActivateFakeAsync(events);
        using var cancellation = new CancellationTokenSource();
        await using var capabilityLease = fixture.Manager.TryAcquire<IBackupConsistencyCapability>(
            FakePluginId,
            cancellation.Token);
        Assert.IsNotNull(capabilityLease);
        var (config, folder) = Snapshot(FakeKind, "C:\\Data");

        await using var sourceLease = await capabilityLease.Capability.AcquireAsync(
            new BackupConsistencyRequest(config, folder, ConsistencyIntent.Prefer),
            capabilityLease.Context);
        events.Add($"diff:{sourceLease.SourcePath}");
        events.Add($"archive:{sourceLease.SourcePath}");

        Assert.AreEqual(cancellation.Token, capabilityLease.Context.OperationCancellation);
        CollectionAssert.AreEqual(
            new[] { "commit", "consistency", "diff:C:\\Data", "archive:C:\\Data" },
            events);
    }

    [TestMethod]
    public async Task FakeRestoreUsesSafetyBackupAndOnceOnlyMutationContinuation()
    {
        var events = new List<string>();
        var fixture = await ActivateFakeAsync(events);
        using var lease = fixture.Manager.TryAcquire<IRestoreCoordinatorCapability>(FakePluginId);
        Assert.IsNotNull(lease);
        var (config, folder) = Snapshot(FakeKind, "C:\\Data");
        var gate = new RestoreMutationContinuationGate(_ =>
        {
            events.Add("mutation");
            return ValueTask.FromResult(OperationOutcome.Success);
        });

        var result = await lease.Capability.CoordinateAsync(
            new RestoreCoordinatorRequest(config, folder, "history-1", gate.InvokeAsync),
            lease.Context);

        Assert.AreEqual(OperationOutcome.Success, result.Outcome);
        Assert.IsTrue(gate.WasInvoked);
        CollectionAssert.AreEqual(new[] { "commit", "safety-backup", "mutation" }, events);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => gate.InvokeAsync(CancellationToken.None).AsTask());
    }

    [TestMethod]
    public async Task FakeCommandRoutesThroughHostBackupService()
    {
        var events = new List<string>();
        var fixture = await ActivateFakeAsync(events);
        using var lease = fixture.Manager.TryAcquire<IPluginCommandCapability>(FakePluginId);
        Assert.IsNotNull(lease);

        var result = await lease.Capability.ExecuteAsync(
            new PluginCommandRequest(
                new PluginCommandId(FakePluginId, "backup"),
                new Dictionary<string, JsonElement> { ["configId"] = Json("\"config-1\"") }),
            lease.Context);

        Assert.AreEqual(OperationOutcome.Success, result.Outcome);
        CollectionAssert.AreEqual(new[] { "commit", "command", "safety-backup" }, events);
    }

    [TestMethod]
    public async Task MineRewindDiscoveryReturnsHostOwnedDraftThroughRuntime()
    {
        using var world = TemporaryWorld.Create();
        var events = new List<string>();
        var fixture = await ActivateMineRewindAsync(events);
        var draftStore = new RecordingDiscoveryDraftStore(events);
        var coordinator = new PluginDiscoveryCoordinator(fixture.Manager, draftStore);

        var run = await coordinator.DiscoverAsync(
            MineRewindPluginId,
            new DiscoveryRequest([world.Path]),
            autoCreateConfigs: true);

        CollectionAssert.AreEqual(new[] { "commit", "draft-commit" }, events);
        Assert.IsTrue(run.DraftsCommitted);
        var draft = run.Discovery.Candidates.Single().ConfigDrafts.Single();
        Assert.AreEqual(MinecraftKind, draft.Kind);
        Assert.AreEqual(world.Path, draft.Folders.Single().Path);
        Assert.AreEqual(0, fixture.Host.Configs.FindCalls);
        Assert.HasCount(1, draftStore.Commits);
    }

    [TestMethod]
    public async Task MineRewindConsistencyRunsBeforeOneSourceIsConsumedThroughRuntime()
    {
        using var world = TemporaryWorld.Create();
        var events = new List<string>();
        var fixture = await ActivateMineRewindAsync(events, knotLinkAvailable: true);
        using var capabilityLease = fixture.Manager.TryAcquire<IBackupConsistencyCapability>(MineRewindPluginId);
        Assert.IsNotNull(capabilityLease);
        var (config, folder) = Snapshot(MinecraftKind, world.Path);

        await using var sourceLease = await capabilityLease.Capability.AcquireAsync(
            new BackupConsistencyRequest(config, folder, ConsistencyIntent.Require),
            capabilityLease.Context);
        events.Add($"diff:{sourceLease.SourcePath}");
        events.Add($"archive:{sourceLease.SourcePath}");

        CollectionAssert.AreEqual(
            new[] { "commit", "knot:minebackup.save", $"diff:{world.Path}", $"archive:{world.Path}" },
            events);
    }

    [TestMethod]
    public async Task MineRewindRestoreCoordinatesBackupMutationAndRejoinThroughRuntime()
    {
        using var world = TemporaryWorld.Create();
        var events = new List<string>();
        var fixture = await ActivateMineRewindAsync(events, knotLinkAvailable: true);
        using var lease = fixture.Manager.TryAcquire<IRestoreCoordinatorCapability>(MineRewindPluginId);
        Assert.IsNotNull(lease);
        var (config, folder) = Snapshot(MinecraftKind, world.Path);
        var gate = new RestoreMutationContinuationGate(_ =>
        {
            events.Add("mutation");
            return ValueTask.FromResult(OperationOutcome.Success);
        });

        var result = await lease.Capability.CoordinateAsync(
            new RestoreCoordinatorRequest(config, folder, "history-1", gate.InvokeAsync),
            lease.Context);

        Assert.AreEqual(OperationOutcome.Success, result.Outcome);
        Assert.IsTrue(gate.WasInvoked);
        CollectionAssert.AreEqual(
            new[] { "commit", "knot:minebackup.save-and-exit", "safety-backup", "mutation", "knot:minebackup.rejoin" },
            events);
    }

    [TestMethod]
    public async Task MineRewindCommandRoutesThroughHostServiceAfterRuntimeActivation()
    {
        var events = new List<string>();
        var fixture = await ActivateMineRewindAsync(events);
        using var lease = fixture.Manager.TryAcquire<IPluginCommandCapability>(MineRewindPluginId);
        Assert.IsNotNull(lease);

        var result = await lease.Capability.ExecuteAsync(
            new PluginCommandRequest(
                new PluginCommandId(MineRewindPluginId, "hot-backup"),
                new Dictionary<string, JsonElement> { ["configId"] = Json("\"config-1\"") }),
            lease.Context);

        Assert.AreEqual(OperationOutcome.Success, result.Outcome);
        CollectionAssert.AreEqual(new[] { "commit", "safety-backup" }, events);
    }

    private static async Task<RuntimeFixture> ActivateFakeAsync(List<string> events)
    {
        var host = new RecordingHostServices(events, knotLinkAvailable: false);
        var manager = new PluginRuntimeManager();
        var result = await manager.ActivateAsync(Candidate(
            FakePluginId,
            () => new FakeVerticalPlugin(events),
            host,
            events,
            new Dictionary<string, JsonElement>()));
        Assert.IsTrue(result.Success);
        return new RuntimeFixture(manager, host);
    }

    private static async Task<RuntimeFixture> ActivateMineRewindAsync(
        List<string> events,
        bool knotLinkAvailable = false)
    {
        var host = new RecordingHostServices(events, knotLinkAvailable);
        var manager = new PluginRuntimeManager();
        var result = await manager.ActivateAsync(Candidate(
            MineRewindPluginId,
            static () => new MinecraftSavesPlugin(),
            host,
            events,
            new Dictionary<string, JsonElement>
            {
                ["AutoDiscoverSaves"] = Json("true"),
                ["AutoCreateConfigs"] = Json("false"),
                ["PreservePlayerData"] = Json("false")
            }));
        Assert.IsTrue(result.Success, string.Join(", ", result.Diagnostics.Select(diagnostic => diagnostic.Code)));
        return new RuntimeFixture(manager, host);
    }

    private static PluginActivationCandidate Candidate(
        PluginId pluginId,
        Func<IFolderRewindPlugin> factory,
        RecordingHostServices host,
        List<string> events,
        IReadOnlyDictionary<string, JsonElement> settings)
        => new(
            pluginId,
            factory,
            new PluginSettingsSnapshot(pluginId, settings),
            Array.Empty<ConfigSnapshot>(),
            host,
            new RecordingActivationStore(events));

    private static (ConfigSnapshot Config, FolderSnapshot Folder) Snapshot(ConfigKindRef kind, string path)
    {
        var folder = new FolderSnapshot(Guid.NewGuid(), path, "World", EmptyStates());
        var config = new ConfigSnapshot("config-1", kind, "Config", [folder], EmptyStates());
        return (config, folder);
    }

    private static IReadOnlyDictionary<StateOwnerId, ProviderStateSnapshot> EmptyStates()
        => new Dictionary<StateOwnerId, ProviderStateSnapshot>();

    private static JsonElement Json(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private sealed record RuntimeFixture(PluginRuntimeManager Manager, RecordingHostServices Host);

    private sealed class RecordingActivationStore(List<string> events) : IPluginActivationStore
    {
        public ValueTask CommitAsync(PluginActivationCommit commit, CancellationToken cancellationToken)
        {
            events.Add("commit");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingDiscoveryDraftStore(List<string> events) : IDiscoveryDraftStore
    {
        public List<DiscoveryDraftCommit> Commits { get; } = new();

        public ValueTask CommitAsync(DiscoveryDraftCommit commit, CancellationToken cancellationToken)
        {
            events.Add("draft-commit");
            Commits.Add(commit);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeVerticalPlugin(List<string> events) :
        IFolderRewindPlugin,
        IDiscoveryCapability,
        IBackupConsistencyCapability,
        IRestoreCoordinatorCapability,
        IPluginCommandCapability
    {
        public DiscoveryProviderId ProviderId { get; } = new(FakePluginId.Value);
        public ConfigKindRef Kind => FakeKind;
        public IReadOnlyList<PluginCommandDescriptor> Commands { get; } =
        [
            new(
                new PluginCommandId(FakePluginId, "backup"),
                "Backup",
                Json("{\"type\":\"object\",\"required\":[\"configId\"]}"))
        ];

        public ValueTask<PluginActivationResult> ActivateAsync(IPluginActivationContext context, CancellationToken cancellationToken)
        {
            context.RegisterCapability<IPluginCapability>(this);
            return ValueTask.FromResult(PluginActivationResult.Empty);
        }

        public ValueTask DeactivateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<DiscoveryResult> DiscoverAsync(DiscoveryRequest request, PluginInvocationContext context)
        {
            events.Add("discover");
            var folder = new FolderDraft("C:\\Data", "Data", new Dictionary<StateOwnerId, ProviderStateDraft>());
            var config = new ConfigDraft(FakeKind, "Data", [folder], new Dictionary<StateOwnerId, ProviderStateDraft>());
            return ValueTask.FromResult(new DiscoveryResult(
                [new DiscoveryCandidate("candidate-1", "Data", [config])],
                Array.Empty<PluginDiagnostic>()));
        }

        public ValueTask<IConsistencyLease> AcquireAsync(BackupConsistencyRequest request, PluginInvocationContext context)
        {
            events.Add("consistency");
            return ValueTask.FromResult<IConsistencyLease>(new FakeConsistencyLease(request.Folder.Path));
        }

        public async ValueTask<RestoreCoordinatorResult> CoordinateAsync(
            RestoreCoordinatorRequest request,
            PluginInvocationContext context)
        {
            var safetyBackup = await context.HostServices.Backups.RequestAsync(
                request.Config.ConfigId,
                request.Folder.FolderId,
                context.OperationCancellation);
            var outcome = safetyBackup == OperationOutcome.Success
                ? await request.ContinueMutationAsync(context.OperationCancellation)
                : safetyBackup;
            return new RestoreCoordinatorResult(outcome, Array.Empty<PluginDiagnostic>());
        }

        public async ValueTask<PluginCommandResult> ExecuteAsync(
            PluginCommandRequest request,
            PluginInvocationContext context)
        {
            events.Add("command");
            var configId = request.Arguments["configId"].GetString()!;
            var outcome = await context.HostServices.Backups.RequestAsync(
                configId,
                null,
                context.OperationCancellation);
            return new PluginCommandResult(
                outcome,
                new Dictionary<string, JsonElement>(),
                Array.Empty<PluginDiagnostic>());
        }
    }

    private sealed class FakeConsistencyLease(string sourcePath) : IConsistencyLease
    {
        public string SourcePath { get; } = sourcePath;
        public IReadOnlyList<PluginDiagnostic> Diagnostics { get; } = Array.Empty<PluginDiagnostic>();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingHostServices : IPluginHostServices
    {
        public RecordingHostServices(List<string> events, bool knotLinkAvailable)
        {
            Configs = new RecordingConfigQuery();
            Backups = new RecordingBackupRequests(events);
            Restores = new RecordingRestoreRequests();
            History = new EmptyHistory();
            Notifications = new NoOpNotifications();
            KnotLink = new RecordingKnotLink(events, knotLinkAvailable);
            DataStore = new MemoryDataStore();
            TemporaryStorage = new TemporaryStorage();
            Logger = new NoOpLogger();
        }

        public RecordingConfigQuery Configs { get; }
        IReadOnlyConfigQueryService IPluginHostServices.Configs => Configs;
        public IBackupRequestService Backups { get; }
        public IRestoreRequestService Restores { get; }
        public IHistoryQueryService History { get; }
        public IPluginNotificationService Notifications { get; }
        public IKnotLinkHostService KnotLink { get; }
        public IPluginDataStore DataStore { get; }
        public IPluginTemporaryStorage TemporaryStorage { get; }
        public IPluginLogger Logger { get; }
    }

    private sealed class RecordingConfigQuery : IReadOnlyConfigQueryService
    {
        public int FindCalls { get; private set; }
        public ValueTask<ConfigSnapshot?> FindAsync(string configId, CancellationToken cancellationToken)
        {
            FindCalls++;
            return ValueTask.FromResult<ConfigSnapshot?>(null);
        }
    }

    private sealed class RecordingBackupRequests(List<string> events) : IBackupRequestService
    {
        public ValueTask<OperationOutcome> RequestAsync(string configId, Guid? folderId, CancellationToken cancellationToken)
        {
            events.Add("safety-backup");
            return ValueTask.FromResult(OperationOutcome.Success);
        }
    }

    private sealed class RecordingRestoreRequests : IRestoreRequestService
    {
        public ValueTask<OperationOutcome> RequestAsync(
            string configId,
            Guid folderId,
            string historyItemId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(OperationOutcome.Success);
    }

    private sealed class EmptyHistory : IHistoryQueryService
    {
        public ValueTask<IReadOnlyList<HistoryItemSnapshot>> QueryAsync(
            string configId,
            Guid? folderId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<HistoryItemSnapshot>>(Array.Empty<HistoryItemSnapshot>());
    }

    private sealed class NoOpNotifications : IPluginNotificationService
    {
        public ValueTask ShowAsync(
            string title,
            string message,
            DiagnosticSeverity severity,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class RecordingKnotLink(List<string> events, bool available) : IKnotLinkHostService
    {
        public bool IsAvailable { get; } = available;
        public ValueTask SendAsync(
            string eventName,
            IReadOnlyDictionary<string, string> arguments,
            CancellationToken cancellationToken)
        {
            events.Add($"knot:{eventName}");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryDataStore : IPluginDataStore
    {
        public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
            => ValueTask.FromResult<Stream>(new MemoryStream());
        public ValueTask<Stream> OpenWriteAsync(string relativePath, CancellationToken cancellationToken)
            => ValueTask.FromResult<Stream>(new MemoryStream());
    }

    private sealed class TemporaryStorage : IPluginTemporaryStorage
    {
        public ValueTask<string> CreateDirectoryAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(Path.GetTempPath());
    }

    private sealed class NoOpLogger : IPluginLogger
    {
        public void Log(DiagnosticSeverity severity, string message, Exception? exception = null) { }
    }

    private sealed class TemporaryWorld : IDisposable
    {
        private TemporaryWorld(string path) => Path = path;
        public string Path { get; }

        public static TemporaryWorld Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "FolderRewind-M3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            File.WriteAllBytes(System.IO.Path.Combine(path, "level.dat"), [0x0A]);
            return new TemporaryWorld(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}

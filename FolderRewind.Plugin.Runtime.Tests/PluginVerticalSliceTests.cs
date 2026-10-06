using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Operations;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class PluginVerticalSliceTests
{
    private static readonly PluginId FakePluginId = new("com.folderrewind.vertical-fake");
    private static readonly ConfigKindRef FakeKind = new(new OwnerId(FakePluginId.Value), "test-data");

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
            new RestoreCoordinatorRequest(config, [folder], "version-1", Guid.NewGuid(), WorkspaceOperationKind.Restore, gate.InvokeAsync),
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
    public async Task ConfigReconciliationRejectsStaleProposalBeforeStoreMutation()
    {
        var events = new List<string>();
        var plugin = new FakeVerticalPlugin(events)
        {
            ReconciliationProposal = Proposal(
                new ConfigRevision("stale-revision"),
                [new AddFolderChange(Folder("C:\\Additional"))])
        };
        var fixture = await ActivateFakeAsync(events, plugin);
        var store = new RecordingConfigChangeStore(events);
        var coordinator = new ConfigReconciliationCoordinator(fixture.Manager, store);
        var (config, _) = Snapshot(FakeKind, "C:\\Data");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ReconcileAsync(
            FakePluginId,
            new ConfigReconciliationRequest(config, "startup"),
            new ConfigReconciliationApplyPolicy(true, true),
            proposalConfirmed: false).AsTask());

        Assert.HasCount(0, store.Commits);
        CollectionAssert.AreEqual(new[] { "commit" }, events);
    }

    [TestMethod]
    public async Task UserOwnedOrDestructiveConfigChangesAlwaysRequireReview()
    {
        var events = new List<string>();
        var (config, folder) = Snapshot(FakeKind, "C:\\Data");
        var plugin = new FakeVerticalPlugin(events)
        {
            ReconciliationProposal = Proposal(
                config.Revision,
                [new RemoveFolderChange(folder.FolderId)])
        };
        var fixture = await ActivateFakeAsync(events, plugin);
        var store = new RecordingConfigChangeStore(events);
        var coordinator = new ConfigReconciliationCoordinator(fixture.Manager, store);

        var result = await coordinator.ReconcileAsync(
            FakePluginId,
            new ConfigReconciliationRequest(config, "world removed"),
            new ConfigReconciliationApplyPolicy(true, true),
            proposalConfirmed: false);

        Assert.AreEqual(ConfigReconciliationStatus.ReviewRequired, result.Status);
        Assert.HasCount(0, store.Commits);
    }

    [TestMethod]
    public async Task ExplicitlyAuthorizedProviderOwnedChangesCommitAsOneRevision()
    {
        var events = new List<string>();
        var (config, _) = Snapshot(FakeKind, "C:\\Data");
        var plugin = new FakeVerticalPlugin(events)
        {
            ReconciliationProposal = Proposal(
                config.Revision,
                [
                    new AddFolderChange(Folder("C:\\Additional")),
                    new SetProviderOptionsChange(
                        new StateOwnerId(FakePluginId.Value),
                        1,
                        Json("{\"enabled\":true}"))
                ])
        };
        var fixture = await ActivateFakeAsync(events, plugin);
        var store = new RecordingConfigChangeStore(events);
        var coordinator = new ConfigReconciliationCoordinator(fixture.Manager, store);

        var result = await coordinator.ReconcileAsync(
            FakePluginId,
            new ConfigReconciliationRequest(config, "startup"),
            new ConfigReconciliationApplyPolicy(true, true),
            proposalConfirmed: false);

        Assert.AreEqual(ConfigReconciliationStatus.Committed, result.Status);
        Assert.AreEqual(new ConfigRevision("revision-2"), result.CommittedRevision);
        Assert.HasCount(1, store.Commits);
        Assert.HasCount(2, store.Commits.Single().Changes);
        CollectionAssert.AreEqual(new[] { "commit", "config-commit" }, events);
    }

    [TestMethod]
    public async Task ConfigStoreFailureLeavesProposalUncommitted()
    {
        var events = new List<string>();
        var (config, _) = Snapshot(FakeKind, "C:\\Data");
        var plugin = new FakeVerticalPlugin(events)
        {
            ReconciliationProposal = Proposal(
                config.Revision,
                [new AddFolderChange(Folder("C:\\Additional"))])
        };
        var fixture = await ActivateFakeAsync(events, plugin);
        var store = new RecordingConfigChangeStore(events) { FailBeforeCommit = true };
        var coordinator = new ConfigReconciliationCoordinator(fixture.Manager, store);

        await Assert.ThrowsExactlyAsync<IOException>(() => coordinator.ReconcileAsync(
            FakePluginId,
            new ConfigReconciliationRequest(config, "startup"),
            new ConfigReconciliationApplyPolicy(true, false),
            proposalConfirmed: false).AsTask());

        Assert.HasCount(0, store.Commits);
        Assert.AreEqual(new ConfigRevision("revision-1"), store.CurrentRevision);
    }

    [TestMethod]
    public async Task PreviewCancellationReleasesLeaseAndRasterOutputIsFrozen()
    {
        var events = new List<string>();
        var plugin = new FakeVerticalPlugin(events);
        var fixture = await ActivateFakeAsync(events, plugin);
        var (config, folder) = Snapshot(FakeKind, "C:\\Data");
        var dispatcher = new SpatialPreviewDispatcher(fixture.Manager);
        using var cancellation = new CancellationTokenSource();
        var render = dispatcher.RenderAsync(new(new(config, folder, Guid.NewGuid()), "layer", 0, 0, 1, null), cancellation.Token).AsTask();
        await plugin.PreviewStarted.Task;
        Assert.AreEqual(1, fixture.Manager.GetSnapshot(FakePluginId).ActiveLeases);
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => render);
        Assert.AreEqual(0, fixture.Manager.GetSnapshot(FakePluginId).ActiveLeases);
        Assert.IsTrue((await fixture.Manager.DeactivateAsync(FakePluginId)).Success);

        var bytes = new byte[256 * 256 * 4]; bytes[0] = 42;
        var frozen = SpatialPreviewDispatcher.FreezeTile(new(256, 256, bytes, []));
        bytes[0] = 9;
        Assert.AreEqual((byte)42, frozen.BgraPremultiplied.Span[0]);
        Assert.ThrowsExactly<InvalidDataException>(() => SpatialPreviewDispatcher.FreezeTile(new(512, 256, bytes, [])));
        Assert.ThrowsExactly<InvalidDataException>(() => SpatialPreviewDispatcher.FreezeTile(new(256, 256, new byte[4], [])));
    }

    private static async Task<RuntimeFixture> ActivateFakeAsync(
        List<string> events,
        FakeVerticalPlugin? plugin = null)
    {
        var host = new RecordingHostServices(events, knotLinkAvailable: false);
        var manager = new PluginRuntimeManager();
        var result = await manager.ActivateAsync(Candidate(
            FakePluginId,
            () => plugin ?? new FakeVerticalPlugin(events),
            host,
            events,
            new Dictionary<string, JsonElement>()));
        Assert.IsTrue(result.Success);
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
        var config = new ConfigSnapshot("config-1", new ConfigRevision("revision-1"), kind, "Config", [folder], EmptyStates());
        return (config, folder);
    }

    private static IReadOnlyDictionary<StateOwnerId, ProviderStateSnapshot> EmptyStates()
        => new Dictionary<StateOwnerId, ProviderStateSnapshot>();

    private static JsonElement Json(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static FolderDraft Folder(string path)
        => new(path, Path.GetFileName(path), new Dictionary<StateOwnerId, ProviderStateDraft>());

    private static ConfigChangeProposal Proposal(
        ConfigRevision revision,
        IReadOnlyList<ConfigChange> changes)
        => new(
            "proposal-1",
            "config-1",
            revision,
            "Provider reconciliation",
            changes,
            Array.Empty<PluginDiagnostic>());

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

    private sealed class RecordingConfigChangeStore(List<string> events) : IConfigChangeStore
    {
        public bool FailBeforeCommit { get; init; }
        public ConfigRevision CurrentRevision { get; private set; } = new("revision-1");
        public List<ConfigChangeCommit> Commits { get; } = new();

        public ValueTask<ConfigRevision> CommitAsync(
            ConfigChangeCommit commit,
            CancellationToken cancellationToken)
        {
            if (commit.ExpectedRevision != CurrentRevision)
            {
                throw new InvalidOperationException("stale");
            }
            if (FailBeforeCommit)
            {
                throw new IOException("fault before atomic commit");
            }

            Commits.Add(commit);
            CurrentRevision = new ConfigRevision("revision-2");
            events.Add("config-commit");
            return ValueTask.FromResult(CurrentRevision);
        }
    }

    private sealed class FakeVerticalPlugin(List<string> events) :
        IFolderRewindPlugin,
        IDiscoveryCapability,
        IConfigReconciliationCapability,
        IBackupConsistencyCapability,
        IRestoreCoordinatorCapability,
        IPluginCommandCapability,
        ISpatialPreviewCapability
    {
        public TaskCompletionSource PreviewStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<SpatialPreviewDescription> DescribeAsync(SpatialPreviewSource source, PluginInvocationContext context)
            => throw new NotSupportedException();
        public async ValueTask<SpatialPreviewTile> RenderAsync(SpatialPreviewTileRequest request, PluginInvocationContext context)
        {
            PreviewStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, context.OperationCancellation);
            throw new InvalidOperationException("Cancellation was not observed.");
        }
        public ValueTask<SpatialPreviewPoint> InspectAsync(SpatialPreviewPointRequest request, PluginInvocationContext context)
            => throw new NotSupportedException();
        public ConfigChangeProposal? ReconciliationProposal { get; init; }
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

        public ValueTask<ConfigChangeProposal?> ProposeAsync(
            ConfigReconciliationRequest request,
            PluginInvocationContext context)
            => ValueTask.FromResult(ReconciliationProposal);

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
                request.Folders.Single().FolderId,
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
        public bool IsStableSourceView => true;
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
        public RecordingKnotLink KnotLink { get; }
        IKnotLinkHostService IPluginHostServices.KnotLink => KnotLink;
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
        public ValueTask<OperationOutcome> RequestQuickAsync(
            string configId,
            Guid folderId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(OperationOutcome.Success);

        public ValueTask<OperationOutcome> RequestAsync(
            string configId,
            Guid folderId,
            string versionId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(OperationOutcome.Success);
    }

    private sealed class EmptyHistory : IHistoryQueryService
    {
        public ValueTask<IReadOnlyList<HistoryVersionSnapshot>> QueryAsync(
            string configId,
            Guid? folderId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<HistoryVersionSnapshot>>(Array.Empty<HistoryVersionSnapshot>());
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

}

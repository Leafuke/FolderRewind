using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Loading;
using FolderRewind.Plugin.Runtime.Operations;
using FolderRewind.Plugin.Runtime.Packaging;
using FolderRewind.Plugin.Runtime.Settings;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class PluginVerticalSliceTests
{
    private static readonly PluginId FakePluginId = new("com.folderrewind.vertical-fake");
    private const string MineRewindSha256 = "f7284f87f2e65f5c8f8a6bdc0e0f3ae6052021a070cbbd04ddae0d5c6061ca62";
    private static readonly PluginId MineRewindPluginId = new("com.folderrewind.minerewind");
    private static readonly ConfigKindRef FakeKind = new(new OwnerId(FakePluginId.Value), "test-data");
    private static readonly ConfigKindRef MinecraftKind = new(
        new OwnerId(MineRewindPluginId.Value),
        "minecraft-saves");

    [ClassCleanup]
    public static void CleanupBlackBoxExtractions()
        => BlackBoxExtraction.CleanupPending();

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
            new RestoreCoordinatorRequest(config, folder, "version-1", gate.InvokeAsync),
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
        await using var fixture = await ActivateMineRewindAsync(events);
        var draftStore = new RecordingDiscoveryDraftStore(events);
        var coordinator = new PluginDiscoveryCoordinator(fixture.Manager, draftStore);

        var run = await coordinator.DiscoverAsync(
            MineRewindPluginId,
            new DiscoveryRequest([world.Path]),
            autoCreateConfigs: true);

        using var discoveryLease = fixture.Manager.TryAcquire<IDiscoveryCapability>(MineRewindPluginId);
        Assert.IsNotNull(discoveryLease);
        var catalog = discoveryLease.Capability as IDiscoveryDefinitionCatalog;
        Assert.IsNotNull(catalog);
        var definition = catalog.Definitions.Single();
        Assert.AreEqual("minecraft-java", definition.DefinitionId);
        Assert.AreEqual(
            definition.DefinitionId,
            catalog.ResolveDefinitionId(run.Discovery.Candidates.Single()));

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
        using var sessionLock = world.AcquireSessionLock();
        var events = new List<string>();
        await using var fixture = await ActivateMineRewindAsync(events, knotLinkAvailable: true);
        fixture.Host.KnotLink.OnSendAsync = async (eventName, _, _) =>
        {
            var response = eventName switch
            {
                "handshake" => "HANDSHAKE_RESPONSE",
                "pre_hot_backup" => "WORLD_SAVED",
                _ => null
            };
            if (response is null) return;
            using var signalLease = fixture.Manager.TryAcquire<IKnotLinkIntegrationCapability>(MineRewindPluginId);
            Assert.IsNotNull(signalLease);
            await signalLease.Capability.ExecuteAsync(
                response,
                response == "HANDSHAKE_RESPONSE"
                    ? new Dictionary<string, string> { ["mod_version"] = "3.0.0" }
                    : new Dictionary<string, string>(),
                signalLease.Context);
        };
        using var capabilityLease = fixture.Manager.TryAcquire<IBackupConsistencyCapability>(MineRewindPluginId);
        Assert.IsNotNull(capabilityLease);
        var (config, folder) = Snapshot(MinecraftKind, world.Path);

        var sourceLease = await capabilityLease.Capability.AcquireAsync(
            new BackupConsistencyRequest(config, folder, ConsistencyIntent.Require),
            capabilityLease.Context);
        var capturedSource = sourceLease.SourcePath;
        events.Add($"diff:{sourceLease.SourcePath}");
        events.Add($"archive:{sourceLease.SourcePath}");

        CollectionAssert.AreEqual(
            new[]
            {
                "commit",
                "knot:handshake",
                "knot:handshake_ack",
                "knot:pre_hot_backup",
                $"diff:{capturedSource}",
                $"archive:{capturedSource}"
            },
            events);
        Assert.AreNotEqual(world.Path, capturedSource);
        Assert.IsTrue(File.Exists(Path.Combine(capturedSource, "level.dat")));
        await sourceLease.DisposeAsync();
        Assert.IsFalse(Directory.Exists(capturedSource));
    }

    [TestMethod]
    public async Task MineRewindRestoreCoordinatesBackupMutationAndRejoinThroughRuntime()
    {
        using var world = TemporaryWorld.Create();
        using var sessionLock = world.AcquireSessionLock();
        var events = new List<string>();
        await using var fixture = await ActivateMineRewindAsync(events, knotLinkAvailable: true);
        fixture.Host.KnotLink.OnSendAsync = async (eventName, _, _) =>
        {
            string? response = null;
            IReadOnlyDictionary<string, string> arguments = new Dictionary<string, string>();
            if (eventName == "handshake")
            {
                response = "HANDSHAKE_RESPONSE";
                arguments = new Dictionary<string, string> { ["mod_version"] = "3.0.0" };
            }
            else if (eventName == "pre_hot_restore")
            {
                sessionLock.Dispose();
                response = "WORLD_SAVE_AND_EXIT_COMPLETE";
            }
            else if (eventName == "rejoin_world")
            {
                response = "REJOIN_RESULT";
                arguments = new Dictionary<string, string> { ["result"] = "success" };
            }
            if (response is null) return;
            using var signalLease = fixture.Manager.TryAcquire<IKnotLinkIntegrationCapability>(MineRewindPluginId);
            Assert.IsNotNull(signalLease);
            await signalLease.Capability.ExecuteAsync(response, arguments, signalLease.Context);
        };
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
            new[]
            {
                "commit",
                "knot:handshake",
                "knot:handshake_ack",
                "knot:pre_hot_restore",
                "mutation",
                "knot:restore_finished",
                "knot:rejoin_world",
                "knot:hot_restore_complete"
            },
            events);
    }

    [TestMethod]
    public async Task MineRewindCommandRoutesThroughHostServiceAfterRuntimeActivation()
    {
        var events = new List<string>();
        await using var fixture = await ActivateMineRewindAsync(events);
        using var lease = fixture.Manager.TryAcquire<IPluginCommandCapability>(MineRewindPluginId);
        Assert.IsNotNull(lease);

        var result = await lease.Capability.ExecuteAsync(
            new PluginCommandRequest(
                new PluginCommandId(MineRewindPluginId, "hotbackup.active-world"),
                new Dictionary<string, JsonElement> { ["configId"] = Json("\"config-1\"") }),
            lease.Context);

        Assert.AreEqual(OperationOutcome.Success, result.Outcome);
        CollectionAssert.AreEqual(new[] { "commit", "safety-backup" }, events);
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

    private static async Task<MineRuntimeFixture> ActivateMineRewindAsync(
        List<string> events,
        bool knotLinkAvailable = false)
    {
        var repositoryRoot = FindRepositoryRoot();
        var packagePath = Path.Combine(
            repositoryRoot,
            "FolderRewind",
            "Assets",
            "Plugins",
            "MineRewind-1.9.2.frplugin");
        var sidecarSha256 = (await File.ReadAllTextAsync(packagePath + ".sha256"))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.AreEqual(MineRewindSha256, sidecarSha256);
        var package = await PluginPackageValidator.ValidateAsync(packagePath, sidecarSha256);
        Assert.AreEqual(MineRewindPluginId, package.Manifest.Contract.PluginId);
        Assert.AreEqual("1.9.2", package.Manifest.Contract.Version);
        CollectionAssert.AreEquivalent(
            new[]
            {
                HostServiceKind.ConfigQuery,
                HostServiceKind.BackupRequest,
                HostServiceKind.RestoreRequest,
                HostServiceKind.HistoryQuery,
                HostServiceKind.KnotLink,
                HostServiceKind.TemporaryStorage,
                HostServiceKind.Logging
            },
            package.Manifest.Contract.RequestedHostServices.ToArray());
        Assert.IsFalse(package.Entries.Any(entry => entry.CanonicalPath.EndsWith(
            "FolderRewind.Plugin.Abstractions.dll",
            StringComparison.OrdinalIgnoreCase)));

        var extraction = BlackBoxExtraction.Create();
        await PluginPackageValidator.ExtractAsync(package, extraction.PayloadPath);
        var manifest = PluginPackageManifestReader.Parse(
            await File.ReadAllBytesAsync(Path.Combine(extraction.PayloadPath, "manifest.json")));
        var loaded = PluginAssemblyLoader.Load(new PluginLoadRequest(
            manifest.Contract.PluginId,
            extraction.PayloadPath,
            manifest.Contract.EntryAssembly,
            manifest.Contract.EntryType,
            manifest.Contract.RequiredApi));
        var schema = PluginSettingsSchema.Parse(
            await File.ReadAllBytesAsync(Path.Combine(extraction.PayloadPath, manifest.Contract.SettingsSchema)));
        var settings = schema.Validate(new PluginSettingsSnapshot(
            manifest.Contract.PluginId,
            new Dictionary<string, JsonElement>())).NormalizedSettings;
        var host = new RecordingHostServices(events, knotLinkAvailable);
        var manager = new PluginRuntimeManager();
        try
        {
            var result = await manager.ActivateAsync(new PluginActivationCandidate(
                manifest.Contract.PluginId,
                () => loaded.Instance,
                settings,
                Array.Empty<ConfigSnapshot>(),
                host,
                new RecordingActivationStore(events),
                manifest.Contract));
            Assert.IsTrue(result.Success, string.Join(", ", result.Diagnostics.Select(diagnostic => diagnostic.Code)));
            return new MineRuntimeFixture(manager, host, loaded, extraction);
        }
        catch
        {
            loaded.Dispose();
            extraction.Dispose();
            throw;
        }
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

    private sealed class MineRuntimeFixture(
        PluginRuntimeManager manager,
        RecordingHostServices host,
        LoadedPluginAssembly loaded,
        BlackBoxExtraction extraction) : IAsyncDisposable
    {
        public PluginRuntimeManager Manager { get; } = manager;
        public RecordingHostServices Host { get; } = host;

        public async ValueTask DisposeAsync()
        {
            await Manager.DeactivateAsync(MineRewindPluginId);
            loaded.Dispose();
            extraction.Dispose();
        }
    }

    private sealed class BlackBoxExtraction : IDisposable
    {
        private static readonly System.Collections.Concurrent.ConcurrentBag<string> PendingCleanup = new();

        private BlackBoxExtraction(string root)
        {
            Root = root;
            PayloadPath = Path.Combine(root, "payload");
        }

        public string Root { get; }
        public string PayloadPath { get; }

        public static BlackBoxExtraction Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "FolderRewind-BlackBox",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new BlackBoxExtraction(root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PendingCleanup.Add(Root);
            }
        }

        public static void CleanupPending()
        {
            while (PendingCleanup.TryTake(out var path))
            {
                for (var attempt = 0; attempt < 10 && Directory.Exists(path); attempt++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    try
                    {
                        Directory.Delete(path, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Windows may retain mapped plugin modules until testhost exits even after
                        // collectible ALC disposal. The OS temp root remains the recovery boundary.
                    }
                }
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "FolderRewind",
                    "Assets",
                    "Plugins",
                    "MineRewind-1.9.2.frplugin")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("FolderRewind repository root could not be located.");
    }

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
        IPluginCommandCapability
    {
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
        public Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task>? OnSendAsync { get; set; }
        public async ValueTask SendAsync(
            string eventName,
            IReadOnlyDictionary<string, string> arguments,
            CancellationToken cancellationToken)
        {
            events.Add($"knot:{eventName}");
            if (OnSendAsync is not null)
            {
                await OnSendAsync(eventName, arguments, cancellationToken);
            }
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

        public FileStream AcquireSessionLock()
        {
            var path = System.IO.Path.Combine(Path, "session.lock");
            File.WriteAllBytes(path, [0]);
            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}

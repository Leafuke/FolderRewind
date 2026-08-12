using System.Text;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class ArtifactVerticalSliceTests
{
    private static readonly PluginId PluginId = new("com.folderrewind.fake-delta");
    private static readonly ArtifactTransformerId TransformerId = new(PluginId, "reverse-delta");
    private static readonly RestoreStrategyId StrategyId = new(PluginId, "reverse-materializer");
    private static readonly ArtifactFormatRef DeltaFormat = new(new OwnerId(PluginId.Value), "reverse-delta");
    private static readonly ArtifactFormatRef CoreFormat = new(new OwnerId("folderrewind.core"), "archive-set");
    private static readonly RestoreStrategyId CoreStrategy = new(new PluginId("folderrewind.core"), "archive-materializer");
    private static readonly ConfigKindRef Kind = new(new OwnerId("com.folderrewind.domain"), "world");

    [TestMethod]
    public async Task FakeReverseDeltaCommitsGraphAndMaterializesOldHistory()
    {
        using var repository = TemporaryDirectory.Create("M3-Artifact");
        var store = new FileArtifactLedgerStore(repository.Path);
        var oldArtifact = await AddCoreHistoryAsync(store, "history-old", "old world");
        var newArtifact = await AddCoreHistoryAsync(store, "history-new", "new world");
        var runtime = await ActivateAsync();
        var (config, folder) = Snapshots();
        var transform = new ArtifactTransformCoordinator(runtime, store);

        var result = await transform.TransformAsync(
            PluginId,
            config,
            folder,
            "history-new",
            new ArtifactTransformPolicy(
                TransformerId,
                new Dictionary<string, JsonElement>(),
                ArtifactTransformFailureBehavior.RequireTransform),
            ["history-old"],
            new ArtifactTransformLimits(10, 1_000_000));

        Assert.IsTrue(result.GraphCommitted);
        var oldRoot = result.Ledger.HistoryRoots.Single(root => root.HistoryItemId == "history-old");
        var newRoot = result.Ledger.HistoryRoots.Single(root => root.HistoryItemId == "history-new");
        Assert.AreNotEqual(oldArtifact, oldRoot.RootArtifactId);
        Assert.AreEqual(newArtifact, newRoot.RootArtifactId);
        var delta = result.Ledger.Artifacts.Single(artifact => artifact.ArtifactId == oldRoot.RootArtifactId);
        Assert.AreEqual(DeltaFormat, delta.Format);
        CollectionAssert.AreEqual(new[] { newArtifact }, delta.Dependencies.ToArray());

        var restore = new RestoreMaterializationCoordinator(runtime, store);
        using var workspaceRoot = TemporaryDirectory.Create("M3-Restore");
        await using var materialized = await restore.MaterializeAsync(
            config,
            folder,
            "history-old",
            RestoreMode.Overwrite,
            RestoreMode.Overwrite,
            Path.Combine(workspaceRoot.Path, "workspace"),
            new ArtifactTransformLimits(10, 1_000_000));

        Assert.AreEqual(OperationOutcome.Success, materialized.Outcome);
        Assert.IsNotNull(materialized.WorkspacePath);
        Assert.AreEqual(
            "old world",
            await File.ReadAllTextAsync(Path.Combine(materialized.WorkspacePath, "world.dat")));
    }

    [TestMethod]
    public async Task RemovingMiddleHistoryRootKeepsReachableDependencyAndBlocksPhysicalDelete()
    {
        using var repository = TemporaryDirectory.Create("M3-Reachability");
        var store = new FileArtifactLedgerStore(repository.Path);
        var replacedOldArtifact = await AddCoreHistoryAsync(store, "history-old", "old world");
        var newArtifact = await AddCoreHistoryAsync(store, "history-new", "new world");
        var runtime = await ActivateAsync();
        var (config, folder) = Snapshots();
        var transform = new ArtifactTransformCoordinator(runtime, store);
        var transformed = await transform.TransformAsync(
            PluginId,
            config,
            folder,
            "history-new",
            Policy(),
            ["history-old"],
            new ArtifactTransformLimits(10, 1_000_000));

        await store.RemoveHistoryRootAsync(
            "history-new",
            new ArtifactGraphRevision("after-history-delete"));
        var afterDelete = await store.LoadAsync();
        var reachable = ArtifactLedgerValidator.ComputeReachable(afterDelete);

        Assert.Contains(newArtifact, reachable);
        Assert.ThrowsExactly<InvalidOperationException>(
            () => FileArtifactLedgerStore.EnsureArtifactCanBePhysicallyDeleted(afterDelete, newArtifact));
        CollectionAssert.AreEqual(
            new[] { replacedOldArtifact },
            (await store.GarbageCollectUnreachableAsync()).ToArray());
        Assert.Contains(newArtifact, (await store.LoadAsync()).Artifacts.Select(artifact => artifact.ArtifactId));
    }

    [TestMethod]
    public async Task GraphTransactionFaultBeforeMetadataSwitchRetainsCommittedRoots()
    {
        using var repository = TemporaryDirectory.Create("M3-Transaction");
        var seedStore = new FileArtifactLedgerStore(repository.Path);
        var oldArtifact = await AddCoreHistoryAsync(seedStore, "history-old", "old world");
        var newArtifact = await AddCoreHistoryAsync(seedStore, "history-new", "new world");
        var before = await seedStore.LoadAsync();
        var faultingStore = new FileArtifactLedgerStore(repository.Path, stage =>
        {
            if (stage == ArtifactTransactionStage.BeforeMetadataSwitch) throw new IOException("fault injection");
        });
        var runtime = await ActivateAsync();
        var (config, folder) = Snapshots();

        await Assert.ThrowsExactlyAsync<IOException>(() => new ArtifactTransformCoordinator(runtime, faultingStore)
            .TransformAsync(
                PluginId,
                config,
                folder,
                "history-new",
                Policy(),
                ["history-old"],
                new ArtifactTransformLimits(10, 1_000_000))
            .AsTask());

        var after = await seedStore.LoadAsync();
        Assert.AreEqual(before.Revision, after.Revision);
        Assert.AreEqual(oldArtifact, after.HistoryRoots.Single(root => root.HistoryItemId == "history-old").RootArtifactId);
        Assert.AreEqual(newArtifact, after.HistoryRoots.Single(root => root.HistoryItemId == "history-new").RootArtifactId);
        await seedStore.RecoverAsync();
        Assert.HasCount(2, (await seedStore.LoadAsync()).Artifacts);
    }

    [TestMethod]
    public async Task SmartOrPartialPrimaryIsRejectedBeforeStagingSideEffect()
    {
        using var repository = TemporaryDirectory.Create("M3-Compatibility");
        var store = new FileArtifactLedgerStore(repository.Path);
        await AddCoreHistoryAsync(
            store,
            "history-old",
            "old world",
            ArtifactCompleteness.Partial,
            CoreCaptureMode.Smart);
        await AddCoreHistoryAsync(store, "history-new", "new world");
        var plugin = new FakeReverseDeltaPlugin();
        var runtime = await ActivateAsync(plugin);
        var (config, folder) = Snapshots();

        var result = await new ArtifactTransformCoordinator(runtime, store).TransformAsync(
            PluginId,
            config,
            folder,
            "history-old",
            Policy(),
            [],
            new ArtifactTransformLimits(10, 1_000_000));

        Assert.AreEqual(OperationOutcome.Blocked, result.Outcome);
        Assert.IsFalse(result.GraphCommitted);
        Assert.AreEqual(0, plugin.StagingWrites);
    }

    [TestMethod]
    public async Task RestoreMissingMaterializerBlocksBeforeWorkspaceCreation()
    {
        using var repository = TemporaryDirectory.Create("M3-MissingOwner");
        var store = new FileArtifactLedgerStore(repository.Path);
        await AddCoreHistoryAsync(store, "history-old", "old world");
        await AddCoreHistoryAsync(store, "history-new", "new world");
        var runtime = await ActivateAsync();
        var (config, folder) = Snapshots();
        await new ArtifactTransformCoordinator(runtime, store).TransformAsync(
            PluginId,
            config,
            folder,
            "history-new",
            Policy(),
            ["history-old"],
            new ArtifactTransformLimits(10, 1_000_000));
        Assert.IsTrue((await runtime.DeactivateAsync(PluginId)).Success);
        using var workspaceRoot = TemporaryDirectory.Create("M3-BlockedWorkspace");
        var expected = Path.Combine(workspaceRoot.Path, "workspace");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new RestoreMaterializationCoordinator(runtime, store)
            .MaterializeAsync(
                config,
                folder,
                "history-old",
                RestoreMode.Clean,
                RestoreMode.Clean,
                expected,
                new ArtifactTransformLimits(10, 1_000_000))
            .AsTask());

        Assert.IsFalse(Directory.Exists(expected));
    }

    [TestMethod]
    public async Task MaterializedRestoreMutatesTargetOnceFromIsolatedWorkspace()
    {
        using var repository = TemporaryDirectory.Create("M3-Mutation");
        var store = new FileArtifactLedgerStore(repository.Path);
        await AddCoreHistoryAsync(store, "history-old", "old world");
        await AddCoreHistoryAsync(store, "history-new", "new world");
        var runtime = await ActivateAsync();
        var (config, folder) = Snapshots();
        await new ArtifactTransformCoordinator(runtime, store).TransformAsync(
            PluginId,
            config,
            folder,
            "history-new",
            Policy(),
            ["history-old"],
            new ArtifactTransformLimits(10, 1_000_000));
        using var workspace = TemporaryDirectory.Create("M3-MutationWorkspace");
        var mutations = 0;
        var coordinator = new RestoreArtifactMutationCoordinator(
            new RestoreMaterializationCoordinator(runtime, store));

        var result = await coordinator.ExecuteAsync(
            config,
            folder,
            "history-old",
            RestoreMode.Overwrite,
            RestoreMode.Overwrite,
            Path.Combine(workspace.Path, "restore"),
            async (source, cancellationToken) =>
            {
                mutations++;
                Assert.AreEqual("old world", await File.ReadAllTextAsync(Path.Combine(source, "world.dat"), cancellationToken));
                return OperationOutcome.Success;
            },
            new ArtifactTransformLimits(10, 1_000_000));

        Assert.AreEqual(OperationOutcome.Success, result.Outcome);
        Assert.IsTrue(result.TargetMutationStarted);
        Assert.AreEqual(1, mutations);
    }

    [TestMethod]
    public async Task StagingRejectsTraversalCaseCollisionAndQuotaOverflow()
    {
        using var temporary = TemporaryDirectory.Create("M3-Staging");
        await using var staging = new ArtifactTransformStagingArea(temporary.Path, 2, 4);
        var artifact = await staging.CreateArtifactAsync(CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => staging.OpenWriteAsync(artifact.Staging, "../escape", CancellationToken.None).AsTask());
        await using (var stream = await staging.OpenWriteAsync(artifact.Staging, "Data.bin", CancellationToken.None))
        {
            await stream.WriteAsync(new byte[] { 1, 2, 3, 4 });
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => staging.OpenWriteAsync(artifact.Staging, "data.BIN", CancellationToken.None).AsTask());
        await using var overflow = await staging.OpenWriteAsync(artifact.Staging, "other.bin", CancellationToken.None);
        await Assert.ThrowsExactlyAsync<IOException>(
            () => overflow.WriteAsync(new byte[] { 5 }, CancellationToken.None).AsTask());
    }

    [TestMethod]
    public void LedgerRejectsCyclesAndCrossFolderDependencies()
    {
        var first = Entry(new ArtifactId(Guid.NewGuid()), "history-1", Guid.Parse("11111111-1111-1111-1111-111111111111"), []);
        var second = Entry(new ArtifactId(Guid.NewGuid()), "history-2", Guid.Parse("22222222-2222-2222-2222-222222222222"), [first.ArtifactId]);
        Assert.ThrowsExactly<InvalidDataException>(() => ArtifactLedgerValidator.Validate(new ArtifactLedgerDocument(
            1,
            new ArtifactGraphRevision("cross-folder"),
            [first, second],
            [new ArtifactHistoryRoot("history-2", "config-1", second.FolderId, second.ArtifactId)])));

        first = first with { FolderId = second.FolderId, Dependencies = [second.ArtifactId] };
        Assert.ThrowsExactly<InvalidDataException>(() => ArtifactLedgerValidator.Validate(new ArtifactLedgerDocument(
            1,
            new ArtifactGraphRevision("cycle"),
            [first, second],
            [new ArtifactHistoryRoot("history-2", "config-1", second.FolderId, second.ArtifactId)])));
    }

    [TestMethod]
    public void LedgerRejectsDependencyChainsBeyondBoundedDepth()
    {
        var artifacts = new List<ArtifactLedgerEntry>();
        ArtifactId? previous = null;
        for (var index = 0; index <= ArtifactLedgerValidator.MaximumDependencyDepth; index++)
        {
            var id = new ArtifactId(Guid.NewGuid());
            artifacts.Add(Entry(
                id,
                $"history-{index}",
                FolderId,
                previous.HasValue ? [previous.Value] : []));
            previous = id;
        }

        Assert.ThrowsExactly<InvalidDataException>(() => ArtifactLedgerValidator.Validate(new ArtifactLedgerDocument(
            1,
            new ArtifactGraphRevision("too-deep"),
            artifacts,
            [new ArtifactHistoryRoot(
                $"history-{ArtifactLedgerValidator.MaximumDependencyDepth}",
                "config-1",
                FolderId,
                previous!.Value)])));
    }

    [TestMethod]
    public async Task RestoreIntegrityMismatchFailsBeforeWorkspaceCreation()
    {
        using var repository = TemporaryDirectory.Create("M3-Integrity");
        var store = new FileArtifactLedgerStore(repository.Path);
        await AddCoreHistoryAsync(store, "history-old", "old world");
        await AddCoreHistoryAsync(store, "history-new", "new world");
        var runtime = await ActivateAsync();
        var (config, folder) = Snapshots();
        await new ArtifactTransformCoordinator(runtime, store).TransformAsync(
            PluginId,
            config,
            folder,
            "history-new",
            Policy(),
            ["history-old"],
            new ArtifactTransformLimits(10, 1_000_000));
        var ledger = await store.LoadAsync();
        var delta = ledger.Artifacts.Single(artifact => artifact.Format == DeltaFormat);
        await File.WriteAllTextAsync(
            Path.Combine(repository.Path, delta.ContentRelativePath.Replace('/', Path.DirectorySeparatorChar), "reverse.bin"),
            "tampered");
        using var workspace = TemporaryDirectory.Create("M3-IntegrityWorkspace");
        var workspacePath = Path.Combine(workspace.Path, "restore");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new RestoreMaterializationCoordinator(runtime, store)
            .MaterializeAsync(
                config,
                folder,
                "history-old",
                RestoreMode.Clean,
                RestoreMode.Clean,
                workspacePath,
                new ArtifactTransformLimits(10, 1_000_000))
            .AsTask());

        Assert.IsFalse(Directory.Exists(workspacePath));
    }

    [TestMethod]
    public async Task CompletionObserverIsAtMostOnceAndFailureOnlyPromotesWarning()
    {
        var plugin = new FakeReverseDeltaPlugin { ThrowFromObserver = true };
        var runtime = await ActivateAsync(plugin);
        var (config, folder) = Snapshots();
        var coordinator = new BackupCompletionObserverCoordinator(runtime);
        var snapshot = new BackupCompletionSnapshot(
            "run-1",
            config,
            folder,
            "history-1",
            new ArtifactId(Guid.NewGuid()),
            new ArtifactGraphRevision("committed"),
            OperationOutcome.Success,
            Array.Empty<PluginDiagnostic>(),
            CloudQueueCommitted: true);

        var first = await coordinator.ObserveAsync(snapshot, [PluginId]);
        var second = await coordinator.ObserveAsync(snapshot, [PluginId]);

        Assert.AreEqual(OperationOutcome.SuccessWithWarnings, first.Outcome);
        Assert.AreEqual(OperationOutcome.Success, second.Outcome);
        Assert.AreEqual(1, plugin.ObserverCalls);
        Assert.IsTrue(first.Diagnostics.Any(diagnostic => diagnostic.Code == "backup.observer_failed"));
    }

    [TestMethod]
    public async Task ArtifactManifestDisclosureIsValidatedBeforePluginFactoryRuns()
    {
        var factoryCalls = 0;
        var runtime = new PluginRuntimeManager();
        var manifest = Manifest(
            [HostServiceKind.ArtifactRead, HostServiceKind.RestoreMaterializationWorkspace],
            hasObserver: true);
        var result = await runtime.ActivateAsync(new PluginActivationCandidate(
            PluginId,
            () =>
            {
                factoryCalls++;
                return new FakeReverseDeltaPlugin();
            },
            new PluginSettingsSnapshot(PluginId, new Dictionary<string, JsonElement>()),
            Array.Empty<ConfigSnapshot>(),
            new HostServices(),
            new ActivationStore(),
            manifest));

        Assert.IsFalse(result.Success);
        Assert.AreEqual(0, factoryCalls);
    }

    [TestMethod]
    public async Task RuntimeArtifactRegistrationsMustMatchStaticManifest()
    {
        var runtime = new PluginRuntimeManager();
        var manifest = Manifest(
            [
                HostServiceKind.ArtifactRead,
                HostServiceKind.ArtifactTransformStaging,
                HostServiceKind.RestoreMaterializationWorkspace
            ],
            hasObserver: false);
        var result = await runtime.ActivateAsync(new PluginActivationCandidate(
            PluginId,
            static () => new FakeReverseDeltaPlugin(),
            new PluginSettingsSnapshot(PluginId, new Dictionary<string, JsonElement>()),
            Array.Empty<ConfigSnapshot>(),
            new HostServices(),
            new ActivationStore(),
            manifest));

        Assert.IsFalse(result.Success);
        Assert.IsNull(runtime.TryAcquire<IBackupArtifactTransformerCapability>(PluginId));
    }

    private static ArtifactTransformPolicy Policy()
        => new(
            TransformerId,
            new Dictionary<string, JsonElement>(),
            ArtifactTransformFailureBehavior.RequireTransform);

    private static PluginManifestContract Manifest(
        IReadOnlyList<HostServiceKind> services,
        bool hasObserver)
    {
        var localized = new LocalizedText("Fake", new Dictionary<string, string>());
        return new PluginManifestContract(
            PluginId,
            "1.0.0",
            new PluginApiVersion(3, 0),
            "Fake.dll",
            "Fake.Plugin",
            localized,
            localized,
            Array.Empty<ConfigKindDeclaration>(),
            "settings.schema.json",
            services,
            [
                PluginCapabilityKind.BackupArtifactTransformer,
                PluginCapabilityKind.BackupCompletionObserver,
                PluginCapabilityKind.RestoreMaterializer
            ],
            [new ArtifactFormatDeclaration(DeltaFormat, 1, 1, localized)],
            [new ArtifactTransformerDeclaration(
                TransformerId,
                [Kind],
                [CoreCaptureMode.Full],
                [ArtifactCompleteness.Complete],
                JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone(),
                [ArtifactTransformFailureBehavior.RequireTransform])],
            [new RestoreStrategyDeclaration(
                StrategyId,
                [new ArtifactFormatVersionRange(DeltaFormat, 1, 1)],
                [ArtifactCompleteness.Complete],
                [RestoreMode.Clean, RestoreMode.Overwrite])],
            hasObserver);
    }

    private static async Task<PluginRuntimeManager> ActivateAsync(FakeReverseDeltaPlugin? plugin = null)
    {
        var runtime = new PluginRuntimeManager();
        var result = await runtime.ActivateAsync(new PluginActivationCandidate(
            PluginId,
            () => plugin ?? new FakeReverseDeltaPlugin(),
            new PluginSettingsSnapshot(PluginId, new Dictionary<string, JsonElement>()),
            Array.Empty<ConfigSnapshot>(),
            new HostServices(),
            new ActivationStore()));
        Assert.IsTrue(result.Success);
        return runtime;
    }

    private static async Task<ArtifactId> AddCoreHistoryAsync(
        FileArtifactLedgerStore store,
        string historyItemId,
        string content,
        ArtifactCompleteness completeness = ArtifactCompleteness.Complete,
        CoreCaptureMode mode = CoreCaptureMode.Full)
    {
        var transactionId = "core-" + Guid.NewGuid().ToString("N");
        await using var staging = store.CreateStagingArea(transactionId, 10, 1_000_000);
        var allocation = await staging.CreateArtifactAsync(CancellationToken.None);
        await using (var stream = await staging.OpenWriteAsync(allocation.Staging, "world.dat", CancellationToken.None))
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(content));
        }
        var facts = await staging.SealAsync("artifacts");
        var fact = facts[allocation.Staging];
        var current = await store.LoadAsync();
        var entry = new ArtifactLedgerEntry(
            allocation.ArtifactId,
            CoreFormat,
            1,
            CoreStrategy,
            "config-1",
            FolderId,
            historyItemId,
            fact.ContentRelativePath,
            fact.LogicalSha256,
            fact.LogicalSize,
            fact.StorageSha256,
            fact.StorageSize,
            completeness,
            mode,
            Array.Empty<ArtifactId>(),
            transactionId,
            ArtifactAvailability.Available,
            ArtifactAvailability.Pending);
        var candidate = current with
        {
            Revision = new ArtifactGraphRevision(Guid.NewGuid().ToString("N")),
            Artifacts = current.Artifacts.Append(entry).ToArray(),
            HistoryRoots = current.HistoryRoots.Append(
                new ArtifactHistoryRoot(historyItemId, "config-1", FolderId, allocation.ArtifactId)).ToArray()
        };
        await store.CommitAsync(current, candidate, transactionId, staging, facts);
        return allocation.ArtifactId;
    }

    private static readonly Guid FolderId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static (ConfigSnapshot Config, FolderSnapshot Folder) Snapshots()
    {
        var folder = new FolderSnapshot(FolderId, "C:\\World", "World", EmptyStates());
        return (new ConfigSnapshot("config-1", new ConfigRevision("revision-1"), Kind, "World", [folder], EmptyStates()), folder);
    }

    private static Dictionary<StateOwnerId, ProviderStateSnapshot> EmptyStates() => new();

    private static ArtifactLedgerEntry Entry(
        ArtifactId id,
        string history,
        Guid folder,
        IReadOnlyList<ArtifactId> dependencies)
        => new(
            id,
            CoreFormat,
            1,
            CoreStrategy,
            "config-1",
            folder,
            history,
            $"artifacts/{id.Value:N}",
            new string('a', 64),
            1,
            new string('b', 64),
            1,
            ArtifactCompleteness.Complete,
            CoreCaptureMode.Full,
            dependencies,
            "transaction",
            ArtifactAvailability.Available,
            ArtifactAvailability.Pending);

    private sealed class FakeReverseDeltaPlugin :
        IFolderRewindPlugin,
        IBackupArtifactTransformerCapability,
        IRestoreMaterializerCapability,
        IBackupCompletionObserverCapability
    {
        public int StagingWrites { get; private set; }
        public int ObserverCalls { get; private set; }
        public bool ThrowFromObserver { get; init; }
        public ArtifactTransformerId TransformerId => ArtifactVerticalSliceTests.TransformerId;
        public RestoreStrategyId RestoreStrategyId => StrategyId;

        public ValueTask<PluginActivationResult> ActivateAsync(IPluginActivationContext context, CancellationToken cancellationToken)
        {
            context.RegisterCapability<IPluginCapability>(this);
            return ValueTask.FromResult(PluginActivationResult.Empty);
        }

        public ValueTask DeactivateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask<ArtifactTransformResult> TransformAsync(
            ArtifactTransformRequest request,
            PluginInvocationContext context)
        {
            if (request.Primary.Completeness != ArtifactCompleteness.Complete
                || request.Primary.CoreCaptureMode != CoreCaptureMode.Full)
            {
                return new ArtifactTransformResult(OperationOutcome.Blocked, null, Array.Empty<PluginDiagnostic>());
            }
            var older = request.CompatibleCandidates.SingleOrDefault(candidate =>
                candidate.Completeness == ArtifactCompleteness.Complete
                && candidate.CoreCaptureMode == CoreCaptureMode.Full);
            if (older is null)
            {
                return new ArtifactTransformResult(OperationOutcome.NoChanges, null, Array.Empty<PluginDiagnostic>());
            }

            var allocation = await request.Staging.CreateArtifactAsync(context.OperationCancellation);
            StagingWrites++;
            await using (var source = await request.ArtifactRead.OpenReadAsync(older.Content, "world.dat", context.OperationCancellation))
            await using (var destination = await request.Staging.OpenWriteAsync(allocation.Staging, "reverse.bin", context.OperationCancellation))
            {
                await source.CopyToAsync(destination, context.OperationCancellation);
            }
            return new ArtifactTransformResult(
                OperationOutcome.Success,
                new ArtifactGraphPatch(
                    request.ExpectedGraphRevision,
                    [new StagedArtifactNode(
                        allocation.ArtifactId,
                        allocation.Staging,
                        older.HistoryItemId,
                        DeltaFormat,
                        1,
                        StrategyId,
                        ArtifactCompleteness.Complete,
                        CoreCaptureMode.Full,
                        [request.Primary.ArtifactId])],
                    [new HistoryRootReplacement(older.HistoryItemId, older.ArtifactId, allocation.ArtifactId)]),
                Array.Empty<PluginDiagnostic>());
        }

        public async ValueTask<RestoreMaterializationResult> MaterializeAsync(
            RestoreMaterializationRequest request,
            PluginInvocationContext context)
        {
            Assert.AreEqual(RestoreMode.Overwrite, request.RequestedMode);
            Assert.AreEqual(RestoreMode.Overwrite, request.EffectiveMode);
            var delta = request.ArtifactsTopologicallySorted.Single(artifact => artifact.Format == DeltaFormat);
            await using var source = await request.ArtifactRead.OpenReadAsync(delta.Content, "reverse.bin", context.OperationCancellation);
            await using var destination = await request.Workspace.OpenWriteAsync("world.dat", context.OperationCancellation);
            await source.CopyToAsync(destination, context.OperationCancellation);
            return new RestoreMaterializationResult(OperationOutcome.Success, Array.Empty<PluginDiagnostic>());
        }

        public ValueTask<BackupCompletionObserverResult> ObserveAsync(
            BackupCompletionSnapshot snapshot,
            PluginInvocationContext context)
        {
            ObserverCalls++;
            if (ThrowFromObserver) throw new IOException("observer failed");
            return ValueTask.FromResult(new BackupCompletionObserverResult(Array.Empty<PluginDiagnostic>()));
        }
    }

    private sealed class ActivationStore : IPluginActivationStore
    {
        public ValueTask CommitAsync(PluginActivationCommit commit, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class HostServices : IPluginHostServices
    {
        public IReadOnlyConfigQueryService Configs { get; } = new NoConfigQuery();
        public IBackupRequestService Backups { get; } = new NoBackupRequests();
        public IRestoreRequestService Restores { get; } = new NoRestoreRequests();
        public IHistoryQueryService History { get; } = new NoHistory();
        public IPluginNotificationService Notifications { get; } = new NoNotifications();
        public IKnotLinkHostService KnotLink { get; } = new NoKnotLink();
        public IPluginDataStore DataStore { get; } = new MemoryDataStore();
        public IPluginTemporaryStorage TemporaryStorage { get; } = new TempStorage();
        public IPluginLogger Logger { get; } = new NoLogger();
    }

    private sealed class NoConfigQuery : IReadOnlyConfigQueryService
    {
        public ValueTask<ConfigSnapshot?> FindAsync(string configId, CancellationToken cancellationToken) => ValueTask.FromResult<ConfigSnapshot?>(null);
    }
    private sealed class NoBackupRequests : IBackupRequestService
    {
        public ValueTask<OperationOutcome> RequestAsync(string configId, Guid? folderId, CancellationToken cancellationToken) => ValueTask.FromResult(OperationOutcome.Blocked);
    }
    private sealed class NoRestoreRequests : IRestoreRequestService
    {
        public ValueTask<OperationOutcome> RequestAsync(string configId, Guid folderId, string historyItemId, CancellationToken cancellationToken) => ValueTask.FromResult(OperationOutcome.Blocked);
    }
    private sealed class NoHistory : IHistoryQueryService
    {
        public ValueTask<IReadOnlyList<HistoryItemSnapshot>> QueryAsync(string configId, Guid? folderId, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<HistoryItemSnapshot>>(Array.Empty<HistoryItemSnapshot>());
    }
    private sealed class NoNotifications : IPluginNotificationService
    {
        public ValueTask ShowAsync(string title, string message, DiagnosticSeverity severity, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
    private sealed class NoKnotLink : IKnotLinkHostService
    {
        public bool IsAvailable => false;
        public ValueTask SendAsync(string eventName, IReadOnlyDictionary<string, string> arguments, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
    private sealed class MemoryDataStore : IPluginDataStore
    {
        public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken) => ValueTask.FromResult<Stream>(new MemoryStream());
        public ValueTask<Stream> OpenWriteAsync(string relativePath, CancellationToken cancellationToken) => ValueTask.FromResult<Stream>(new MemoryStream());
    }
    private sealed class TempStorage : IPluginTemporaryStorage
    {
        public ValueTask<string> CreateDirectoryAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Path.GetTempPath());
    }
    private sealed class NoLogger : IPluginLogger
    {
        public void Log(DiagnosticSeverity severity, string message, Exception? exception = null) { }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;
        public string Path { get; }
        public static TemporaryDirectory Create(string prefix)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}

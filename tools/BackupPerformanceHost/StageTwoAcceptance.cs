using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Merge;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using static FolderRewind.PerformanceHost.Program;

namespace FolderRewind.PerformanceHost;

// 生产服务的无窗口接线验收；不以模拟 Capture 回调代替 BackupService。
internal static class StageTwoAcceptance
{
    private static readonly Type Native = typeof(BackupService).Assembly.GetType("FolderRewind.Services.NativeHistoryApplicationService")!;
    private static readonly List<object> Results = [];
    internal static async Task RunRestoreCleanupAsync()
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var assembly = typeof(BackupService).Assembly;
        foreach (var scenario in new[] { "materialize-failure", "legacy-197", "legacy-unproven" })
        {
            var (config, folder) = await CreateAsync("restore-cleanup-" + scenario, BackupMode.Full, BackupArchiveVerificationLevel.Strict);
            var history = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
            var source = new SourceId(Guid.Parse(folder.Id));
            var workspace = (await history.WorkspaceStore.LoadAsync()).Value!;
            var selected = workspace.GetSourceState(source).BaseVersionId!.Value;
            File.WriteAllText(Path.Combine(folder.Path, "keep.txt"), "current user world");
            var digest = (await MergeTreeManifest.ReadAsync(folder.Path, _ => true, default)).Digest;
            string? container = null;
            if (scenario == "materialize-failure")
            {
                var backendType = assembly.GetType("FolderRewind.Services.SevenZipHistoryArchiveBackend")!;
                var backend = (IArchiveRepresentationBackend)Activator.CreateInstance(backendType, instance, null, [config, true], null)!;
                var handler = new FailedHostMaterializer(new CoreArchiveRepresentationHandler(backend));
                var restore = new HistoryRestoreService(history, new RepresentationRuntime([handler]),
                    async token => new RepresentationEnvironment((await history.LocalReplicaCatalogStore.LoadAsync(token)).Value!.Entries, [], []),
                    new FileSystemHistoryRestoreMutationBackend());
                typeof(HistoryRestoreService).GetProperty("UseTargetVolumeWorkspaces", instance)!.SetValue(restore, true);
                var failed = await restore.RestoreVersionAsync(selected, new(source, folder.Path), workspace, HistoryRestoreApplyMode.Clean);
                Require(failed.Status == HistoryRestoreStatus.BlockedBeforeMutation && handler.Materialized,
                    "Expected a failure after the real 7-Zip materializer produced staging output.");
            }
            else
            {
                var storeType = assembly.GetType("FolderRewind.History.Application.HistoryRestoreTransactionJournalStore")!;
                var store = Activator.CreateInstance(storeType, instance, null, [history, new FileSystemHistoryRestoreMutationBackend()], null)!;
                var sessionType = assembly.GetType("FolderRewind.History.Application.HistoryRestorePreparationSession")!;
                using (var session = (IDisposable)Activator.CreateInstance(sessionType, instance, null,
                    [history, store, workspace, new[] { new HistoryRestoreSourceBinding(source, folder.Path) }, Array.Empty<string>(), null], null)!)
                {
                    var staging = (string)sessionType.GetMethod("CreateStaging", instance)!.Invoke(session, [source])!;
                    container = Path.GetDirectoryName(staging)!;
                    var journal = sessionType.GetProperty("Journal", instance)!.GetValue(session)!;
                    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                    var json = JsonSerializer.SerializeToNode(journal, journal.GetType(), options)!;
                    var planType = assembly.GetType("FolderRewind.History.Application.HistoryRestoreFileSystemPlanStore")!;
                    var known = planType.GetMethod("CaptureTree", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [container])!;
                    json["workspaces"]![0]!["knownObjects"] = JsonSerializer.SerializeToNode(known, known.GetType(), options);
                    Directory.CreateDirectory(Path.Combine(staging, "region"));
                    File.WriteAllText(Path.Combine(staging, "region", "r.0.0.mca"), "old unregistered output");
                    var transactionDirectory = Path.GetDirectoryName(Directory.GetFiles(history.Repository.Paths.TransactionsRoot, "restore-preparation.json", SearchOption.AllDirectories).Single())!;
                    File.WriteAllText(Path.Combine(transactionDirectory, "restore-journal.json"), json.ToJsonString(options));
                    File.Delete(Path.Combine(transactionDirectory, "restore-preparation.json"));
                    if (scenario == "legacy-unproven") File.WriteAllText(Path.Combine(container, "owner.json"), "unproven owner");
                    sessionType.GetMethod("Finish", instance)!.Invoke(session, null);
                }
            }
            // Exercise the production preflight, capture, and commit rather than just its admission gate.
            var backup = await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal());
            Require(backup.CreatedNewArchive, "BackupService remained blocked after preparation recovery: " + scenario);
            await (Task)typeof(HistoryRuntime).GetMethod("DrainRestoreCleanupAsync", instance)!.Invoke(history, [CancellationToken.None])!;
            Require(digest == (await MergeTreeManifest.ReadAsync(folder.Path, _ => true, default)).Digest,
                "Recovery or the later backup changed the current world.");
            if (container is not null)
                Require(Directory.Exists(container) == (scenario == "legacy-unproven"), "Unexpected preparation preservation policy.");
            Require(!history.Issues.Any(issue => issue.BlocksMutation), "Preparation left a blocking runtime issue.");
            Results.Add(new { scenario, realBackupCommitted = true, currentWorldUnchanged = true,
                preservedAsMaintenance = scenario == "legacy-unproven", history.MaintenanceDiagnostic });
            await File.WriteAllTextAsync(Path.Combine(Root, "host-restore-cleanup.json"), JsonSerializer.Serialize(new { results = Results }, new JsonSerializerOptions { WriteIndented = true }));
            await NativeHistoryCoreGateway.DetachActiveConfigAsync(config.Id);
        }
        await LogService.FlushAsync();
    }

    private sealed class FailedHostMaterializer(IRepresentationHandler inner) : IRepresentationHandler
    {
        internal bool Materialized;
        public bool CanHandle(VersionRepresentation representation) => inner.CanHandle(representation);
        public ValueTask<RepresentationAssessment> AssessAsync(RepresentationAssessmentContext context, CancellationToken token)
            => inner.AssessAsync(context, token);
        public async ValueTask MaterializeAsync(RepresentationMaterializationContext context, CancellationToken token)
        {
            await inner.MaterializeAsync(context, token);
            Require(Directory.EnumerateFileSystemEntries(context.StagingDirectory).Any(), "Real materializer produced no output.");
            Materialized = true;
            throw new IOException("Injected failure after real archive materialization.");
        }
    }

    internal static async Task RunAsync()
    {
        foreach (var mode in new[] { BackupMode.Full, BackupMode.Smart, BackupMode.Rolling })
        foreach (var grade in new[] { BackupArchiveVerificationLevel.Balanced, BackupArchiveVerificationLevel.Strict })
        foreach (var format in new[] { "7z", "zip" })
            await VerifyArchiveListingAttributesAsync(mode, grade, format);
        foreach (var mode in new[] { BackupMode.Full, BackupMode.Smart, BackupMode.Rolling })
        foreach (var grade in new[] { BackupArchiveVerificationLevel.Balanced, BackupArchiveVerificationLevel.Strict })
        foreach (bool missingCache in new[] { false, true })
            await VerifyOrdinaryProtectionAsync(mode, grade, missingCache);
        foreach (var scenario in new[] { "equal", "equal-same-version", "different", "unknown" }) await VerifyCheckoutAsync(scenario);
        await VerifyExcludedLinkAndPreserveAsync();
        await VerifyOrdinaryProtectionAsync(BackupMode.Full, BackupArchiveVerificationLevel.Balanced, false, redirectedRoot: true);
        await VerifyBoundSnapshotFiltersAsync();
        await VerifySmartChainTargetAsync();
        await VerifyBatchAsync();
        await VerifyMetadataOperationFenceAsync();
        await File.WriteAllTextAsync(Path.Combine(Root, "host-stage2.json"), JsonSerializer.Serialize(new
        { source = "real BackupService / Restore / Checkout", ui = "not executed", results = Results }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task VerifyMetadataOperationFenceAsync()
    {
        var (config, folder) = await CreateAsync("metadata-operation-fence", BackupMode.Smart, BackupArchiveVerificationLevel.Strict);
        var history = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var initialPackCount = (await history.Repository.ReadAllPacksAsync()).Count;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var gate = typeof(BackupService).Assembly.GetType("FolderRewind.Services.NativeHistoryConfigurationOperationGate")!;
        var acquire = gate.GetMethod("EnterAsync", flags)!.Invoke(null, [config.Id, CancellationToken.None])!;
        var acquireTask = (Task)acquire.GetType().GetMethod("AsTask")!.Invoke(acquire, null)!;
        await acquireTask;
        var lease = (IAsyncDisposable)acquireTask.GetType().GetProperty("Result")!.GetValue(acquireTask)!;
        var pending = FolderRewind.History.Storage.HistoryTransactionJournal.Prepared(HistoryTransactionId.New(), PackId.New());
        var commit = typeof(NativeHistoryCoreGateway).GetMethod("CommitBackupInsideOperationAsync", flags)!;
        var arguments = commit.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
        arguments[0] = history; arguments[1] = lease;
        var source = new SourceId(Guid.Parse(folder.Id));
        arguments[2] = new HistoryConfigSnapshot(history.ConfigId,
            [new HistoryConfigSourceSnapshot(source, new(folder.DisplayName, folder.Path))]);
        arguments[3] = new[] { FolderRewind.History.Capture.SourceCaptureResult.NoChanges(source, CaptureScope.FullSource) };
        arguments[4] = BackupInvocationKind.Internal; arguments[5] = DateTimeOffset.UtcNow;
        try
        {
            history.Repository.Journals.Save(pending);
            bool blocked = false;
            try { await (Task)commit.Invoke(null, arguments)!; }
            catch (InvalidOperationException error) when (error.Message.Contains("incomplete history commit", StringComparison.Ordinal)) { blocked = true; }
            Require(blocked && (await history.Repository.ReadAllPacksAsync()).Count == initialPackCount,
                "Operation runtime reuse bypassed a newly pending journal before commit.");
            await history.Repository.Journals.RecoverAsync((_, _) => throw new InvalidOperationException("Absent pack must not commit."),
                (_, _) => Task.CompletedTask);
        }
        finally { await lease.DisposeAsync(); }
        bool releasedBlocked = false;
        try { await (Task)commit.Invoke(null, arguments)!; }
        catch (InvalidOperationException error) when (error.Message.Contains("active operation lease", StringComparison.Ordinal)) { releasedBlocked = true; }
        Require(releasedBlocked, "Released configuration operation lease was accepted.");
        var noChanges = await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal());
        Require(noChanges.IsSuccessful && !noChanges.CreatedNewArchive, "Unchanged source was not reused after recovery.");
        File.Move(Path.Combine(folder.Path, "keep.txt"), Path.Combine(folder.Path, "KEEP.txt"));
        Require((await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal())).CreatedNewArchive,
            "Case-only rename lost the existing Ordinal-baseline capture behavior.");
        await RecordAsync(new { scenario = "metadata-operation-fence", pendingJournalBlocked = true,
            releasedLeaseBlocked = true, noChangesPreserved = true, caseRenameCaptured = true });
        await NativeHistoryCoreGateway.DetachActiveConfigAsync(config.Id);
    }

    private static async Task VerifyArchiveListingAttributesAsync(BackupMode mode, BackupArchiveVerificationLevel grade, string format)
    {
        string name = $"archive-listing-{mode}-{grade}-{format}";
        var (config, folder) = await CreateAsync(name, mode, grade, archiveFormat: format, normalAttributes: true);
        var history = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var source = new SourceId(Guid.Parse(folder.Id));
        var originalArchives = (await history.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries
            .ToDictionary(entry => entry.Locator.AbsolutePath!, entry => System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(entry.Locator.AbsolutePath!)));
        string world = Path.Combine(folder.Path, "world.bin");
        Require(File.GetAttributes(world) == FileAttributes.Normal, "Initial input did not retain Normal attributes.");
        File.WriteAllBytes(world, Enumerable.Repeat((byte)7, 65537).ToArray());
        File.SetAttributes(world, FileAttributes.Normal);
        string added = Path.Combine(folder.Path, "中文目录", "新增.txt");
        File.WriteAllText(added, "新增的普通文件");
        File.SetAttributes(added, FileAttributes.Normal);

        var diagnostics = HistoryOperationDiagnostics.Begin("host-archive-listing");
        using (diagnostics)
            Require((await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal())).CreatedNewArchive,
                "Changed Normal-attribute files were not committed by the actual Host.");
        var statistics = HistoryOperationDiagnostics.GetRecent().Single(item => item.Operation == "backup"
            && item.StartedAtUtc >= diagnostics.Summary!.StartedAtUtc);
        var representations = await history.Query.GetAllRepresentationsAsync();
        var selected = (await history.WorkspaceStore.LoadAsync()).Value!.GetSourceState(source).BaseVersionId!.Value;
        var current = representations.Single(representation => representation.VersionId == selected);
        Require((await history.Query.GetAllCheckpointsAsync()).Count == 2, "Both captures must have committed checkpoints.");
        var expectedKind = mode switch
        {
            BackupMode.Smart => RepresentationKind.CoreSmartDelta,
            BackupMode.Rolling => RepresentationKind.CoreRolling,
            _ => RepresentationKind.CoreFull
        };
        Require(current.Kind == expectedKind,
            "The changed capture used an unexpected representation or fell back to Full.");
        Require(statistics.Counters.GetValueOrDefault("7z-u") == (mode == BackupMode.Rolling ? 1 : 0)
            && statistics.Counters.GetValueOrDefault("7z-a") == (mode == BackupMode.Rolling ? 0 : 1),
            "Rolling must update the staging archive; a Full fallback does not validate Rolling.");

        var backendType = typeof(BackupService).Assembly.GetType("FolderRewind.Services.SevenZipHistoryArchiveBackend")!;
        var backend = (IArchiveRepresentationBackend)Activator.CreateInstance(backendType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [config, true], null)!;
        var engine = new RepresentationRuntime([new CoreArchiveRepresentationHandler(backend), new SmartDeltaRepresentationHandler(backend)]);
        var export = Path.Combine(Root, name + "-export");
        await new HistoryVersionExportService(engine).ExportAsync(selected, representations,
            new RepresentationEnvironment((await history.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries, [], []),
            export, [config.DestinationPath, folder.Path]);
        Require((await MergeTreeManifest.ReadAsync(folder.Path, _ => true, default)).Digest
            == (await MergeTreeManifest.ReadAsync(export, _ => true, default)).Digest, "Exported content differs from the captured source.");
        foreach (var original in originalArchives)
            Require(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(original.Key)).AsSpan().SequenceEqual(original.Value),
                "The later capture or export modified an existing immutable archive.");
        await RecordAsync(new { scenario = "archive-listing-normal-attributes", mode, grade, format,
            current.Kind, statistics, initialAndChangedCaptureCommitted = true, exportMatchesSource = true, oldPayloadUnchanged = true });
        await NativeHistoryCoreGateway.DetachActiveConfigAsync(config.Id);
    }

    private static async Task VerifyOrdinaryProtectionAsync(BackupMode mode, BackupArchiveVerificationLevel grade, bool missingCache, bool redirectedRoot = false)
    {
        var (config, folder) = await CreateAsync($"ordinary-{mode}-{grade}-{missingCache}-{redirectedRoot}", mode, grade, redirectedRoot: redirectedRoot);
        var history = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var source = new SourceId(Guid.Parse(folder.Id));
        var baseline = (await history.WorkspaceStore.LoadAsync()).Value!.GetSourceState(source).BaseVersionId!.Value;
        var archivePaths = (await history.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries.Select(e => e.Locator.AbsolutePath!).ToArray();
        var originalArchives = archivePaths.ToDictionary(p => p, p => System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)));
        var path = Path.Combine(folder.Path, "world.bin"); var stamp = File.GetLastWriteTimeUtc(path);
        var changed = File.ReadAllBytes(path); changed[0] ^= 1; File.WriteAllBytes(path, changed); File.SetLastWriteTimeUtc(path, stamp);
        if (missingCache)
        {
            var cache = await history.CaptureBaselines.LoadAsync(source);
            if (cache is not null) await history.CaptureBaselines.RemoveAsync(source, cache.Revision);
        }
        config.Archive.BackupBeforeRestore = true; config.Archive.SkipIfUnchanged = true;
        await SaveAsync();
        var measured = await MeasureAsync(history, "ordinary", () => InvokeAsync<HistoryRestoreResult>("RestoreVersionAsync", config, folder,
            baseline, BackupService.RestoreMode.Clean, CancellationToken.None));
        Require(measured.Result.Succeeded, measured.Result.Diagnostic);
        Require(File.ReadAllBytes(path).All(b => b == 0), "Ordinary restore did not restore the original contents.");
        foreach (var original in originalArchives)
            Require(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(original.Key)).AsSpan().SequenceEqual(original.Value), "Protective capture changed an existing immutable payload.");
        if (redirectedRoot) Require(new DirectoryInfo(folder.Path).LinkTarget is not null, "Source root redirection entry was lost.");
        var snapshot = (await history.Query.GetSafetySnapshotsAsync()).Single();
        await AssertSnapshotAsync(config, history, snapshot, changed);
        var restoreStats = measured.Diagnostics.Single(d => d.Operation == "ordinary-restore");
        Require(restoreStats.Counters.GetValueOrDefault("target-source-materializations") == 1, "Target materialization must occur exactly once.");
        Require(restoreStats.Counters.GetValueOrDefault("logical-materializations") == 1, "A missing cache must not materialize the old baseline during a protective capture.");
        var backupStats = measured.Diagnostics.Single(d => d.Operation == "safety-snapshot");
        Require(backupStats.Counters.GetValueOrDefault("safety-full-captures") == 1, "Core protection must use full capture.");
        await RecordAsync(new { scenario = "ordinary-protection", mode, grade, missingCache, redirectedRoot, measured.FileReadyMs, measured.TotalMs,
            measured.Result.Status, measured.Diagnostics, capturedCurrentBytes = true });
        await NativeHistoryCoreGateway.DetachActiveConfigAsync(config.Id);
    }

    private static async Task VerifyCheckoutAsync(string scenario)
    {
        var (config, folder) = await CreateAsync("checkout-" + scenario, BackupMode.Full, BackupArchiveVerificationLevel.Balanced);
        var history = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var source = new SourceId(Guid.Parse(folder.Id));
        var first = (await history.Query.GetAllCheckpointsAsync()).Single();
        var branch = await history.Branches.CreateFromCheckpointAsync(first.CheckpointId, "target", sourceId: source);
        var path = Path.Combine(folder.Path, "world.bin");
        var active = File.ReadAllBytes(path); active[0] = 2; File.WriteAllBytes(path, active);
        Require((await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal())).CreatedNewArchive, "Second actual backup was not captured.");
        if (scenario == "equal-same-version")
            branch = await history.Branches.CreateFromCheckpointAsync((await history.Query.GetAllCheckpointsAsync()).OrderBy(c => c.CreatedAtUtc).Last().CheckpointId,
                "same-version", sourceId: source);
        if (scenario == "different")
        {
            var stamp = File.GetLastWriteTimeUtc(path); active[0] = 3;
            File.WriteAllBytes(path, active); File.SetLastWriteTimeUtc(path, stamp);
        }
        if (scenario == "unknown")
        {
            var state = (await history.WorkspaceStore.LoadAsync()).Value!;
            await history.WorkspaceStore.SaveAsync(state.WithSourceStates([state.GetSourceState(source) with { RequiresFullCapture = true }]), state.StateRevision);
        }
        var measured = await MeasureAsync(history, "checkout", () => InvokeAsync<HistoryRestoreResult>("CheckoutAsync", config, branch.BranchUpdate.UpdateId, CancellationToken.None));
        Require(measured.Result.Succeeded, measured.Result.Diagnostic);
        Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(scenario == "equal-same-version" ? active : new byte[65536]), "Checkout did not restore the selected branch.");
        var snapshots = await history.Query.GetSafetySnapshotsAsync();
        Require(snapshots.Count == (scenario.StartsWith("equal", StringComparison.Ordinal) ? 0 : 1), "Checkout protection was not conditional on reliable equality.");
        if (snapshots.Count > 0) await AssertSnapshotAsync(config, history, snapshots.Single(), active);
        var stats = measured.Diagnostics.Single(d => d.Operation == "host-stage2-checkout");
        Require(stats.Counters.GetValueOrDefault("baseline-comparisons") <= 1, "Checkout compared the previous baseline more than once.");
        Require(stats.Counters.GetValueOrDefault("target-source-materializations") == 1, "Checkout target was prepared more than once.");
        Require(stats.Counters.GetValueOrDefault("restore-patch-calls") == 0, "Checkout must not apply ordinary restore patches.");
        if (scenario == "equal-same-version") Require(stats.Counters.GetValueOrDefault("logical-materializations") == 1
            && stats.Counters.GetValueOrDefault("file-moves") == 0, "Same-version Checkout must reuse preparation and avoid replacing equal files.");
        await RecordAsync(new { scenario = "checkout-" + scenario, measured.FileReadyMs, measured.TotalMs, measured.Result.Status, measured.Diagnostics,
            snapshotCount = snapshots.Count, branchCorrect = (await history.WorkspaceStore.LoadAsync()).Value!.GetSourceState(source).ActiveBranchId == branch.BranchUpdate.BranchId });
        await NativeHistoryCoreGateway.DetachActiveConfigAsync(config.Id);
    }

    private static async Task VerifyExcludedLinkAndPreserveAsync()
    {
        var (config, folder) = await CreateAsync("excluded-link", BackupMode.Full, BackupArchiveVerificationLevel.Balanced, excludeCache: true);
        var history = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var source = new SourceId(Guid.Parse(folder.Id));
        var baseline = (await history.WorkspaceStore.LoadAsync()).Value!.GetSourceState(source).BaseVersionId!.Value;
        var outside = Path.Combine(Root, "outside-excluded"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "foreign"), "outside contents");
        Directory.CreateSymbolicLink(Path.Combine(folder.Path, "cache"), outside);
        File.WriteAllText(Path.Combine(folder.Path, "missing-from-backup.txt"), "current player contents");
        File.WriteAllText(Path.Combine(folder.Path, "keep.txt"), "current changed existing file");
        config.Filters.RestoreWhitelist.Add("missing-from-backup.txt"); config.Archive.BackupBeforeRestore = false;
        await SaveAsync();
        var measured = await MeasureAsync(history, "preserve", () => InvokeAsync<HistoryRestoreResult>("RestoreVersionAsync", config, folder,
            baseline, BackupService.RestoreMode.Clean, CancellationToken.None));
        Require(measured.Result.Succeeded, measured.Result.Diagnostic);
        Require(File.ReadAllText(Path.Combine(folder.Path, "missing-from-backup.txt")) == "current player contents", "Whitelist patch was lost.");
        Require(File.ReadAllText(Path.Combine(folder.Path, "keep.txt")) == "original", "Whitelist must not mask an existing archive file.");
        Require(File.ReadAllText(Path.Combine(outside, "foreign")) == "outside contents" && new DirectoryInfo(Path.Combine(folder.Path, "cache")).LinkTarget is not null,
            "Excluded link entry or outside content was modified.");
        var stats = measured.Diagnostics.Single(d => d.Operation == "ordinary-restore");
        Require(stats.Counters.GetValueOrDefault("restore-patch-calls") == 1, "Host preparation must run once.");
        Require((await history.WorkspaceStore.LoadAsync()).Value!.GetSourceState(source).Relation == FolderRewind.History.LocalState.WorkspaceBaselineRelation.Derived,
            "Preserve patch must retain Derived semantics.");
        await RecordAsync(new { scenario = "plugin-locked-view-excluded-link-whitelist", measured.FileReadyMs, measured.TotalMs, measured.Diagnostics, outsideIntact = true });
        File.WriteAllText(Path.Combine(folder.Path, "keep.txt"), "explicitly preserved");
        config.Filters.RestoreWhitelist.Clear(); await SaveAsync();
        var preserved = await MeasureAsync(history, "preserve", () => InvokeAsync<HistoryRestoreResult>("RestoreVersionAsync", config, folder,
            baseline, BackupService.RestoreMode.Clean, CancellationToken.None, new RestoreRequestOptions { RestorePreservePaths = ["keep.txt"] }));
        Require(preserved.Result.Succeeded && File.ReadAllText(Path.Combine(folder.Path, "keep.txt")) == "explicitly preserved", preserved.Result.Diagnostic);
        Require(!File.Exists(Path.Combine(folder.Path, "missing-from-backup.txt")), "A previous whitelist must not leak into a later operation.");
        await RecordAsync(new { scenario = "plugin-locked-view-explicit-preserve", preserved.FileReadyMs, preserved.TotalMs, preserved.Diagnostics });
        await NativeHistoryCoreGateway.DetachActiveConfigAsync(config.Id);
    }

    private static async Task VerifyBatchAsync()
    {
        var (config, first) = await CreateAsync("batch", BackupMode.Full, BackupArchiveVerificationLevel.Balanced);
        var secondPath = Path.Combine(Root, "batch", "second"); Directory.CreateDirectory(secondPath);
        File.WriteAllBytes(Path.Combine(secondPath, "world.bin"), new byte[65536]);
        var second = new ManagedFolder { Path = secondPath, DisplayName = "second" }; config.SourceFolders.Add(second); await SaveAsync();
        Require((await BackupService.BackupConfigAsync(config, BackupInvocationOptions.ForInternal())).CreatedNewArchive, "Actual multi-Source backup failed.");
        var history = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var run = (await history.Query.GetLatestRunAsync())!;
        var changed = new byte[65536]; changed[0] = 5;
        foreach (var folder in new[] { first, second })
        {
            var path = Path.Combine(folder.Path, "world.bin"); var stamp = File.GetLastWriteTimeUtc(path);
            File.WriteAllBytes(path, changed); File.SetLastWriteTimeUtc(path, stamp);
        }
        config.Archive.BackupBeforeRestore = true; config.Archive.SkipIfUnchanged = true; await SaveAsync();
        var measured = await MeasureAsync(history, "batch", () => InvokeAsync<HistoryRestoreResult>("RestoreRunAsync", config, run.RunId,
            new[] { new SourceId(Guid.Parse(first.Id)), new SourceId(Guid.Parse(second.Id)) }, BackupService.RestoreMode.Clean, CancellationToken.None));
        Require(measured.Result.Succeeded && measured.Result.AppliedSources.Count == 2, measured.Result.Diagnostic);
        foreach (var folder in new[] { first, second }) Require(File.ReadAllBytes(Path.Combine(folder.Path, "world.bin")).All(b => b == 0), "Batch output mismatch.");
        var snapshot = (await history.Query.GetSafetySnapshotsAsync()).Single();
        Require(snapshot.CheckpointIds.Length == 2, "A multi-Source safety capture must protect both Sources.");
        await AssertSnapshotAsync(config, history, snapshot, changed);
        Require(measured.Diagnostics.Single(d => d.Operation == "host-stage2-batch").Counters.GetValueOrDefault("target-source-materializations") == 2, "Batch target materializations are duplicated.");
        await RecordAsync(new { scenario = "multi-source-real-snapshot", measured.FileReadyMs, measured.TotalMs, measured.Diagnostics, bothSourcesProtected = true });
        await NativeHistoryCoreGateway.DetachActiveConfigAsync(config.Id);
    }

    private static async Task VerifyBoundSnapshotFiltersAsync()
    {
        var (config, folder) = await CreateAsync("absolute-filter-snapshot", BackupMode.Full, BackupArchiveVerificationLevel.Strict,
            redirectedRoot: true, absoluteExclusion: true);
        var history = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var baseline = (await history.WorkspaceStore.LoadAsync()).Value!.GetSourceState(new SourceId(Guid.Parse(folder.Id))).BaseVersionId!.Value;
        var outside = Path.Combine(Root, "absolute-filter-outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "foreign"), "must remain");
        Directory.CreateSymbolicLink(Path.Combine(folder.Path, "cache"), outside);
        var changed = File.ReadAllBytes(Path.Combine(folder.Path, "world.bin")); changed[0] = 8;
        File.WriteAllBytes(Path.Combine(folder.Path, "world.bin"), changed);
        config.Archive.BackupBeforeRestore = true; await SaveAsync();
        var measured = await MeasureAsync(history, "absolute-filter", () => InvokeAsync<HistoryRestoreResult>("RestoreVersionAsync", config, folder,
            baseline, BackupService.RestoreMode.Clean, CancellationToken.None));
        Require(measured.Result.Succeeded, measured.Result.Diagnostic);
        Require(File.ReadAllText(Path.Combine(outside, "foreign")) == "must remain"
            && new DirectoryInfo(Path.Combine(folder.Path, "cache")).LinkTarget is not null, "Physical capture lost logical absolute-filter semantics.");
        await AssertSnapshotAsync(config, history, (await history.Query.GetSafetySnapshotsAsync()).Single(), changed);
        await RecordAsync(new { scenario = "physical-snapshot-logical-absolute-filter", measured.FileReadyMs, measured.TotalMs, measured.Diagnostics });
        await NativeHistoryCoreGateway.DetachActiveConfigAsync(config.Id);
    }

    private static async Task VerifySmartChainTargetAsync()
    {
        var (config, folder) = await CreateAsync("smart-chain-target", BackupMode.Smart, BackupArchiveVerificationLevel.Strict);
        config.Archive.MaxSmartBackupsPerFull = 8; await SaveAsync();
        var path = Path.Combine(folder.Path, "world.bin");
        for (byte layer = 1; layer <= 5; layer++)
        {
            var contents = new byte[65536]; contents[0] = layer; File.WriteAllBytes(path, contents);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(layer));
            Require((await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal())).CreatedNewArchive,
                "Actual Smart chain layer was not captured.");
        }
        var history = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var source = new SourceId(Guid.Parse(folder.Id));
        var selected = (await history.WorkspaceStore.LoadAsync()).Value!.GetSourceState(source).BaseVersionId!.Value;
        var representations = await history.Query.GetAllRepresentationsAsync();
        Require(representations.Count(r => r.Kind == RepresentationKind.CoreSmartDelta) == 5, "The target must be a real six-layer Smart chain.");
        var archives = (await history.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries
            .ToDictionary(e => e.Locator.AbsolutePath!, e => System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(e.Locator.AbsolutePath!)));
        var changed = new byte[65536]; changed[0] = 9; File.WriteAllBytes(path, changed);
        config.Archive.BackupBeforeRestore = true; await SaveAsync();
        var measured = await MeasureAsync(history, "smart-chain", () => InvokeAsync<HistoryRestoreResult>("RestoreVersionAsync", config, folder,
            selected, BackupService.RestoreMode.Clean, CancellationToken.None));
        Require(measured.Result.Succeeded && File.ReadAllBytes(path)[0] == 5, measured.Result.Diagnostic);
        var stats = measured.Diagnostics.Single(d => d.Operation == "ordinary-restore");
        Require(stats.Counters.GetValueOrDefault("logical-materializations") == 1
            && stats.Counters.GetValueOrDefault("target-source-materializations") == 1, "Smart target must materialize logically once.");
        Require(stats.Counters.GetValueOrDefault("7z-x") >= 2, "Smart owner extracts must remain distinct from logical materialization count.");
        foreach (var archive in archives)
            Require(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archive.Key)).AsSpan().SequenceEqual(archive.Value),
                "Actual Smart capture or protection changed an older payload.");
        await AssertSnapshotAsync(config, history, (await history.Query.GetSafetySnapshotsAsync()).Single(), changed);
        await RecordAsync(new { scenario = "real-six-layer-smart-target", measured.FileReadyMs, measured.TotalMs, measured.Diagnostics });
        await NativeHistoryCoreGateway.DetachActiveConfigAsync(config.Id);
    }

    private static async Task<(BackupConfig Config, ManagedFolder Folder)> CreateAsync(string name, BackupMode mode, BackupArchiveVerificationLevel grade,
        bool excludeCache = false, bool redirectedRoot = false, bool absoluteExclusion = false,
        string? archiveFormat = null, bool normalAttributes = false)
    {
        var directory = Path.Combine(Root, name); var source = Path.Combine(directory, "world");
        if (redirectedRoot)
        {
            var physical = Path.Combine(directory, "physical-world"); Directory.CreateDirectory(physical); Directory.CreateSymbolicLink(source, physical);
        }
        else Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "world.bin"), new byte[65536]); File.WriteAllText(Path.Combine(source, "keep.txt"), "original");
        if (normalAttributes)
        {
            File.SetAttributes(Path.Combine(source, "world.bin"), FileAttributes.Normal);
            Directory.CreateDirectory(Path.Combine(source, "中文目录"));
            string normal = Path.Combine(source, "中文目录", "普通文件.txt");
            File.WriteAllText(normal, "正常的初始内容"); File.SetAttributes(normal, FileAttributes.Normal);
            File.SetAttributes(Path.Combine(source, "keep.txt"), FileAttributes.Archive);
        }
        var config = new BackupConfig { Name = name, DestinationPath = Path.Combine(directory, "backups") };
        var folder = new ManagedFolder { Path = source, DisplayName = "world" }; config.SourceFolders.Add(folder);
        config.Archive.Mode = mode; config.Archive.SkipIfUnchanged = false; config.Archive.BackupVerificationLevel = grade;
        if (archiveFormat is not null)
        {
            config.Archive.Format = archiveFormat;
            config.Archive.Method = archiveFormat == "zip" ? "Deflate" : "LZMA2";
        }
        config.Archive.RestorePreflightMode = grade == BackupArchiveVerificationLevel.Strict ? RestoreArchivePreflightMode.Strict : RestoreArchivePreflightMode.Balanced;
        if (excludeCache) config.Filters.Blacklist.Add("cache");
        if (absoluteExclusion) config.Filters.Blacklist.Add(Path.Combine(source, "cache"));
        ConfigService.CurrentConfig.BackupConfigs.Add(config); await SaveAsync();
        Require((await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal())).CreatedNewArchive, "Initial real Host backup failed.");
        return (config, folder);
    }

    private sealed record Measurement(HistoryRestoreResult Result, double FileReadyMs, double TotalMs, IReadOnlyList<HistoryOperationDiagnostic> Diagnostics);
    private static async Task<Measurement> MeasureAsync(HistoryRuntime history, string scenario, Func<Task<HistoryRestoreResult>> operation)
    {
        var start = DateTimeOffset.UtcNow; var clock = Stopwatch.StartNew();
        HistoryRestoreResult result; double fileReady;
        using (HistoryOperationDiagnostics.Begin("host-stage2-" + scenario))
        {
            result = await operation(); fileReady = clock.Elapsed.TotalMilliseconds;
            var drain = history.GetType().GetMethod("DrainRestoreCleanupAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            if (drain is not null) await (Task)drain.Invoke(history, [CancellationToken.None])!;
        }
        return new(result, fileReady, clock.Elapsed.TotalMilliseconds, HistoryOperationDiagnostics.GetRecent().Where(d => d.StartedAtUtc >= start).ToArray());
    }

    private static async Task AssertSnapshotAsync(BackupConfig config, HistoryRuntime history, SafetySnapshot snapshot, byte[] bytes)
    {
        var graph = await history.Query.GetAllRepresentationsAsync();
        var backendType = typeof(BackupService).Assembly.GetType("FolderRewind.Services.SevenZipHistoryArchiveBackend")!;
        var backend = (IArchiveRepresentationBackend)Activator.CreateInstance(backendType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [config, true], null)!;
        var engine = new RepresentationRuntime([new CoreArchiveRepresentationHandler(backend), new SmartDeltaRepresentationHandler(backend)]);
        foreach (var id in snapshot.CheckpointIds)
        {
            var checkpoint = await history.Query.GetCheckpointAsync(id);
            Require(graph.Where(r => r.VersionId == checkpoint!.VersionId).All(r => r.Kind == RepresentationKind.CoreFull), "Protective Core capture must be self-contained Full.");
            var planner = typeof(BackupService).Assembly.GetType("FolderRewind.History.Application.HistoryRestoreWorkspacePlanner")!;
            var physical = (string)planner.GetMethod("ResolvePath", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [Root])!;
            var export = Path.Combine(physical, "snapshot-export-" + Guid.NewGuid().ToString("N"));
            await new HistoryVersionExportService(engine).ExportAsync(checkpoint!.VersionId, graph,
                new RepresentationEnvironment((await history.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries, [], []), export, [config.DestinationPath]);
            Require(File.ReadAllBytes(Path.Combine(export, "world.bin")).AsSpan().SequenceEqual(bytes), "SafetySnapshot did not capture actual current bytes with unchanged size/time.");
        }
    }

    private static async Task<T> InvokeAsync<T>(string name, params object?[] arguments)
    {
        var method = Native.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        var values = method.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
        Array.Copy(arguments, values, arguments.Length);
        return await (Task<T>)method.Invoke(null, values)!;
    }
    private static async Task SaveAsync() => Require((await ConfigService.SaveAsync()).Success, "Isolated configuration save failed.");
    private static async Task RecordAsync(object result)
    {
        Results.Add(result);
        await File.WriteAllTextAsync(Path.Combine(Root, "host-stage2.json"), JsonSerializer.Serialize(new { results = Results }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void Require(bool condition, string diagnostic) { if (!condition) throw new InvalidOperationException(diagnostic); }
}

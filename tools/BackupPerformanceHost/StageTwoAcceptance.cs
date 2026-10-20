using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
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
        await File.WriteAllTextAsync(Path.Combine(Root, "host-stage2.json"), JsonSerializer.Serialize(new
        { source = "real BackupService / Restore / Checkout", ui = "not executed", results = Results }, new JsonSerializerOptions { WriteIndented = true }));
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

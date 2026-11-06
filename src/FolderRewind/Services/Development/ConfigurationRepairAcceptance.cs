#if CONFIGURATION_REPAIR_ACCEPTANCE
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.ViewModels;
using FolderRewind.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Development;

// Runs inside the actual WinUI Host. Only the final persistence boundary can be delayed/failed.
// There is no substitute model, serializer, snapshot coordinator, writer, or interaction service.
internal static class ConfigurationRepairAcceptance
{
    private static string? _root;
    private static Func<ReadOnlyMemory<byte>, Task>? _beforeWrite;
    private static int _writes, _successfulWrites, _activeWrites, _maxWriters, _saved, _savedOffUi;
    private static readonly ConcurrentQueue<object> Writes = new();
    private static readonly List<object> Results = [];

    internal static void Prepare()
    {
        if (Environment.GetEnvironmentVariable("FOLDERREWIND_CONFIGURATION_REPAIR") != "1") return;
        var data = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_DATA_ROOT");
        if (!AppRuntimeInfo.IsMsiDistribution || string.IsNullOrWhiteSpace(data) || !Path.IsPathFullyQualified(data))
            throw new InvalidOperationException("Configuration acceptance requires an isolated MSI data root.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(data));
        var root = Directory.GetParent(full)?.FullName;
        if (root is null || Path.GetFileName(full) != "appdata" || !File.Exists(Path.Combine(root, "acceptance-owner"))
            || ConfigService.CurrentConfig.BackupConfigs.Count != 0)
            throw new InvalidOperationException("Configuration acceptance requires a fresh marked data root.");
        _root = root;
        ConfigService.CurrentConfig.GlobalSettings.Plugins.AutoCheckUpdates = false;
        ConfigService.CurrentConfig.GlobalSettings.EnableNotifications = false;
        ConfigService.CurrentConfig.GlobalSettings.ToastNotificationLevel = 0;
        ConfigService.CurrentConfig.GlobalSettings.HasShownFirstLaunchGuide = true;
        // This suite exercises configuration persistence, not the independent two-second
        // first-project validator. Its automatic success toast would pollute command evidence.
        ConfigService.CurrentConfig.GlobalSettings.HasTriggeredInitialCoreValidation = true;
        ConfigService.CurrentConfig.GlobalSettings.EnableNotices = false;
        ConfigService.CurrentConfig.GlobalSettings.EnableUpdateReminder = false;
    }

    internal static void Start(Window window)
    {
        if (_root is null) return;
        window.DispatcherQueue.TryEnqueue(() => TaskObserver.Observe(RunAsync(window), nameof(ConfigurationRepairAcceptance)));
    }

    internal static async Task WriteSnapshotAsync(ReadOnlyMemory<byte> payload, CancellationToken token, Func<Task> write)
    {
        if (_root is null) { await write().ConfigureAwait(false); return; }
        var ordinal = Interlocked.Increment(ref _writes);
        var active = Interlocked.Increment(ref _activeWrites);
        int observed;
        do { observed = Volatile.Read(ref _maxWriters); }
        while (active > observed && Interlocked.CompareExchange(ref _maxWriters, active, observed) != observed);
        var hash = Convert.ToHexString(SHA256.HashData(payload.Span));
        try
        {
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _beforeWrite) is { } before) await before(payload).ConfigureAwait(false);
            await write().ConfigureAwait(false);
            Interlocked.Increment(ref _successfulWrites);
            Writes.Enqueue(new { ordinal, hash, success = true });
        }
        catch (Exception error)
        {
            Writes.Enqueue(new { ordinal, hash, success = false, error = error.Message });
            throw;
        }
        finally { Interlocked.Decrement(ref _activeWrites); }
    }

    private static async Task RunAsync(Window window)
    {
        void Saved()
        {
            if (!window.DispatcherQueue.HasThreadAccess) Interlocked.Increment(ref _savedOffUi);
            _saved++;
        }
        try
        {
            // Startup has no fixture configurations to warm; let its initial UI saves finish first.
            await Task.Delay(2000);
            ConfigService.Saved += Saved;
            await BoundaryCompensationAsync();
            await ConcurrentEditsAsync();
            await SourceIdentitiesAsync();
            await QueuedSnapshotsAsync();
            await CompensationFailureAsync();
            await FailureNotificationsAsync(window);
            await HistoryCommandAsync(window, leavePage: false, ordinal: 1);
            await HistoryCommandAsync(window, leavePage: true, ordinal: 2);
            await DrainAsync();
            Require(_savedOffUi == 0, "Saved must be published on the actual UI owner.");
            await WriteAsync("configuration-repair-results.json", new { status = "PASS", groups = 7,
                scope = "Actual isolated WinUI Host, real configuration models/JSON and both snapshot queues; IO-boundary failures only. Metadata-only history fixture, no successful checkout or archive throughput claim.",
                results = Results, writes = Writes.ToArray() });
        }
        catch (Exception error)
        {
            _beforeWrite = null;
            await WriteAsync("configuration-repair-results.json", new { status = "FAIL", error = error.ToString(), results = Results,
                writes = Writes.ToArray() });
        }
        finally { _beforeWrite = null; ConfigService.Saved -= Saved; }
    }

    private static async Task<(BackupConfig Config, ManagedFolder Folder, BackupConfig Other)> ResetAsync(string name)
    {
        _beforeWrite = null;
        await DrainAsync();
        var root = Path.Combine(_root!, name);
        Directory.CreateDirectory(root);
        var folder = new ManagedFolder { Path = Path.Combine(root, "source"), DisplayName = "Repair source",
            SourceScope = new() { Mode = BackupSourceScopeMode.Include, IncludePatterns = ["saves/**"] } };
        Directory.CreateDirectory(folder.Path);
        var config = new BackupConfig { Name = name, DestinationPath = Path.Combine(root, "backup") };
        config.SourceFolders.Add(folder);
        config.Filters.RestoreWhitelist.Add(new("keep", PathFilterTarget.Directory, 2));
        var other = new BackupConfig { Name = name + " other", DestinationPath = Path.Combine(root, "other-backup") };
        other.SourceFolders.Add(new() { Path = Path.Combine(root, "other-source"), DisplayName = "Other source" });
        ConfigService.CurrentConfig.BackupConfigs.Clear();
        ConfigService.CurrentConfig.BackupConfigs.Add(config);
        ConfigService.CurrentConfig.BackupConfigs.Add(other);
        ConfigService.CurrentConfig.GlobalSettings.EnableNotifications = false;
        Require((await ConfigService.SaveAsync(publishSavedEvent: false)).Success, "Fixture baseline must persist.");
        await DrainAsync();
        _writes = _successfulWrites = _maxWriters = _saved = 0;
        return (config, folder, other);
    }

    private static HistorySourceBoundaryMismatch Mismatch(ManagedFolder folder)
        => new(new(Guid.Parse(folder.Id)), EffectiveSourceBoundarySnapshot.CreateTyped(EffectiveBoundaryScopeMode.Include,
            ["region\\**", "REGION/**"], EffectiveBoundaryFilterMode.Blacklist,
            [new PathFilterRule(@" ^cache\\[A-Z]+$ ", PathFilterTarget.Any, 1)], true), EffectiveSourceBoundarySnapshot.All);

    private static Task<HistoryConfigurationRepairResult> RepairAsync(BackupConfig config, ManagedFolder folder, CancellationToken token = default)
        => HistorySourceBindingRepairService.RestoreHistoricalBoundaryAsync(config, Mismatch(folder), config.ConfigRevision, true, token);

    private static async Task BoundaryCompensationAsync()
    {
        var (config, folder, _) = await ResetAsync("normalized-compensation");
        var scope = folder.SourceScope; var filters = config.Filters; var revision = config.ConfigRevision;
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { await RepairAsync(config, folder, canceled.Token); throw new InvalidOperationException("Expected cancellation before apply."); }
            catch (OperationCanceledException) { }
            Require(_writes == 0 && ReferenceEquals(folder.SourceScope, scope) && config.ConfigRevision == revision,
                "Cancellation before apply must not mutate or enqueue a snapshot.");
        }
        using var barrier = new WriteBarrier();
        var entered = barrier.Entered; var release = barrier.Release; var calls = 0;
        _beforeWrite = async _ =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            entered.TrySetResult(); await release.Task;
            throw new IOException("acceptance.initial-write-failed");
        };
        var operation = RepairAsync(config, folder);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await using var pendingGate = new PendingLease(config.Id);
        Require(!pendingGate.Task.IsCompleted, "The config operation gate was released before persistence completed.");
        release.TrySetResult();
        var result = await operation.WaitAsync(TimeSpan.FromSeconds(30));
        await pendingGate.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await pendingGate.DisposeAsync();
        await DrainAsync();
        Require(result.Status == HistoryConfigurationRepairStatus.SaveFailed, "Compensation success must not report repair success.");
        Require(ReferenceEquals(folder.SourceScope, scope) && ReferenceEquals(config.Filters, filters)
            && folder.SourceScope.IncludePatterns.SequenceEqual(new[] { "saves/**" }) && config.ConfigRevision == revision,
            "Normalized candidate was mistaken for a concurrent edit.");
        Require(Disk(config.Id).SourceFolders.Single().SourceScope.IncludePatterns.SequenceEqual(new[] { "saves/**" }), "Compensation did not reach disk.");
        Require(_writes == 2 && _successfulWrites == 1 && _saved == 1 && _maxWriters == 1, "Failed/compensating write event accounting is wrong.");
        _beforeWrite = null;
        Require((await RepairAsync(config, folder)).Succeeded, "The next ordinary retry must succeed.");
        var persisted = Disk(config.Id);
        Require(persisted.SourceFolders.Single().SourceScope.IncludePatterns.SequenceEqual(new[] { "REGION/**" })
            && persisted.Filters.UseRegex && persisted.Filters.Blacklist.Single() == Mismatch(folder).HistoricalBoundary.Rules.Single(),
            "Typed/regex filter fields must retain their exact persisted spelling and identity.");
        Results.Add(new { group = "normalized-failure-and-retry", status = "PASS" });
    }

    private static async Task ConcurrentEditsAsync()
    {
        var (config, folder, other) = await ResetAsync("concurrent-edits");
        using var barrier = new WriteBarrier();
        var entered = barrier.Entered; var release = barrier.Release; var calls = 0;
        _beforeWrite = async _ =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            entered.TrySetResult(); await release.Task; throw new IOException("acceptance.concurrent-write-failed");
        };
        var operation = RepairAsync(config, folder);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        folder.SourceScope.IncludePatterns = [" mods\\** "];
        var added = new PathFilterRule("later/**", PathFilterTarget.Directory, 2);
        config.Filters.Blacklist.Add(added);
        config.Filters.RestoreWhitelist.Add(new("later-keep", PathFilterTarget.File, 2));
        config.Filters.SchemaExtensions["concurrent"] = JsonSerializer.SerializeToElement(7);
        config.ConfigRevision = "concurrent-revision";
        config.DestinationPath += "-edited";
        other.Name = "Another editor";
        release.TrySetResult();
        Require((await operation.WaitAsync(TimeSpan.FromSeconds(30))).Status == HistoryConfigurationRepairStatus.SaveFailed, "Concurrent failure must be reported.");
        await DrainAsync();
        var disk = Disk(config.Id);
        Require(folder.SourceScope.IncludePatterns.SequenceEqual(new[] { "mods/**" })
            && disk.SourceFolders.Single().SourceScope.IncludePatterns.SequenceEqual(new[] { "mods/**" })
            && disk.Filters.Blacklist.Contains(added) && disk.Filters.RestoreWhitelist.Count == 2
            && disk.Filters.SchemaExtensions["concurrent"].GetInt32() == 7 && disk.ConfigRevision == "concurrent-revision"
            && disk.DestinationPath.EndsWith("-edited", StringComparison.Ordinal) && Disk(other.Id).Name == "Another editor",
            "Field-level compensation overwrote a concurrent edit.");
        Results.Add(new { group = "concurrent-field-compensation", status = "PASS" });
    }

    private static async Task SourceIdentitiesAsync()
    {
        var (config, folder, other) = await ResetAsync("source-identities");
        var otherFolder = other.SourceFolders.Single();
        var otherId = otherFolder.Id; var original = folder.Id;
        var missing = new MissingHistoricalSource(new(Guid.Parse(otherId)), new("Historical", folder.Path), folder.Path);
        var conflict = await HistorySourceBindingRepairService.RestoreMissingBindingAsync(config, missing,
            Path.Combine(_root!, "missing-source"), config.ConfigRevision);
        Require(conflict.Status == HistoryConfigurationRepairStatus.Conflict && _writes == 0, "Cross-config repair conflict must be rejected before writing.");
        foreach (var invalid in new[] { "", "invalid", Guid.Empty.ToString() })
        {
            folder.Id = invalid;
            var rejected = await ConfigService.SaveAsync();
            Require(!rejected.Success && folder.Id == invalid && _writes == 0, "Ordinary save changed or persisted an invalid identity.");
        }
        folder.Id = original;
        // The mutation occurs after SaveAsync requests a capture but before the UI capture runs.
        var beforeCapture = ConfigService.SaveAsync();
        otherFolder.Id = Guid.Parse(original).ToString("N").ToUpperInvariant();
        Require(!(await beforeCapture).Success && _writes == 0 && otherFolder.Id != otherId, "A pre-capture identity collision entered the writer.");
        otherFolder.Id = otherId;
        using var barrier = new WriteBarrier();
        var entered = barrier.Entered; var release = barrier.Release;
        _beforeWrite = async _ => { entered.TrySetResult(); await release.Task; };
        var accepted = ConfigService.SaveAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        otherFolder.Id = original;
        Require(!(await ConfigService.SaveAsync().WaitAsync(TimeSpan.FromSeconds(10))).Success && _writes == 1, "A later bad capture must not enqueue or replace an accepted snapshot.");
        release.TrySetResult();
        Require((await accepted).Success, "A valid detached snapshot was invalidated by a later edit.");
        await DrainAsync();
        Require(Disk(other.Id).SourceFolders.Single().Id == otherId && Disk(config.Id).SourceFolders.Single().Id == original
            && otherFolder.Id == original, "Snapshot isolation or identity preservation failed.");
        otherFolder.Id = otherId; _beforeWrite = null;
        Require((await ConfigService.SaveAsync(false)).Success, "Restored identities must be writable.");

        // A different configuration edits after the missing-binding precheck but before capture.
        var historical = SourceId.New();
        var missingOperation = HistorySourceBindingRepairService.RestoreMissingBindingAsync(config,
            new(historical, new("Historical", ""), ""), Path.Combine(_root!, "racing-missing-source"), config.ConfigRevision);
        otherFolder.Id = historical.ToString();
        Require((await missingOperation).Status == HistoryConfigurationRepairStatus.SaveFailed, "A racing identity collision was not rejected by capture.");
        Require(config.SourceFolders.Count == 1 && otherFolder.Id == historical.ToString(), "Identity compensation changed another editor's Source.");
        await DrainAsync();
        Require(Disk(other.Id).SourceFolders.Single().Id == historical.ToString(), "Compensating snapshot lost the other configuration edit.");
        // Loading keeps its explicit compatibility repair; only ordinary saving rejects bad IDs.
        var legacy = ConfigSnapshotMapper.Copy(ConfigService.CurrentConfig);
        var firstId = legacy.BackupConfigs[0].SourceFolders.Single().Id;
        legacy.BackupConfigs[1].SourceFolders.Single().Id = firstId;
        legacy.BackupConfigs[1].SourceFolders.Add(new ManagedFolder { Id = "invalid", Path = Path.Combine(_root!, "legacy-source") });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(legacy, AppJsonContext.Default.AppConfig);
        await AtomicFileService.WriteAsync(ConfigService.ConfigFilePath, (stream, token) => stream.WriteAsync(bytes, token).AsTask());
        Require(ConfigService.Reload(), "The real Host loader no longer repairs legacy invalid/duplicate source identities.");
        var loaded = ConfigService.CurrentConfig.BackupConfigs.SelectMany(item => item.SourceFolders).ToArray();
        Require(loaded[0].Id == firstId && loaded.All(item => Guid.TryParse(item.Id, out var id) && id != Guid.Empty)
            && loaded.Select(item => Guid.Parse(item.Id)).Distinct().Count() == loaded.Length, "Load-time identity repair changed a valid first Source or retained a collision.");
        Require((await ConfigService.SaveAsync(false)).Success, "Repaired load-time identities must pass ordinary persistence validation.");
        Results.Add(new { group = "source-id-preflight-and-capture-isolation", status = "PASS" });
    }

    private static async Task QueuedSnapshotsAsync()
    {
        var (config, folder, other) = await ResetAsync("queued-snapshots");
        using var barrier = new WriteBarrier();
        var entered = barrier.Entered; var release = barrier.Release; var calls = 0;
        _beforeWrite = async _ =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            entered.TrySetResult(); await release.Task; throw new IOException("acceptance.queued-initial-failure");
        };
        var repair = RepairAsync(config, folder);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        other.Name = "Queued edit";
        var queued = Enumerable.Range(0, 12).Select(_ => ConfigService.SaveAsync()).ToArray();
        var queuedFlush = ConfigService.FlushAsync(); // Drains the real UI capture before releasing the writer.
        Require(!queuedFlush.IsCompleted && queued.All(task => !task.IsCompleted), "Queued saves completed before durability.");
        release.TrySetResult();
        Require((await repair.WaitAsync(TimeSpan.FromSeconds(30))).Status == HistoryConfigurationRepairStatus.SaveFailed, "Queued compensation must still report the original failure.");
        Require((await Task.WhenAll(queued)).All(result => result.Success), "Accepted following snapshots did not complete.");
        await queuedFlush; await DrainAsync();
        Require(Disk(config.Id).SourceFolders.Single().SourceScope.IncludePatterns.SequenceEqual(new[] { "saves/**" })
            && Disk(other.Id).Name == "Queued edit" && _maxWriters == 1 && _saved == _successfulWrites,
            "The final compensation did not follow temporary snapshots, or Saved was counted per caller.");
        Results.Add(new { group = "queued-snapshots-and-durable-batches", status = "PASS", logicalRequests = queued.Length + 2,
            physicalWrites = _writes, successfulWrites = _successfulWrites, savedEvents = _saved });
    }

    private static async Task CompensationFailureAsync()
    {
        var (config, folder, _) = await ResetAsync("compensation-failure");
        var before = await File.ReadAllBytesAsync(ConfigService.ConfigFilePath);
        var calls = 0;
        _beforeWrite = _ => Task.FromException(new IOException("acceptance.write-" + Interlocked.Increment(ref calls) + "-failed"));
        var result = await RepairAsync(config, folder);
        await DrainAsync();
        Require(result.Status == HistoryConfigurationRepairStatus.SaveFailed
            && result.Diagnostic.Contains("acceptance.write-1-failed", StringComparison.Ordinal)
            && result.Diagnostic.Contains("acceptance.write-2-failed", StringComparison.Ordinal), "Both persistence failures must remain diagnosable.");
        Require(_writes == 2 && _successfulWrites == 0 && _saved == 0
            && (await File.ReadAllBytesAsync(ConfigService.ConfigFilePath)).AsSpan().SequenceEqual(before), "A failed write published Saved or altered the durable baseline.");
        Results.Add(new { group = "failed-compensation", status = "PASS" });
    }

    private static async Task FailureNotificationsAsync(Window window)
    {
        var (_, folder, _) = await ResetAsync("save-failure-notifications");
        NotificationService.Core.Store.Remove("configuration:persistence-failure");
        ConfigService.CurrentConfig.GlobalSettings.EnableNotifications = true;
        NativeWindowActivationService.TryActivate(window);
        await UntilAsync(NativeWindowActivationService.IsAppForeground);
        var deliveries = new List<NotificationEvent>();
        var bannerEvidence = new List<object>();
        void Delivered(NotificationEvent item)
        {
            if (item.Id == "configuration:persistence-failure") deliveries.Add(item);
        }
        void Banner(NotificationBannerRequest request)
        {
            if (request.Event.Id == "configuration:persistence-failure")
                bannerEvidence.Add(new { request.Event.Revision, request.Event.FailureKey, request.Event.AlertOnce, request.QuietUpdate });
        }
        NotificationService.Core.Delivered += Delivered;
        NotificationService.InfoBarRequested += Banner;
        try
        {
            var original = folder.Id;
            folder.Id = "invalid";
            _ = ConfigService.SaveAsync(); // Deliberately no UI caller observes this automatic save result.
            await DrainAsync();
            await UntilAsync(() => deliveries.Any(item => item.FailureKey.EndsWith("configuration.source-id-invalid", StringComparison.Ordinal)));
            var record = NotificationCenterService.Find("configuration:persistence-failure");
            Require(record is { Severity: NotificationSeverity.Error, Target.Kind: NotificationTargetKind.Configuration }
                && record.Details == I18n.GetString("Config_SourceIdInvalid"), "An unobserved automatic save failure is not visible in the real center.");
            var revision = record!.Revision;
            var firstDeliveries = deliveries.Count;
            var repeats = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => ConfigService.SaveAsync()));
            await DrainAsync(); await YieldAsync();
            Require(repeats.All(result => !result.Success) && NotificationCenterService.Find(record.Id)?.Revision == revision
                && NotificationCenterService.Snapshot().Count(item => item.Id == record.Id) == 1 && _writes == 0 && _saved == 0
                && deliveries.Count == firstDeliveries, "Repeated failed captures were not deduplicated, or they emitted Saved/another alert.");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await ConfigService.SaveAsync(cancellationToken: cancellation.Token); throw new InvalidOperationException("Expected cancellation."); }
            catch (OperationCanceledException) { }
            folder.Id = original;
            Require((await ConfigService.SaveAsync(publishSavedEvent: false)).Success, "A corrected configuration must save.");
            await DrainAsync();
            Require(_successfulWrites == 1 && _saved == 0, "publishSavedEvent=false was ignored.");

            // The first real presentation populated the existing delivery ledger. A new failure
            // after recovery must still be alertable immediately, with the same center record ID.
            _beforeWrite = _ => Task.FromException(new IOException("acceptance.after-recovery-write-failed"));
            Require(!(await ConfigService.SaveAsync()).Success, "Expected the new IO failure after a successful recovery save.");
            await DrainAsync();
            await UntilAsync(() => deliveries.Any(item => item.FailureKey.EndsWith("configuration.save-failed", StringComparison.Ordinal)));
            Require(deliveries.Count > firstDeliveries && deliveries.All(item => !item.AlertOnce)
                && NotificationCenterService.Find(record.Id)?.Revision != revision, "A new failure was silenced by the previous operation alert limit.");
            Results.Add(new { group = "automatic-failure-visibility-and-deduplication", status = "PASS",
                recoverySaveSucceeded = true, firstFailurePresented = firstDeliveries,
                newFailureAfterRecoveryPresented = deliveries.Count - firstDeliveries,
                deliveries = deliveries.Select(item => new { item.Revision, item.FailureKey, item.AlertOnce }).ToArray(), bannerEvidence });
        }
        finally
        {
            _beforeWrite = null;
            NotificationService.Core.Delivered -= Delivered;
            NotificationService.InfoBarRequested -= Banner;
        }
    }

    private static async Task HistoryCommandAsync(Window window, bool leavePage, int ordinal)
    {
        var (config, folder, _) = await ResetAsync("history-command-" + ordinal);
        ConfigService.CurrentConfig.GlobalSettings.EnableNotifications = true;
        window.Activate();
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config);
        var configId = runtime.ConfigId; var source = new SourceId(Guid.Parse(folder.Id));
        var boundary = Mismatch(folder).HistoricalBoundary;
        var codec = new HistoryPackCodec(); var time = DateTimeOffset.UtcNow;
        var version = new SourceVersion(VersionId.New(), configId, source, [], time, null, CaptureScope.FullSource,
            CaptureOutcome.Captured, [], new(folder.DisplayName, folder.Path), null, HistoryProvenance.Native("configuration-acceptance"), boundary);
        var representation = new VersionRepresentation(RepresentationId.New(), version.VersionId, RepresentationKind.CoreFull,
            "7z", [], MaterializationFidelity.Exact, null, null, new Dictionary<string, string> { ["fileName"] = "metadata-only.fixture" });
        var checkpoint = new SourceCheckpoint(CheckpointId.New(), configId, time, null, HistoryProvenance.Native("configuration-acceptance"),
            [new CheckpointSource(source, version.SourceDescriptorSnapshot, version.VersionId, CheckpointSourceDisposition.Captured, boundary)]);
        var branch = BranchId.New();
        var update = new BranchUpdate(BranchUpdateId.New(), branch, [], "Acceptance", checkpoint.CheckpointId, false, time, BranchUpdateReason.Created, source);
        await runtime.Repository.CommitAsync(new(PackId.New(), HistoryTransactionId.New(), time,
            new object[] { version, representation, checkpoint, update }.Select(item => codec.CreateObject(item))));
        var payload = Path.Combine(_root!, "history-command-" + ordinal, "metadata-only.fixture");
        await File.WriteAllTextAsync(payload, "Metadata-only admission fixture; never extract this file.");
        var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync()).Value;
        await runtime.LocalReplicaCatalogStore.SaveAsync(new(configId, (catalog?.CatalogRevision ?? -1) + 1,
            new[] { new LocalReplicaCatalogEntry(representation.RepresentationId, LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(payload), time) }),
            catalog?.CatalogRevision ?? LocalReplicaCatalogStore.MissingRevision);
        var workspace = (await runtime.WorkspaceStore.LoadAsync()).Value;
        if (workspace is null) await runtime.WorkspaceStore.SaveAsync(new(configId, 0, Array.Empty<WorkspaceSourceBaseline>()), HistoryWorkspaceStore.MissingRevision);
        var plan = await NativeHistoryApplicationService.PlanCheckoutAsync(config, update.UpdateId);
        Require(plan.Readiness == HistoryCheckoutReadiness.ConfigurationBoundaryChangeRequired,
            "The real Host planner did not reach boundary repair: " + plan.Readiness + ": " + plan.Diagnostic);
        Require((await ConfigService.SaveAsync(false)).Success, "History fixture setup must persist before fault injection.");
        await DrainAsync();
        var sourceMarker = Path.Combine(folder.Path, "must-remain.txt");
        await File.WriteAllTextAsync(sourceMarker, "Checkout must never proceed after a failed configuration repair.");
        var sourceMarkerBefore = await File.ReadAllBytesAsync(sourceMarker);
        var workspacePath = Path.Combine(runtime.Repository.Paths.LocalStateRoot, "workspace.json");
        var workspaceBefore = await File.ReadAllBytesAsync(workspacePath);
        var packsBefore = Directory.GetFiles(runtime.Repository.Paths.PacksRoot, "*.frpack", SearchOption.AllDirectories).Length;
        var packSha256Before = await PackInventorySha256Async(runtime.Repository.Paths.PacksRoot);
        Require(NavigationService.NavigateTo("History", ManagerNavigationParameter.ForFolder(config.Id, folder.Path)),
            "The Host refused History navigation.");
        HistoryPage? attachedPage = null;
        await UntilAsync(() =>
        {
            attachedPage = Find<HistoryPage>(window.Content).FirstOrDefault(candidate => candidate.IsLoaded
                && candidate.XamlRoot is not null && candidate.ViewModel.IsConfigurationRepairAcceptanceReady(config, folder));
            return attachedPage is not null;
        }, "The actual HistoryPage did not finish loading and activating after navigation.", TimeSpan.FromSeconds(20));
        var page = attachedPage ?? throw new InvalidOperationException("Actual HistoryPage readiness was lost.");
        page.ViewModel.ConfigureConfigurationRepairAcceptance(config, folder, new(new(branch, "Acceptance", [update], false, false, false,
            false, false, false, true, true, true, source), plan));
        await DrainAsync();
        var configBefore = await File.ReadAllBytesAsync(ConfigService.ConfigFilePath);
        var sourceConfigBefore = JsonSerializer.SerializeToUtf8Bytes(Disk(config.Id), AppJsonContext.Default.BackupConfig);
        var entered = Signal(); var release = Signal(); var calls = 0; var warnings = 0; var successes = 0;
        var bannerSync = new object();
        var observing = true;
        var commandStage = "subscribed";
        var bannerEvents = new List<object>();
        void Banner(NotificationBannerRequest request)
        {
            lock (bannerSync)
            {
                if (!observing) return;
                var item = request.Event;
                bannerEvents.Add(new
                {
                    observedUtc = DateTimeOffset.UtcNow, commandStage,
                    pageActive = page.ViewModel.IsConfigurationRepairAcceptanceActive,
                    cancellationRequested = page.ViewModel.CheckoutBranchCommand.IsCancellationRequested,
                    ambientOperation = NotificationOperationScope.Current?.Target,
                    item.Id, item.Revision, item.Title, item.Message, item.Details, item.TechnicalDetails, item.ResultKey,
                    category = item.Category.ToString(), severity = item.Severity.ToString(), importance = item.Importance.ToString(),
                    item.Target, item.Correlation, item.OccurredUtc, item.KeepInCenter, item.AlertOnce, item.AutoCloseMs,
                    item.CompletionSoundSuccess, item.FailureKey, item.ActionText, hasTransientAction = item.TransientAction is not null,
                    item.Arguments, item.Logo, item.Group, item.SystemOnly, item.Content, item.Product,
                    item.SuppressAlert, item.RestoreReadState,
                    request.Generation, request.QuietUpdate, request.Resume, request.PresentationGeneration
                });
                if (item.Severity == NotificationSeverity.Warning && item.Message.Contains("acceptance.history-write-failed", StringComparison.Ordinal)) warnings++;
                // Preserve the global zero-success gate. Unknown or unrelated successes remain
                // visible and failing; their full identity is evidence rather than an exclusion.
                if (item.Severity == NotificationSeverity.Success || item.Content?.Result == NotificationResultKind.Success) successes++;
            }
        }
        NotificationService.InfoBarRequested += Banner;
        _beforeWrite = async _ =>
        {
            if (!ContainsAppliedScope(_, config.Id)) return;
            if (Interlocked.Increment(ref calls) != 1) return;
            entered.TrySetResult(); await release.Task; throw new IOException("acceptance.history-write-failed-" + ordinal);
        };
        try
        {
            await WriteAsync("history-confirm-" + ordinal + ".ready.json", new { status = "READY", ordinal,
                primaryButtonText = I18n.GetString("History_Checkout_RepairBoundaryPrimary") });
            commandStage = "awaiting-confirmation";
            var command = page.ViewModel.CheckoutBranchCommand.ExecuteAsync(null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(45));
            commandStage = "io-held";
            if (leavePage)
            {
                Require(NavigationService.NavigateTo("Home"), "The Host refused the page-leave navigation.");
                await UntilAsync(() => !page.ViewModel.IsConfigurationRepairAcceptanceActive,
                    "The History ViewModel did not suspend after leaving the page.");
                commandStage = "left-page";
            }
            Require(!command.IsCompleted, "Leaving the page must not abandon the durable result.");
            await using var gate = new PendingLease(config.Id);
            Require(!gate.Task.IsCompleted, "History repair released its configuration gate before compensation.");
            release.TrySetResult();
            commandStage = "io-released";
            await command.WaitAsync(TimeSpan.FromSeconds(30));
            commandStage = "command-completed";
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await DrainAsync();
            var workspaceAfter = await File.ReadAllBytesAsync(workspacePath);
            var packsAfter = Directory.GetFiles(runtime.Repository.Paths.PacksRoot, "*.frpack", SearchOption.AllDirectories).Length;
            var packSha256After = await PackInventorySha256Async(runtime.Repository.Paths.PacksRoot);
            var sourceMarkerBytesAfter = await File.ReadAllBytesAsync(sourceMarker);
            var sourceMarkerAfter = await File.ReadAllTextAsync(sourceMarker);
            var configAfter = await File.ReadAllBytesAsync(ConfigService.ConfigFilePath);
            var sourceConfigAfter = JsonSerializer.SerializeToUtf8Bytes(Disk(config.Id), AppJsonContext.Default.BackupConfig);
            var scopeAfter = Disk(config.Id).SourceFolders.Single().SourceScope.IncludePatterns.ToArray();
            var failedSave = NotificationCenterService.Find("configuration:persistence-failure");
            var failureRecorded = failedSave is { Severity: NotificationSeverity.Error, Target.Kind: NotificationTargetKind.Configuration }
                && failedSave.Details.Contains("acceptance.history-write-failed-" + ordinal, StringComparison.Ordinal);
            var unchanged = workspaceAfter.AsSpan().SequenceEqual(workspaceBefore) && packsAfter == packsBefore
                && packSha256After == packSha256Before && sourceMarkerBytesAfter.AsSpan().SequenceEqual(sourceMarkerBefore)
                && sourceMarkerAfter == "Checkout must never proceed after a failed configuration repair."
                && scopeAfter.SequenceEqual(new[] { "saves/**" }) && sourceConfigAfter.AsSpan().SequenceEqual(sourceConfigBefore);
            lock (bannerSync) observing = false;
            NotificationService.InfoBarRequested -= Banner;
            await WriteAsync("history-command-" + ordinal + ".evidence.json", new
            {
                leavePage, pageActive = page.ViewModel.IsConfigurationRepairAcceptanceActive,
                cancellationRequested = page.ViewModel.CheckoutBranchCommand.IsCancellationRequested,
                warnings, successes, bannerEvents = bannerEvents.ToArray(), failureRecorded, failureNotification = failedSave,
                workspaceSha256Before = Convert.ToHexString(SHA256.HashData(workspaceBefore)),
                workspaceSha256After = Convert.ToHexString(SHA256.HashData(workspaceAfter)),
                configSha256Before = Convert.ToHexString(SHA256.HashData(configBefore)),
                configSha256After = Convert.ToHexString(SHA256.HashData(configAfter)),
                sourceConfigSha256Before = Convert.ToHexString(SHA256.HashData(sourceConfigBefore)),
                sourceConfigSha256After = Convert.ToHexString(SHA256.HashData(sourceConfigAfter)),
                sourceMarkerSha256Before = Convert.ToHexString(SHA256.HashData(sourceMarkerBefore)),
                sourceMarkerSha256After = Convert.ToHexString(SHA256.HashData(sourceMarkerBytesAfter)),
                packsBefore, packsAfter, packSha256Before, packSha256After, sourceMarkerAfter, scopeAfter, unchanged
            });
            Require(unchanged,
                "Checkout or a late configuration write continued after repair failure.");
            Require(successes == 0, "A failed repair must not report checkout success.");
            Require(failureRecorded, "The durable configuration-save failure disappeared from the notification center.");
            Require(warnings == (leavePage ? 0 : 1), $"The real History warning count was {warnings}; leavePage={leavePage}, successes={successes}.");
            Require(!leavePage || !page.ViewModel.IsConfigurationRepairAcceptanceActive,
                "The retired History page became active again while compensation completed.");
            Results.Add(new { group = "real-history-command", status = "PASS", leavePage, warnings, successes, failureRecorded,
                pageActive = page.ViewModel.IsConfigurationRepairAcceptanceActive,
                cancellationRequested = page.ViewModel.CheckoutBranchCommand.IsCancellationRequested });
        }
        finally
        {
            release.TrySetResult(); _beforeWrite = null;
            lock (bannerSync) observing = false;
            NotificationService.InfoBarRequested -= Banner;
            await WriteAsync("history-command-" + ordinal + ".banner-events.json", new
                { commandStage, warnings, successes, events = bannerEvents.ToArray() });
            page.ViewModel.Suspend();
        }
    }

    private static BackupConfig Disk(string id)
        => JsonSerializer.Deserialize(File.ReadAllBytes(ConfigService.ConfigFilePath), AppJsonContext.Default.AppConfig)!
            .BackupConfigs.Single(item => item.Id == id);
    private static async Task<string> PackInventorySha256Async(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.GetFiles(root, "*.frpack", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
        {
            var digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path).Replace('\\', '/') + "\n" + digest + "\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static async Task DrainAsync() { await ConfigService.FlushAsync(); await YieldAsync(); }
    private static async Task UntilAsync(Func<bool> condition,
        string timeoutMessage = "Expected Host continuation did not finish.", TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException(timeoutMessage);
            await Task.Delay(20);
        }
    }
    private static bool ContainsAppliedScope(ReadOnlyMemory<byte> payload, string configId)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("BackupConfigs").EnumerateArray().Any(config =>
            config.GetProperty("Id").GetString() == configId
            && config.GetProperty("SourceFolders").EnumerateArray().Any(folder =>
                folder.GetProperty("SourceScope").GetProperty("IncludePatterns").EnumerateArray().Any(pattern => pattern.GetString() == "REGION/**")));
    }
    private static Task YieldAsync()
    {
        var signal = Signal(); UiDispatcherService.Post(() => signal.TrySetResult()); return signal.Task;
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static Task WriteAsync(string name, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true });
        return AtomicFileService.WriteAsync(Path.Combine(_root!, name), (stream, token) => stream.WriteAsync(bytes, token).AsTask());
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private sealed class PendingLease(string id) : IAsyncDisposable
    {
        internal Task<NativeHistoryConfigurationOperationGate.Lease> Task { get; } = NativeHistoryConfigurationOperationGate.EnterAsync(id).AsTask();
        public async ValueTask DisposeAsync() { await using var lease = await Task; }
    }
    private sealed class WriteBarrier : IDisposable
    {
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource Release { get; } = Signal();
        public void Dispose() => Release.TrySetResult();
    }
}
#endif

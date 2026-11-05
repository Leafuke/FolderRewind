#if SPATIAL_WORKSPACE_UI_ACCEPTANCE
using FolderRewind.History.Merge;
using FolderRewind.Controls;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Services.Plugins.V3;
using FolderRewind.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Development;

// Independent presentation/lifecycle fixture. It creates its own fixed trees, never history versions.
internal static class SpatialWorkspaceUiAcceptance
{
    private static string? _root;
    private static readonly Dictionary<WeakReference, (int Generation, string Kind, string Id)> WeakLabels = [];
    private static readonly List<UiSpatialRelease> Releases = [];
    private static int _expectedPages;
    private static int _expectedRows;
    private static UiSpatialFunctionalEvidence? _functionalEvidence;
    internal static void Prepare()
    {
        if (Environment.GetEnvironmentVariable("FOLDERREWIND_SPATIAL_UI") != "1") return;
        var data = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_DATA_ROOT");
        if (!AppRuntimeInfo.IsMsiDistribution || string.IsNullOrWhiteSpace(data) || !Path.IsPathFullyQualified(data))
            throw new InvalidOperationException("Spatial UI acceptance requires an isolated MSI data root.");
        var full = Path.GetFullPath(data);
        var root = Directory.GetParent(full)?.FullName;
        if (root is null || !File.Exists(Path.Combine(root, "acceptance-owner")) ||
            !string.Equals(Path.GetFileName(full), "appdata", StringComparison.OrdinalIgnoreCase) ||
            ConfigService.CurrentConfig.BackupConfigs.Count != 0)
            throw new InvalidOperationException("Spatial UI acceptance requires a fresh marked root.");
        _root = root;
        if (!int.TryParse(Environment.GetEnvironmentVariable("FOLDERREWIND_SPATIAL_ROWS"), out _expectedRows) ||
            _expectedRows is not (10_000 or 50_000 or 100_000))
            throw new InvalidOperationException("Spatial fixture row count must be 10000, 50000, or 100000.");
        var config = new BackupConfig { Id = Guid.NewGuid().ToString(), Name = "Spatial UI fixture",
            DestinationPath = Path.Combine(root, "repository"),
            Kind = new ConfigKindReference { OwnerId = SpatialWorkspaceUiProvider.Id.Value, KindId = "preview" } };
        config.SourceFolders.Add(new ManagedFolder { Id = Guid.NewGuid().ToString(),
            Path = Path.Combine(root, "unused-live-source"), DisplayName = "Spatial UI source" });
        ConfigService.CurrentConfig.BackupConfigs.Add(config);
        ConfigService.CurrentConfig.GlobalSettings.EnableNotifications = false;
        ConfigService.CurrentConfig.GlobalSettings.Plugins.AutoCheckUpdates = false;
    }
    internal static void Start(Window window)
    {
        if (_root is null) return;
        window.DispatcherQueue.TryEnqueue(() => TaskObserver.Observe(RunAsync(window), nameof(SpatialWorkspaceUiAcceptance)));
    }
    private static async Task RunAsync(Window window)
    {
        try
        {
            await Task.Delay(2000);
            var lifetimeOnly = Environment.GetEnvironmentVariable("FOLDERREWIND_SPATIAL_LIFETIME_ONLY") == "1";
            var weak = await ExercisePageAsync(window, lifetimeOnly);
            await SpatialWorkspaceUiProvider.DeactivateAsync();
            await WriteAsync("gc-ready.json", new { status = "READY", lifetimeOnly, tracked = weak.Length });
            // The driver first moves automation focus off the retired page and releases its UI workflow.
            await RequestAsync("gc.request");
            var before = CountTargets(weak);
            await Task.Delay(1000);
            var rounds = await CollectRoundsAsync(weak);
            var after = CountTargets(weak);
            var previousNavigation = FrameSnapshot(window);
            await WriteAsync("weak-lifetime.json", new { status = "OBSERVED", lifetimeOnly, before, after, rounds,
                targets = SnapshotTargets(weak), frame = previousNavigation });
            // Always perform the real Tasks -> Home navigation, including the full 100k fixture.
            await WriteAsync("navigation-reset-ready.json", new { status = "READY", previousNavigation });
            await RequestAsync("tasks-visited.request");
            var tasksNavigation = FrameSnapshot(window);
            await WriteAsync("tasks-observation.json", new { status = "OBSERVED", frame = tasksNavigation });
            await RequestAsync("navigation-gc.request");
            var currentNavigation = FrameSnapshot(window);
            var navigationRounds = await CollectRoundsAsync(weak);
            var afterNavigation = CountTargets(weak);
            await WriteAsync("navigation-lifetime.json", new { status = "OBSERVED", previousNavigation, tasksNavigation, currentNavigation,
                afterNavigation, rounds = navigationRounds });
            var evidence = new UiSpatialLifetimeEvidence(_expectedPages, Releases.ToArray(), previousNavigation,
                tasksNavigation, currentNavigation, rounds, navigationRounds);
            await WriteAsync("spatial-observation.json", new { status = "OBSERVED", lifetimeOnly, functional = _functionalEvidence, evidence,
                weakTargets = new { tracked = weak.Length, before, after, afterNavigation } });
            var lifecycle = UiAcceptanceCriteria.EvaluateSpatial(evidence);
            var functionality = UiAcceptanceCriteria.EvaluateSpatialFunctionality(_functionalEvidence);
            var status = lifecycle.Status != "PASS" || functionality.Status != "PASS" ? "FAIL" :
                lifetimeOnly ? "PASS" : "NOT_EVALUATED";
            await WriteAsync("spatial-results.json", new { status,
                collection = new { status = "PASS" },
                functionality = new { status = functionality.Status, functionality.Failures,
                    scope = lifetimeOnly ? "Stop and close with a held 500-row scan" : $"{_expectedRows} row scan, latest filter, Stop and close" },
                performance = new { status = lifetimeOnly ? "NOT_APPLICABLE" : "NOT_EVALUATED",
                    reason = lifetimeOnly ? "Held-scan lifecycle scenario." : "A single scan is an observation; median comparison is evaluated separately." },
                lifecycle = new { status = lifecycle.Status, lifecycle.Failures },
                lifetimeOnly, scope = lifetimeOnly ? "Small held-scan lifecycle diagnostic; does not replace the complete 100k functional run."
                    : "Actual SpatialWorkspacePage presentation and lifecycle with fixture-owned fixed trees and fake provider; no history materialization or backup/restore coverage." });
        }
        catch (Exception error)
        {
            var page = Find<SpatialWorkspacePage>(window.Content).FirstOrDefault();
            await WriteAsync("spatial-observation.json", new { status = "ERROR", error = error.ToString(),
                pageStatus = page?.ViewModel.Status, pageError = page?.ViewModel.Error,
                isScanning = page?.ViewModel.IsScanning });
            await WriteAsync("spatial-results.json", new { status = "FAIL", error = error.ToString(),
                collection = new { status = "FAIL" }, functionality = new { status = "FAIL", reason = "Incomplete scenario." },
                performance = new { status = "NOT_EVALUATED" }, lifecycle = new { status = "FAIL", reason = "Incomplete evidence." } });
        }
    }
    private static async Task<WeakReference[]> ExercisePageAsync(Window window, bool lifetimeOnly)
    {
        var config = ConfigService.CurrentConfig.BackupConfigs.Single();
        var storage = await OwnedPluginTemporaryStorage.CreateAsync(Path.Combine(_root!, "fixed-inputs"));
        var leftRoot = await storage.CreateDirectoryAsync(default);
        var rightRoot = await storage.CreateDirectoryAsync(default);
        var indexRoot = await storage.CreateDirectoryAsync(default);
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "fixture.txt"), "left fixed input");
        await File.WriteAllTextAsync(Path.Combine(rightRoot, "fixture.txt"), "right fixed input");
        var leftTree = await MergeTreeManifest.ReadAsync(leftRoot, _ => true, default);
        var rightTree = await MergeTreeManifest.ReadAsync(rightRoot, _ => true, default);
        var left = new SpatialFixedInput("spatial-ui-left/" + leftTree.Digest, leftRoot, leftTree.Digest);
        var right = new SpatialFixedInput("spatial-ui-right/" + rightTree.Digest, rightRoot, rightTree.Digest);
        SpatialWorkspaceUiProvider.Configure(left, right, holdComparison: lifetimeOnly, holdRender: false);
        await SpatialWorkspaceUiProvider.ActivateAsync(PluginV3ModelMapper.ToSnapshot(config));
        if (!NavigationService.NavigateTo("SpatialWorkspace")) throw new InvalidOperationException("Spatial page navigation failed.");
        await WaitAsync(() => Find<SpatialWorkspacePage>(window.Content).Any(), "Spatial page visual-tree load");
        var page = Find<SpatialWorkspacePage>(window.Content).Single();
        var vm = page.ViewModel;
        UiFullScanObservation? fullScan = null;
        UiFilterObservation? filter = null;
        var diagnostics = lifetimeOnly ? null : new SpatialIndexDiagnostics();
        var scanClock = Stopwatch.StartNew();
        await vm.InitializeUiAcceptanceAsync(config, storage, Path.Combine(indexRoot, "spatial.db"), left, right, diagnostics);
        if (lifetimeOnly)
        {
            await SpatialWorkspaceUiProvider.ComparisonBlocked.WaitAsync(TimeSpan.FromSeconds(20));
            await vm.CancelComparisonAsync();
            await WriteAsync("diagnostic-ready.json", new { status = "READY", lifetimeOnly = true, indexed = vm.UiAcceptanceIndexedCount() });
        }
        else
        {
        await WaitAsync(() => !vm.IsBusy && !vm.IsScanning && vm.UiAcceptanceState().ScanCompleted &&
            vm.UiAcceptanceState().RowsCurrent && page.UiAcceptanceOverlayDrained,
            "Full scan completion", 900);
        scanClock.Stop();
        // Snapshot immediately at the measurement boundary; serialization and file I/O are outside the timer.
        var operations = diagnostics!.Operations.ToArray();
        var ui = diagnostics.Ui.ToArray();
        fullScan = new(_expectedRows, vm.UiAcceptanceIndexedCount(), vm.Items.Count, vm.Error);
        await WriteAsync("full-ready.json", new { status = "OBSERVED", rows = vm.UiAcceptanceIndexedCount(), visiblePage = vm.Items.Count,
            initializationAndScanSeconds = scanClock.Elapsed.TotalSeconds, overlayDrained = page.UiAcceptanceOverlayDrained, observation = fullScan });
        await WriteAsync("spatial-index-observation.json", new { status = "OBSERVED", rowCount = _expectedRows,
            scanMs = scanClock.Elapsed.TotalMilliseconds, stopwatchFrequency = Stopwatch.Frequency,
            peakOperations = diagnostics.PeakOperations, operations, ui });
        var snapshotPath = Path.Combine(_root!, "full-spatial.db");
        await vm.SaveUiAcceptanceIndexSnapshotAsync(snapshotPath);
        await using (var stream = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length <= 0) throw new InvalidOperationException("The spatial index snapshot is empty.");
            var length = stream.Length;
            var hash = await SHA256.HashDataAsync(stream);
            await WriteAsync("spatial-index-snapshot.json", new { status = "OBSERVED", file = "full-spatial.db",
                bytes = length, sha256 = Convert.ToHexString(hash), rowCount = _expectedRows });
        }
        var queryReplay = await vm.MeasureUiAcceptanceQueriesAsync();
        await WriteAsync("spatial-query-replay.json", new { status = "OBSERVED", rowCount = _expectedRows,
            scope = "Three warm full-index presentation reads, same actual page query and committed index; outside scan timing.",
            operations = queryReplay });
        await RequestAsync("rapid.request");
        await page.RunRapidUiAcceptanceFiltersAsync();
        await Task.Delay(500);
        await WaitAsync(() => vm.UiAcceptanceState().RowsCurrent, "Last filter publication");
        await WaitAsync(() => page.UiAcceptanceOverlayDrained, "Last filter overlay drain");
        var overlays = page.CaptureUiAcceptanceOverlays();
        await WriteAsync("rapid-overlay-observation.json", new { status = "OBSERVED", observation = overlays });
        var overlayResult = UiAcceptanceCriteria.EvaluateSpatialOverlays(overlays);
        await WriteAsync("rapid-overlay-results.json", new { status = overlayResult.Status, overlayResult.Failures });
        filter = new(vm.Search, vm.Filter, vm.Category, vm.Counts, I18n.Format("SpatialWorkspace_CompareCounts", 1, 1),
            vm.CategoryBreakdown.Split(" · "), vm.Items.Select(item => item.Id).ToArray(), vm.Error, overlays);
        await WriteAsync("rapid-results.json", new { status = "OBSERVED", vm.Search, vm.Filter, vm.Category, vm.Counts,
            ids = vm.Items.Select(item => item.Id).ToArray(), vm.CategoryBreakdown, observation = filter });
        await WriteAsync("spatial-interaction-observation.json", new { status = "OBSERVED", rowCount = _expectedRows,
            peakOperations = diagnostics.PeakOperations, operations = diagnostics.Operations, ui = diagnostics.Ui });
        }
        await RequestAsync("prepare-cancel.request");
        page.ResetUiAcceptanceFilters();
        SpatialWorkspaceUiProvider.HoldNextScan(render: false);
        await vm.ChangeInputsAsync();
        await SpatialWorkspaceUiProvider.ComparisonBlocked.WaitAsync(TimeSpan.FromSeconds(20));
        await WriteAsync("cancel-ready.json", new { status = "READY", vm.IsScanning, indexed = vm.UiAcceptanceIndexedCount() });
        await RequestAsync("cancel-invoked.request");
        await WaitAsync(() => !vm.IsScanning && vm.UiAcceptanceState().ScanCompleted && vm.UiAcceptanceState().RowsCurrent, "User cancellation drain");
        var canceled = SpatialWorkspaceUiProvider.ComparisonStatistics;
        var canceledRows = vm.UiAcceptanceIndexedCount();
        await Task.Delay(500);
        var stop = new UiStopObservation(canceledRows, vm.UiAcceptanceIndexedCount(), canceled.Active, canceled.CanceledCalls,
            vm.Status, I18n.GetString("SpatialWorkspace_PartialScan"), vm.Error);
        await WriteAsync("cancel-results.json", new { status = "OBSERVED", indexed = canceledRows, activeComparisons = canceled.Active,
            canceledCalls = canceled.CanceledCalls, comparisonCloses = canceled.Closes, observation = stop });
        if (canceled.Active != 0) throw new InvalidOperationException("Cannot reopen while the stopped comparison is still active.");
        await RequestAsync("prepare-close.request");
        var weak = TrackTargets(1, page.UiAcceptanceWeakTargets().Concat(vm.UiAcceptanceWeakTargets()));
        if (!NavigationService.NavigateTo("Home")) throw new InvalidOperationException("Could not leave the canceled workspace.");
        await vm.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        if (Directory.Exists(leftRoot) || Directory.Exists(rightRoot) || Directory.Exists(indexRoot) ||
            SpatialWorkspaceUiProvider.Statistics.Generations != 0 || SpatialWorkspaceUiProvider.ComparisonStatistics.Active != 0)
            throw new InvalidOperationException("Canceled workspace did not release its owned inputs before reopening.");
        await Task.Delay(500);
        Releases.Add(CaptureRelease(1, page, vm, leftRoot, rightRoot, indexRoot));
        page = null!; vm = null!;
        // A new real page gives this close-during-render case an empty canvas cache.
        var pageCount = lifetimeOnly && int.TryParse(Environment.GetEnvironmentVariable("FOLDERREWIND_SPATIAL_LIFETIME_PAGES"), out var requestedPages)
            ? requestedPages : 2;
        if (pageCount is not (2 or 5 or 20)) throw new InvalidOperationException("Lifetime page count must be 2, 5, or 20.");
        _expectedPages = pageCount;
        var previouslyCanceledRenders = SpatialWorkspaceUiProvider.ComparisonStatistics.CanceledRenders;
        for (var visit = 2; visit <= pageCount; visit++)
        {
        storage = await OwnedPluginTemporaryStorage.CreateAsync(Path.Combine(_root!, "fixed-inputs-close-" + visit));
        leftRoot = await storage.CreateDirectoryAsync(default);
        rightRoot = await storage.CreateDirectoryAsync(default);
        indexRoot = await storage.CreateDirectoryAsync(default);
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "fixture.txt"), "left fixed input");
        await File.WriteAllTextAsync(Path.Combine(rightRoot, "fixture.txt"), "right fixed input");
        leftTree = await MergeTreeManifest.ReadAsync(leftRoot, _ => true, default);
        rightTree = await MergeTreeManifest.ReadAsync(rightRoot, _ => true, default);
        left = new SpatialFixedInput("spatial-ui-left/" + leftTree.Digest, leftRoot, leftTree.Digest);
        right = new SpatialFixedInput("spatial-ui-right/" + rightTree.Digest, rightRoot, rightTree.Digest);
        previouslyCanceledRenders = SpatialWorkspaceUiProvider.ComparisonStatistics.CanceledRenders;
        SpatialWorkspaceUiProvider.Configure(left, right, holdComparison: true, holdRender: visit == pageCount);
        if (!NavigationService.NavigateTo("SpatialWorkspace")) throw new InvalidOperationException("Spatial close-pressure navigation failed.");
        await WaitAsync(() => Find<SpatialWorkspacePage>(window.Content).Any(), "Spatial close-pressure page load");
        page = Find<SpatialWorkspacePage>(window.Content).Single(); vm = page.ViewModel;
        await vm.InitializeUiAcceptanceAsync(config, storage, Path.Combine(indexRoot, "spatial.db"), left, right);
        await SpatialWorkspaceUiProvider.ComparisonBlocked.WaitAsync(TimeSpan.FromSeconds(20));
        weak = weak.Concat(TrackTargets(visit, page.UiAcceptanceWeakTargets().Concat(vm.UiAcceptanceWeakTargets()))).ToArray();
        if (visit != pageCount)
        {
            if (!NavigationService.NavigateTo("Home")) throw new InvalidOperationException("Lifetime navigation failed.");
            await vm.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
            if (Directory.Exists(leftRoot) || Directory.Exists(rightRoot) || Directory.Exists(indexRoot) ||
                !vm.UiAcceptanceIndexDisposed || SpatialWorkspaceUiProvider.Statistics.Generations != 0)
                throw new InvalidOperationException("Intermediate lifetime page did not release resources.");
            await Task.Delay(500);
            Releases.Add(CaptureRelease(visit, page, vm, leftRoot, rightRoot, indexRoot));
            page = null!; vm = null!;
        }
        }
        await SpatialWorkspaceUiProvider.RenderBlocked.WaitAsync(TimeSpan.FromSeconds(20));
        var previouslyCanceledCalls = SpatialWorkspaceUiProvider.ComparisonStatistics.CanceledCalls;
        var frame = page.Frame;
        await WriteAsync("close-ready.json", new { status = "READY", vm.IsScanning,
            generations = SpatialWorkspaceUiProvider.Statistics.Generations, activeComparisons = SpatialWorkspaceUiProvider.ComparisonStatistics.Active });
        await RequestAsync("close-invoked.request");
        var changesAfterClose = 0;
        NotifyCollectionChangedEventHandler changed = (_, _) => changesAfterClose++;
        vm.Items.CollectionChanged += changed;
        await vm.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        var after = SpatialWorkspaceUiProvider.ComparisonStatistics;
        var preview = SpatialWorkspaceUiProvider.Statistics;
        var state = vm.UiAcceptanceState();
        var ids = state.Ids;
        await Task.Delay(500);
        var close = new UiCloseObservation(state.Active, state.ScanCompleted, preview.Generations, after.Active,
            after.CanceledCalls, previouslyCanceledCalls, after.CanceledRenders, previouslyCanceledRenders,
            !Directory.Exists(leftRoot) && !Directory.Exists(rightRoot) && !Directory.Exists(indexRoot),
            vm.Items.Select(item => item.Id).SequenceEqual(ids), changesAfterClose);
        vm.Items.CollectionChanged -= changed;
        Releases.Add(CaptureRelease(pageCount, page, vm, leftRoot, rightRoot, indexRoot));
        await WriteAsync("lifecycle-results.json", new { status = "OBSERVED", active = state.Active, scanTaskCompleted = state.ScanCompleted,
            preview.Generations, preview.Renders, preview.Closes, activeComparisons = after.Active,
            comparisonCloses = after.Closes, canceledCalls = after.CanceledCalls, canceledRenders = after.CanceledRenders,
            fixtureTreesDeleted = close.FixtureTreesDeleted, indexDisposed = vm.UiAcceptanceIndexDisposed, changesAfterClose, weakTracked = weak.Length, pageCount,
            observation = close,
            navigationCacheMode = page.NavigationCacheMode.ToString(), currentFramePage = frame.SourcePageType?.FullName,
            forwardStack = frame.ForwardStack.Select(entry => new { type = entry.SourcePageType?.FullName,
                parameterType = entry.Parameter?.GetType().FullName }).ToArray() });
        frame = null!;
        page = null!; vm = null!;
        _functionalEvidence = new(lifetimeOnly, fullScan, filter, stop, close);
        return weak;
    }
    private static async Task WaitAsync(Func<bool> ready, string name, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (!ready()) throw new TimeoutException(name);
    }
    private static Task RequestAsync(string name) => WaitAsync(() => File.Exists(Path.Combine(_root!, name)), "Missing driver marker: " + name, 180);
    private static async Task WriteAsync(string name, object result)
    {
        var path = Path.Combine(_root!, name);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
    private static UiFrameEvidence FrameSnapshot(Window window)
    {
        var frame = Find<Frame>(window.Content).FirstOrDefault();
        return new(frame?.SourcePageType?.FullName, frame?.BackStack.Count,
            frame?.ForwardStack.Select(entry => entry.SourcePageType?.FullName).ToArray(),
            frame?.ForwardStack.Select(entry => entry.Parameter?.GetType().FullName).ToArray());
    }
    private static async Task<UiWeakRound[]> CollectRoundsAsync(WeakReference[] weak)
    {
        var rounds = new List<UiWeakRound>();
        for (var round = 1; round <= 3; round++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            await Task.Delay(150); // WinRT/XAML releases may require the UI queue to run.
            rounds.Add(new(round, SnapshotTargets(weak)));
        }
        return rounds.ToArray();
    }
    private static UiWeakTarget[] SnapshotTargets(WeakReference[] weak) => weak.Select(reference =>
    {
        var label = WeakLabels[reference];
        return new UiWeakTarget(label.Generation, label.Kind, label.Id, reference.IsAlive);
    }).ToArray();
    private static WeakReference[] TrackTargets(int generation, IEnumerable<object> targets)
    {
        var tracked = new List<WeakReference>();
        foreach (var target in targets.Distinct(ReferenceEqualityComparer.Instance))
        {
            if (WeakLabels.Keys.Any(reference => ReferenceEquals(reference.Target, target)))
                throw new InvalidOperationException("A target was shared between independently tracked page generations.");
            var weak = new WeakReference(target);
            var kind = target.GetType().Name;
            WeakLabels.Add(weak, (generation, kind, $"page-{generation}/{kind}/{tracked.Count}/{RuntimeHelpers.GetHashCode(target)}"));
            tracked.Add(weak);
        }
        return tracked.ToArray();
    }
    private static UiSpatialRelease CaptureRelease(int generation, SpatialWorkspacePage page,
        FolderRewind.ViewModels.SpatialWorkspaceViewModel vm, string leftRoot, string rightRoot, string indexRoot)
    {
        var canvases = page.UiAcceptanceWeakTargets().OfType<SpatialPreviewCanvas>().ToArray();
        if (canvases.Length != 2) throw new InvalidOperationException("Expected two canvases for each page.");
        var state = vm.UiAcceptanceState();
        return new(generation, vm.UiAcceptanceIndexDisposed, CaptureCanvas(canvases[0]), CaptureCanvas(canvases[1]),
            !Directory.Exists(leftRoot) && !Directory.Exists(rightRoot) && !Directory.Exists(indexRoot),
            SpatialWorkspaceUiProvider.Statistics.Generations, SpatialWorkspaceUiProvider.ComparisonStatistics.Active,
            state.ScanCompleted, state.Active, page.NavigationCacheMode.ToString());
    }
    private static UiCanvasRelease CaptureCanvas(SpatialPreviewCanvas canvas)
    {
        object? Field(string name) => (typeof(SpatialPreviewCanvas).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(SpatialPreviewCanvas).FullName, name)).GetValue(canvas);
        int Count(string name) => ((ICollection)(Field(name) ?? throw new InvalidOperationException(name + " is null."))).Count;
        return new(Field("_session") is not null, Field("_layer") is not null, (bool)Field("_active")!, canvas.NavigationEnabled,
            Count("_bitmaps"), Count("_images"), Count("_pending"), (int)Field("_running")!,
            ((Canvas)Field("_tiles")!).Children.Count, ((Canvas)Field("_fallback")!).Children.Count,
            ((DispatcherTimer)Field("_schedule")!).IsEnabled);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CountTargets(WeakReference[] weak) => weak.Select(reference => reference.Target).Where(target => target is not null)
        .Distinct(ReferenceEqualityComparer.Instance).Count();
    private static IEnumerable<T> Find<T>(object? root) where T : DependencyObject
    {
        if (root is not DependencyObject node) yield break;
        if (node is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in Find<T>(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
}
#endif

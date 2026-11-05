#if NATIVE_ARTIFACT_ACCEPTANCE
using FolderRewind.Controls;
using FolderRewind.History.Application;
using FolderRewind.Services.Plugins.V3;
using FolderRewind.Models;
using FolderRewind.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FolderRewind.Services.Development;

// Isolated performance fixture. Never enabled in distribution builds.
internal static class PerformanceUiAcceptance
{
    private static string? _root;
    private static readonly List<WeakReference> Pages = [];
#if PERFORMANCE_POST_RUN_ACCEPTANCE
    internal static bool ExerciseFinished { get; private set; }
    internal static int TrackedReferenceCount { get { lock (Pages) return Pages.Count; } }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static (int Total, int Managers, int Canvases) CountTrackedTargets()
    {
        lock (Pages)
        {
            var targets = Pages.Select(reference => reference.Target).Where(target => target is not null)
                .Distinct(ReferenceEqualityComparer.Instance).ToArray();
            return (targets.Length, targets.Count(target => target is FolderManagerPage),
                targets.Count(target => target is SpatialPreviewCanvas));
        }
    }
#endif
    private static readonly List<string> Failures = [];
    private static int _cycles, _mapMoves, _taskTransitions, _managerSwitches;
    private static readonly Stopwatch Clock = new();
    internal static void Prepare()
    {
        var data = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_DATA_ROOT");
        if (Environment.GetEnvironmentVariable("FOLDERREWIND_PERFORMANCE_UI") != "1") return;
        if (!AppRuntimeInfo.IsMsiDistribution || string.IsNullOrWhiteSpace(data) || !Path.IsPathFullyQualified(data))
            throw new InvalidOperationException("Performance fixture requires the isolated MSI data root.");
        var full = Path.GetFullPath(data);
        var root = Directory.GetParent(full)?.FullName;
        if (root is null || !File.Exists(Path.Combine(root, "acceptance-owner")) ||
            !string.Equals(Path.GetFileName(full), "appdata", StringComparison.OrdinalIgnoreCase) ||
            ConfigService.CurrentConfig.BackupConfigs.Count != 0)
            throw new InvalidOperationException("Performance fixture requires a fresh marked root.");
        _root = root;
        Directory.CreateDirectory(Path.Combine(root, "source", "0", "0"));
        Directory.CreateDirectory(Path.Combine(root, "repository", "0"));
        Directory.CreateDirectory(Path.Combine(root, "repository", "1"));
        var payload = JsonSerializer.SerializeToElement(new { data = new string('x', 4096) });
        for (var c = 0; c < 2; c++)
        {
            var config = new BackupConfig { Id = new Guid(c + 100, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0).ToString(), Name = $"Performance {c}",
                DestinationPath = Path.Combine(root, "repository", c.ToString()),
                Kind = new ConfigKindReference { OwnerId = "folderrewind.performance", KindId = "preview" } };
            config.ProviderStates.Add("folderrewind.performance", new ProviderStatePayload { SchemaVersion = 1, Data = payload });
            for (var i = 0; i < 500; i++)
            {
                var folder = new ManagedFolder { Id = new Guid(c + 1, 0, 0, 0, 0, 0, 0, 0, 0, (byte)(i >> 8), (byte)i).ToString(),
                    Path = Path.Combine(root, "source", c.ToString(), i.ToString()), DisplayName = $"Source {i:D4}" };
                folder.ProviderStates.Add("folderrewind.performance", new ProviderStatePayload { SchemaVersion = 1, Data = payload });
                config.SourceFolders.Add(folder);
            }
            ConfigService.CurrentConfig.BackupConfigs.Add(config);
        }
        ConfigService.CurrentConfig.GlobalSettings.EnableNotifications = false;
        ConfigService.CurrentConfig.GlobalSettings.Plugins.AutoCheckUpdates = false;
    }
    internal static void Start(Window window)
    {
        if (_root is null) return;
#if PERFORMANCE_UI_PROFILING
        PerformanceUiProfiling.VerifySnapshotCopies(_root);
#endif
        Clock.Start();
#if PERFORMANCE_UI_PROFILING
        PerformanceUiProfiling.Start(_root, window.DispatcherQueue);
#endif
        _ = Task.Run(() => SampleAsync(window.DispatcherQueue));
        window.DispatcherQueue.TryEnqueue(async () => await ExerciseAsync(window));
    }
    private static async Task SampleAsync(DispatcherQueue queue)
    {
        try
        {
            using var writer = new StreamWriter(Path.Combine(_root!, "samples.csv"));
            await writer.WriteLineAsync("elapsed_s,phase,dispatch_ms,allocated_bytes,gc0,gc1,gc2,managed_heap_bytes,private_bytes,working_set_bytes,tracked_pages_alive,preview_generations,preview_renders,preview_closes");
            using var process = Process.GetCurrentProcess();
#if PERFORMANCE_UI_PROFILING
            while (Clock.Elapsed < TimeSpan.FromMinutes(10) && !PerformanceUiProfiling.StopExercise)
#else
            while (Clock.Elapsed < TimeSpan.FromMinutes(10))
#endif
            {
                var tcs = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
                var start = Stopwatch.GetTimestamp();
                if (!queue.TryEnqueue(() => tcs.TrySetResult(Stopwatch.GetElapsedTime(start).TotalMilliseconds)))
                    throw new InvalidOperationException("Dispatcher rejected sample.");
                var latency = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
                process.Refresh();
                var seconds = Clock.Elapsed.TotalSeconds;
                int alive;
                lock (Pages) alive = Pages.Count(w => w.IsAlive);
                var stats = PerformanceFakePreview.Statistics;
                await writer.WriteLineAsync(FormattableString.Invariant($"{seconds:F3},{(seconds < 120 ? "warmup" : "measure")},{latency:F3},{GC.GetTotalAllocatedBytes(false)},{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)},{GC.GetTotalMemory(false)},{process.PrivateMemorySize64},{process.WorkingSet64},{alive},{stats.Generations},{stats.Renders},{stats.Closes}"));
                await writer.FlushAsync();
                await Task.Delay(100);
            }
        }
        catch (Exception e) { lock (Failures) Failures.Add("Sampler: " + e); }
        finally
        {
            string[] failures; lock (Failures) failures = Failures.ToArray();
            await File.WriteAllTextAsync(Path.Combine(_root!, "results.json"), JsonSerializer.Serialize(new
            { durationSeconds = Clock.Elapsed.TotalSeconds, warmupSeconds = 120, cycles = _cycles, mapMoves = _mapMoves,
                taskTransitions = _taskTransitions, managerSwitches = _managerSwitches, failures,
                scope = "UI lifecycle and rendering controls with simulated tasks; not cloud transport or FPS" }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    private static async Task ExerciseAsync(Window window)
    {
        try
        {
            // Activating a test-only in-memory provider never contacts a network.
            VerifyPreviewCurrentness();
            await Task.Delay(2000);
            File.WriteAllText(Path.Combine(_root!, "stage.txt"), "activating provider");
            await PerformanceFakePreview.ActivateAsync(PluginV3ModelMapper.ToSnapshot(ConfigService.CurrentConfig.BackupConfigs[0]));
            File.WriteAllText(Path.Combine(_root!, "stage.txt"), "initializing history");
            await NativeHistoryCoreGateway.EnsureReadyAsync(ConfigService.CurrentConfig.BackupConfigs[0]);
            File.WriteAllText(Path.Combine(_root!, "stage.txt"), "exercising");
            var mapWindow = new Window { Title = "Performance preview fixture", Content = new Grid() };
            mapWindow.Activate();
#if PERFORMANCE_UI_PROFILING
            while (Clock.Elapsed < TimeSpan.FromMinutes(10) && !PerformanceUiProfiling.StopExercise)
#else
            while (Clock.Elapsed < TimeSpan.FromMinutes(10))
#endif
            {
#if PERFORMANCE_UI_PROFILING
                PerformanceUiProfiling.BeginCycle(_cycles + 1);
                using (PerformanceUiProfiling.Measure("Manager.Navigate"))
#endif
                if (!NavigationService.NavigateTo("Manager")) throw new InvalidOperationException("Manager navigation failed.");
#if PERFORMANCE_UI_PROFILING
                PerformanceUiProfiling.ObserveNavigation("Manager.Navigate", window, mapWindow);
#endif
                await Task.Delay(350);
#if PERFORMANCE_UI_PROFILING
                PerformanceUiProfiling.MarkBoundary("Manager.Delay.Completed", window, mapWindow);
#endif
                var manager = Find<FolderManagerPage>(window.Content).FirstOrDefault() ?? throw new InvalidOperationException("Manager page missing.");
                lock (Pages) Pages.Add(new WeakReference(manager));
                var configs = ConfigService.CurrentConfig.BackupConfigs;
#if PERFORMANCE_UI_PROFILING
                using (PerformanceUiProfiling.Measure("Manager.CurrentConfig.First")) manager.ViewModel.CurrentConfig = configs[0];
                using (PerformanceUiProfiling.Measure("Manager.CurrentConfig.Second")) manager.ViewModel.CurrentConfig = configs[1];
                using (PerformanceUiProfiling.Measure("Manager.CurrentConfig.Third")) manager.ViewModel.CurrentConfig = configs[0];
                PerformanceUiProfiling.MarkBoundary("Manager.ConfigSwitches.Completed", window, mapWindow);
#else
                manager.ViewModel.CurrentConfig = configs[0]; manager.ViewModel.CurrentConfig = configs[1]; manager.ViewModel.CurrentConfig = configs[0];
#endif
                _managerSwitches += 3;
                var folder = configs[0].SourceFolders[0];
#if PERFORMANCE_UI_PROFILING
                FolderRewind.Plugin.Abstractions.ConfigSnapshot snapshot;
                using (PerformanceUiProfiling.Measure("Preview.ToSnapshot")) snapshot = PluginV3ModelMapper.ToSnapshot(configs[0]);
                PluginV3SpatialPreview session;
                using (PerformanceUiProfiling.Measure("Preview.OpenWorld")) session = PluginV3SpatialPreview.OpenWorld(snapshot, Guid.Parse(folder.Id), "world");
#else
                var session = PluginV3SpatialPreview.OpenWorld(PluginV3ModelMapper.ToSnapshot(configs[0]), Guid.Parse(folder.Id), "world");
#endif
                var description = await session.DescribeAsync(default);
                var map = new SpatialPreviewCanvas();
                lock (Pages) Pages.Add(new WeakReference(map));
                mapWindow.Content = map;
                await Task.Delay(200);
                var before = PerformanceFakePreview.Statistics.Renders;
                map.SetSource(session, description.Layers[0], null, true);
                for (var i = 0; i < 6; i++) { map.Zoom(i % 2 == 0 ? 1.2 : 1 / 1.2); map.RestoreCamera(i * 20, i * 15, map.Camera.Scale); _mapMoves++; await Task.Delay(100); }
                await Task.Delay(400);
                if (PerformanceFakePreview.Statistics.Renders <= before) throw new InvalidOperationException("No preview tiles rendered.");
#if PERFORMANCE_UI_PROFILING
                PerformanceUiProfiling.MarkBoundary("Preview.Clear.Begin", window, mapWindow);
                using (PerformanceUiProfiling.Measure("Preview.Clear")) map.Clear();
                PerformanceUiProfiling.MarkBoundary("Preview.Clear.Returned", window, mapWindow);
                using (PerformanceUiProfiling.Measure("Preview.ContentDetach")) mapWindow.Content = new Grid();
                PerformanceUiProfiling.MarkBoundary("Preview.ContentDetach.Returned", window, mapWindow);
                using (PerformanceUiProfiling.MeasureElapsed("Preview.Close.ToAwaitCompletion"))
                {
                    Task closing;
                    using (PerformanceUiProfiling.Measure("Preview.Close.SyncStart")) closing = session.CloseAsync();
                    PerformanceUiProfiling.MarkBoundary("Preview.Close.SyncStart.Returned", window, mapWindow);
                    await closing;
                }
                PerformanceUiProfiling.MarkBoundary("Preview.Close.Completed", window, mapWindow);
#else
                map.Clear(); mapWindow.Content = new Grid();
                await session.CloseAsync();
#endif
#if PERFORMANCE_UI_PROFILING
                using (PerformanceUiProfiling.Measure("Tasks.Navigate"))
#endif
                if (!NavigationService.NavigateTo("Tasks")) throw new InvalidOperationException("Task page navigation failed.");
#if PERFORMANCE_UI_PROFILING
                PerformanceUiProfiling.ObserveNavigation("Tasks.Navigate", window, mapWindow);
#endif
                var task = new BackupTask { FolderName = "Synthetic task", Status = "synthetic" };
#if PERFORMANCE_UI_PROFILING
                using (PerformanceUiProfiling.Measure("Tasks.Insert"))
#endif
                BackupService.TaskRegistry.Insert(0, task);
#if PERFORMANCE_UI_PROFILING
                using (PerformanceUiProfiling.Measure("Tasks.Transitions"))
                {
#endif
                task.IsCompleted = true; task.IsCompleted = false; task.IsSuccess = true; task.IsCompleted = true;
#if PERFORMANCE_UI_PROFILING
                }
                PerformanceUiProfiling.MarkBoundary("Tasks.Transitions.Completed", window, mapWindow);
#endif
                _taskTransitions += 3;
                await Task.Delay(300);
#if PERFORMANCE_UI_PROFILING
                PerformanceUiProfiling.MarkBoundary("Tasks.Delay.Completed", window, mapWindow);
#endif
                _cycles++;
            }
            mapWindow.Close();
            await PerformanceFakePreview.DeactivateAsync();
#if PERFORMANCE_UI_PROFILING
            var drained = PerformanceFakePreview.Statistics;
            if (drained.Generations != 0) throw new InvalidOperationException("Profile exercise has not drained.");
            await File.WriteAllTextAsync(Path.Combine(_root!, "profile-drained.json"), JsonSerializer.Serialize(new
            { status = "OBSERVED", seconds = Clock.Elapsed.TotalSeconds, generations = drained.Generations,
                renders = drained.Renders, closes = drained.Closes, cycles = _cycles }));
#endif
        }
        catch (Exception e) { lock (Failures) Failures.Add("Exercise: " + e); File.WriteAllText(Path.Combine(_root!, "exercise-error.txt"), e.ToString()); }
#if PERFORMANCE_POST_RUN_ACCEPTANCE
        finally { ExerciseFinished = true; }
#endif
    }
    private static void VerifyPreviewCurrentness()
    {
        var config = ConfigService.CurrentConfig.BackupConfigs[0];
        var folder = config.SourceFolders[0];
        var id = Guid.Parse(folder.Id);
        var origin = PluginV3ModelMapper.ToSnapshot(config);
        void Check(bool expected, string description)
        {
            if (PluginV3SpatialPreview.IsCurrent(origin, id) != expected)
                throw new InvalidOperationException("Preview currentness: " + description);
        }
        var revision = config.ConfigRevision;
        var kind = config.Kind;
        var path = folder.Path;
        var folderId = folder.Id;
        try
        {
            Check(true, "unchanged");
            config.ConfigRevision = "different-revision"; Check(false, "revision change"); config.ConfigRevision = revision;
            config.Kind = new ConfigKindReference { OwnerId = "folderrewind.performance", KindId = "different" };
            Check(false, "kind change"); config.Kind = kind;
            folder.Path = path + "-changed"; Check(false, "path change"); folder.Path = path;
            folder.Id = folderId.ToUpperInvariant(); Check(true, "GUID formatting"); folder.Id = folderId;
            config.SourceFolders.RemoveAt(0); Check(false, "removed folder"); config.SourceFolders.Insert(0, folder);
            Check(true, "restored");
        }
        finally
        {
            config.ConfigRevision = revision; config.Kind = kind; folder.Path = path; folder.Id = folderId;
            if (!config.SourceFolders.Contains(folder)) config.SourceFolders.Insert(0, folder);
        }
        File.WriteAllText(Path.Combine(_root!, "preview-currentness.txt"), "PASS");
    }
    private static IEnumerable<T> Find<T>(object? root) where T : DependencyObject
    {
        if (root is not DependencyObject node) yield break;
        if (node is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var item in Find<T>(VisualTreeHelper.GetChild(node, i))) yield return item;
    }
}
#endif

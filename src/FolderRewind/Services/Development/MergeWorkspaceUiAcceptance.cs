#if SPATIAL_WORKSPACE_UI_ACCEPTANCE
using FolderRewind.Models;
using FolderRewind.Services.Plugins.V3;
using FolderRewind.Views;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FolderRewind.Services.Development;

// The driver operates the actual pages. This observer records their real state and
// awaits their normal disposal; it does not initialize the VM with substitute inputs.
internal static class MergeWorkspaceUiAcceptance
{
    private static string? _root;
    private static readonly ConcurrentQueue<object> Events = new();

    internal static void Prepare()
    {
        var data = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_DATA_ROOT");
        if (!AppRuntimeInfo.IsMsiDistribution || string.IsNullOrWhiteSpace(data) || !Path.IsPathFullyQualified(data))
            throw new InvalidOperationException("Merge UI acceptance requires an absolute isolated MSI data root.");
        var full = Path.GetFullPath(data);
        var root = Directory.GetParent(full)?.FullName;
        if (root is null || !string.Equals(Path.GetFileName(full), "appdata", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(root, "acceptance-owner")) || !File.Exists(Path.Combine(root, "merge-ui-seed.json"))
            || File.ReadAllText(Path.Combine(root, "acceptance-owner")).Trim() != "native-artifact-v2")
            throw new InvalidOperationException("Merge UI acceptance requires a marked seeded workspace.");
        var config = ConfigService.CurrentConfig.BackupConfigs.Single();
        if (!Inside(config.DestinationPath, root) || !Inside(config.SourceFolders.Single().Path, root)
            || !Inside(ConfigService.ConfigDirectory, root))
            throw new InvalidOperationException("Merge UI fixture paths must remain inside the marked workspace.");
        _root = root;
    }

    private static bool Inside(string path, string root) => Path.IsPathFullyQualified(path)
        && Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    internal static void Start(Window window)
    {
        if (_root is not null) window.DispatcherQueue.TryEnqueue(() => TaskObserver.Observe(RunAsync(window), nameof(MergeWorkspaceUiAcceptance)));
    }

    internal static void ObserveSpatial(SpatialWorkspacePage page)
    {
        if (_root is null) return;
        page.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(page.ViewModel.Status))
                Events.Enqueue(new { kind = "spatial-ui", page.ViewModel.Status, page.ViewModel.IsBusy, page.ViewModel.Error });
        };
    }

    private static async Task RunAsync(Window window)
    {
        var releases = new List<object>();
        UiDecisionSnapshot? decisionBefore = null;
        string? decisionId = null;
        try
        {
            var config = ConfigService.CurrentConfig.BackupConfigs.Single();
            await WaitAsync(() => PluginV3SemanticMerge.Options(config).Count > 0, "Minecraft plugin activation", 120);
            if (!NavigationService.NavigateTo("Merge", new MergeNavigationParameter(config.Id, config.SourceFolders.Single().Id)))
                throw new InvalidOperationException("Merge page navigation failed.");
            await WaitAsync(() => Find<MergePage>(window.Content).Any(p => p.ViewModel.CanAnalyze && p.ViewModel.Methods.Count > 1), "Merge setup", 90);
            var page = Find<MergePage>(window.Content).Single();
            var semantic = page.ViewModel.Methods.Single(m => m.Binding?.PolicyId == "terrain-first");
            page.ViewModel.SelectedMethod = semantic;
            page.ViewModel.Notify();
            var tracker = page.ViewModel.Operations!.Tracker;
            void OnTracker()
            {
                var s = tracker.Snapshot;
                if (s.Progress is { } progress) Events.Enqueue(new { kind = "progress", stage = progress.Stage.ToString(), progress.Completed, progress.Total });
                if (s.Notice == "Merge_Cancelled") Events.Enqueue(new { kind = "canceled", s.IsBusy, s.Notice });
            }
            tracker.Changed += OnTracker;
            void OnUi(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                var vm = page.ViewModel;
                if (vm.State.Progress is { } progress) Events.Enqueue(new { kind = "merge-ui", stage = progress.Stage.ToString(), vm.Status, vm.CanStop });
            }
            page.ViewModel.PropertyChanged += OnUi;
            await WriteAsync("merge-ui-ready.json", new { status = "READY", semantic = semantic.Binding!.PolicyId, page.ViewModel.CanAnalyze });
            SpatialWorkspacePage? previous = null;
            var visits = 0;
            var readyWritten = false;
            var decisionRegression = Environment.GetEnvironmentVariable("FOLDERREWIND_MERGE_DECISION_UI") == "1";
            var decisionStep = 0;
            var nextMapSample = DateTime.UtcNow;
            var deadline = DateTime.UtcNow.AddMinutes(20);
            while (!File.Exists(Path.Combine(_root!, "merge-ui-finish.request")))
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("UI driver did not finish.");
                var spatial = Find<SpatialWorkspacePage>(window.Content).FirstOrDefault();
                if (spatial is not null && !ReferenceEquals(spatial, previous))
                {
                    if (previous is not null) await ReleaseAsync(previous);
                    previous = spatial;
                    visits++; readyWritten = false;
                    nextMapSample = DateTime.UtcNow.AddSeconds(5);
                    Events.Enqueue(new { kind = "spatial-open", time = DateTimeOffset.UtcNow });
                    await WriteAsync($"merge-spatial-open-{visits}.json", new { status = "OPEN" });
                }
                else if (spatial is null && previous is not null) { await ReleaseAsync(previous); previous = null; }
                if (spatial is not null && !readyWritten && DateTime.UtcNow >= nextMapSample)
                {
                    var sample = spatial.UiAcceptanceMapActivity();
                    await WriteAsync($"merge-spatial-activity-{visits}-{DateTime.UtcNow:HHmmssfff}.json", sample);
                    nextMapSample = DateTime.UtcNow.AddSeconds(10);
                }
                if (spatial is not null && !readyWritten && !string.IsNullOrEmpty(spatial.ViewModel.Error))
                    throw new InvalidOperationException("Spatial UI failed: " + spatial.ViewModel.Error);
                if (spatial is not null && !readyWritten && !spatial.ViewModel.HasActivity && spatial.ViewModel.CanEdit && spatial.UiAcceptanceOverlayDrained)
                {
                    readyWritten = true;
                    object? reopened = null;
                    if (decisionRegression && visits == 3 && decisionBefore is not null)
                    {
                        var current = spatial.ViewModel.UiAcceptanceDecisionSnapshot();
                        reopened = new { sameIndex = ReferenceEquals(current.Index, decisionBefore.Index),
                            sameBuild = ReferenceEquals(current.Build, decisionBefore.Build), current.Directory,
                            current.Revision, current.IndexedRevision, current.Selected };
                    }
                    await WriteAsync($"merge-spatial-ready-{visits}.json", new { status = "READY", textStatus = spatial.ViewModel.Status,
                        rows = spatial.ViewModel.UiAcceptanceIndexedCount(), spatial.ViewModel.IsMerge,
                        mapActivity = spatial.UiAcceptanceMapActivity(), reopened });
                }
                if (decisionRegression && visits == 2 && spatial is not null && readyWritten && spatial.ViewModel.CanDecide
                    && spatial.ViewModel.UiAcceptanceState().RowsCurrent)
                {
                    var stepNames = new[] { "before", "selected", "decided", "reselected", "cleared" };
                    if (decisionStep < stepNames.Length && File.Exists(Path.Combine(_root!, $"decision-{stepNames[decisionStep]}.request")))
                    {
                        var current = spatial.ViewModel.UiAcceptanceDecisionSnapshot();
                        if (decisionStep == 0)
                        {
                            var candidate = current.Rows.FirstOrDefault(row => row.Status == "Automatic" && row.CanChooseOurs);
                            if (candidate is not null && current.Selected.Length == 0)
                            { decisionBefore = current; decisionId = candidate.Id; await WriteDecisionAsync(stepNames[decisionStep++], current); }
                        }
                        else if (decisionBefore is not null && decisionId is not null)
                        {
                            var row = current.Rows.FirstOrDefault(item => item.Id == decisionId);
                            var expectedRevision = decisionBefore.Revision + (decisionStep >= 2 ? decisionStep >= 4 ? 2 : 1 : 0);
                            var expectedStatus = decisionStep is 2 or 3 ? "Resolved" : "Automatic";
                            var expectedSelected = decisionStep is 1 or 3;
                            if (current.Revision == expectedRevision && current.IndexedRevision == expectedRevision && row is not null
                                && row.Status == expectedStatus && current.Selected.Length == (expectedSelected ? 1 : 0)
                                && (!expectedSelected || current.Selected[0] == decisionId))
                                await WriteDecisionAsync(stepNames[decisionStep++], current);
                        }
                    }
                }
                await Task.Delay(50);
            }
            if (previous is not null) throw new InvalidOperationException("Driver finished before leaving the spatial page.");
            tracker.Changed -= OnTracker;
            page.ViewModel.PropertyChanged -= OnUi;
            await WriteAsync("merge-ui-observation.json", new { status = "OBSERVED", events = Events.ToArray(), releases });
            await WaitAsync(() => File.Exists(Path.Combine(_root!, "merge-ui-exit.request")), "Driver exit request", 120);
            await App.ExitApplicationAsync();
        }
        catch (Exception error) { await WriteAsync("merge-ui-error.json", new { status = "FAIL", error = error.ToString(), events = Events.ToArray(), releases }); }

        async Task ReleaseAsync(SpatialWorkspacePage old)
        {
            await old.ViewModel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            var merge = old.ViewModel.UiAcceptanceMergeRelease();
            var release = new { indexDisposed = old.ViewModel.UiAcceptanceIndexDisposed, overlayDrained = old.UiAcceptanceOverlayDrained,
                published = merge.Published, rootExists = merge.RootExists };
            releases.Add(release);
            await WriteAsync($"merge-spatial-release-{releases.Count}.json", release);
        }

        async Task WriteDecisionAsync(string name, UiDecisionSnapshot current)
        {
            var baseline = decisionBefore!;
            await WriteAsync($"decision-{name}.json", new { status = "OBSERVED", target = decisionId,
                sameIndex = ReferenceEquals(current.Index, baseline.Index), sameBuild = ReferenceEquals(current.Build, baseline.Build),
                current.Directory, current.Session, current.Plan, current.Revision, current.IndexedRevision, current.Generation,
                current.Counts, current.Summary, current.Selected, current.Rows });
        }
    }

    private static async Task WaitAsync(Func<bool> condition, string label, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow >= until) throw new TimeoutException(label); await Task.Delay(100); }
    }
    private static async Task WriteAsync(string name, object value)
    {
        var target = Path.Combine(_root!, name);
        var temporary = target + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value));
        File.Move(temporary, target);
    }
    private static IEnumerable<T> Find<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) yield break;
        if (root is T result) yield return result;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
#endif

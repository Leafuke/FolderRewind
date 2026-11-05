#if PERFORMANCE_POST_RUN_ACCEPTANCE
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using FolderRewind.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace FolderRewind.Services.Development;

// Opt-in post-measurement diagnostic; never runs inside the timed workload.
internal static class PerformanceUiPostRunAcceptance
{
    internal static void Start(Window window)
    {
        if (Environment.GetEnvironmentVariable("FOLDERREWIND_UI_POST_GC") != "1") return;
        var data = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_DATA_ROOT");
        if (!AppRuntimeInfo.IsMsiDistribution || Environment.GetEnvironmentVariable("FOLDERREWIND_PERFORMANCE_UI") != "1" ||
            string.IsNullOrWhiteSpace(data) || !Path.IsPathFullyQualified(data))
            throw new InvalidOperationException("Post-run diagnostic requires an isolated performance fixture.");
        var full = Path.GetFullPath(data);
        var root = Directory.GetParent(full)?.FullName;
        if (root is null || !File.Exists(Path.Combine(root, "acceptance-owner")) ||
            !string.Equals(Path.GetFileName(full), "appdata", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Post-run diagnostic requires a marked fixture root.");
        _ = window.DispatcherQueue.TryEnqueue(() => TaskObserver.Observe(RunAsync(window, root), nameof(PerformanceUiPostRunAcceptance)));
    }
    private static async Task RunAsync(Window window, string root)
    {
        try
        {
            // Leave the measured interval untouched; begin observing only after it ends.
            await Task.Delay(TimeSpan.FromSeconds(605));
            // A separate marker ensures the caller has observed completion of the timed run.
            var deadline = DateTime.UtcNow.AddMinutes(15);
            while ((!File.Exists(Path.Combine(root, "post-gc.request")) ||
                    !File.Exists(Path.Combine(root, "results.json")) || !PerformanceUiAcceptance.ExerciseFinished)
                   && DateTime.UtcNow < deadline) await Task.Delay(250);
            if (!File.Exists(Path.Combine(root, "post-gc.request")) || !File.Exists(Path.Combine(root, "results.json")) ||
                !PerformanceUiAcceptance.ExerciseFinished) throw new InvalidOperationException("Timed workload did not finish and drain.");
            if (File.Exists(Path.Combine(root, "exercise-error.txt"))) throw new InvalidOperationException("Exercise failed during shutdown.");
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "results.json")));
            if (result.RootElement.GetProperty("failures").GetArrayLength() != 0)
                throw new InvalidOperationException("Timed workload reported failures.");
            if (!NavigationService.NavigateTo("Home")) throw new InvalidOperationException("Could not leave the exercised page.");
            await Task.Delay(1500);
            if (!ContainsHome(window.Content)) throw new InvalidOperationException("Home was not reached after the timed workload.");
            var statistics = PerformanceFakePreview.Statistics;
            var before = PerformanceUiAcceptance.CountTrackedTargets();
            var rounds = new List<UiManagerGcRound>();
            for (int round = 1; round <= 3; round++)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                await Task.Delay(150); // Allow XAML/WinRT release work to run on the UI queue.
                var observed = PerformanceUiAcceptance.CountTrackedTargets();
                rounds.Add(new(round, observed.Total, observed.Managers, observed.Canvases));
            }
            var evidence = new UiPostRunEvidence(PerformanceUiAcceptance.TrackedReferenceCount, statistics.Generations, rounds.ToArray());
            await WriteJsonAsync(root, "post-gc-observation.json", new
            {
                status = "OBSERVED", homeReached = true, trackedReferences = evidence.TrackedReferences,
                before = new { before.Total, before.Managers, before.Canvases }, rounds = evidence.Rounds,
                provider = new { statistics.Generations, statistics.Renders, statistics.Closes },
                note = "Deduplicated weak targets only; this is not a heap-wide object census."
            });
            var acceptance = UiAcceptanceCriteria.EvaluatePostRun(evidence);
            await WriteJsonAsync(root, "post-gc-results.json", new
            { status = acceptance.Status, category = "lifecycle", collection = "OBSERVED", acceptance.Failures });
        }
        catch (Exception error)
        {
            await WriteJsonAsync(root, "post-gc-observation.json", new { status = "ERROR", error = error.ToString() });
            await WriteJsonAsync(root, "post-gc-results.json", new
            { status = "FAIL", category = "lifecycle", collection = "ERROR", failures = new[] { error.ToString() } });
        }
    }
    private static bool ContainsHome(object? root)
    {
        if (root is not DependencyObject node) return false;
        if (node is HomePage) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (ContainsHome(VisualTreeHelper.GetChild(node, i))) return true;
        return false;
    }
    private static async Task WriteJsonAsync(string root, string name, object value)
    {
        var path = Path.Combine(root, name);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}
#endif

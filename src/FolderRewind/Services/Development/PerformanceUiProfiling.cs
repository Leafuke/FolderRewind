#if PERFORMANCE_UI_PROFILING
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Development;

// Opt-in diagnosis only. Timed phases append value records; all file I/O runs after 60 seconds on a worker.
internal static partial class PerformanceUiProfiling
{
    private readonly record struct Sample(string Phase, int Cycle, int Thread, bool IsUi,
        double StartSeconds, double DurationMs, long AllocatedBytes);
    private static readonly ConcurrentQueue<Sample> Samples = new();
    private static long _started;
    private static int _uiThread, _cycle;
    private static bool _recording, _stopExercise;
    internal static bool StopExercise => Volatile.Read(ref _stopExercise);

    internal static void Start(string root, DispatcherQueue queue)
    {
        if (Environment.GetEnvironmentVariable("FOLDERREWIND_UI_PROFILING") != "1") return;
        if (!AppRuntimeInfo.IsMsiDistribution || !File.Exists(Path.Combine(root, "acceptance-owner")))
            throw new InvalidOperationException("UI profiling requires a marked MSI performance fixture.");
        _started = Stopwatch.GetTimestamp();
        _uiThread = Environment.CurrentManagedThreadId;
        Volatile.Write(ref _recording, true);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                Volatile.Write(ref _recording, false);
                Volatile.Write(ref _stopExercise, true);
                await StopNavigationObservationsAsync(queue).ConfigureAwait(false);
                var rows = Samples.ToArray();
                var csv = new StringBuilder("start_s,cycle,phase,thread_id,is_ui,duration_ms,allocated_bytes\n");
                foreach (var sample in rows)
                    csv.Append(CultureInfo.InvariantCulture, $"{sample.StartSeconds:F6},{sample.Cycle},{sample.Phase},{sample.Thread},{sample.IsUi},{sample.DurationMs:F6},{sample.AllocatedBytes}\n");
                await File.WriteAllTextAsync(Path.Combine(root, "ui-profile.csv"), csv.ToString()).ConfigureAwait(false);
                await WriteNavigationObservationsAsync(root).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(root, "profile-results.json"), JsonSerializer.Serialize(new
                {
                    status = ObservationErrors.IsEmpty ? "OBSERVED" : "ERROR", seconds = Stopwatch.GetElapsedTime(_started).TotalSeconds,
                    samples = rows.Length, cycles = Volatile.Read(ref _cycle),
                    phases = rows.Select(row => row.Phase).Distinct().ToArray(),
                    windowSamples = WindowStates.Count, navigationSignals = NavigationSignals.Count,
                    missingNavigationSignals = NavigationSignals.Count(row => row.Status != "observed"),
                    observationErrors = ObservationErrors.ToArray(),
                    note = "Diagnostic observations, not a performance acceptance verdict or a normal ten-minute comparison. Navigation signals run alongside the workload without awaiting layout or rendering; LayoutUpdated is for the main root, Rendering is application-wide and does not prove presentation. Missing signals are marked unavailable or censored at supersession/cutoff. Window state is sampled on the UI thread, without activation or hooks; IsWindowVisible does not establish absence of occlusion. Allocated bytes -1 means no allocation measurement (elapsed spans, markers and queue/event observations)."
                }, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                await File.WriteAllTextAsync(Path.Combine(root, "profile-results.json"), JsonSerializer.Serialize(new
                { status = "ERROR", error = error.ToString() })).ConfigureAwait(false);
            }
        });
    }
    internal static void BeginCycle(int cycle) => Volatile.Write(ref _cycle, cycle);
    internal static Scope Measure(string phase) => Volatile.Read(ref _recording) ? new(phase) : default;
    internal static Scope MeasureElapsed(string phase) => Volatile.Read(ref _recording) ? new(phase, false) : default;

    internal readonly struct Scope : IDisposable
    {
        private readonly string? _phase;
        private readonly int _cycle, _thread;
        private readonly long _start, _allocated;
        internal Scope(string phase, bool measureAllocation = true)
        {
            _phase = phase; _cycle = Volatile.Read(ref PerformanceUiProfiling._cycle);
            _thread = Environment.CurrentManagedThreadId;
            _allocated = measureAllocation ? GC.GetAllocatedBytesForCurrentThread() : -1; _start = Stopwatch.GetTimestamp();
        }
        public void Dispose()
        {
            if (_phase is null || !Volatile.Read(ref _recording)) return;
            var elapsed = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            var thread = Environment.CurrentManagedThreadId;
            var allocated = _allocated >= 0 && thread == _thread ? GC.GetAllocatedBytesForCurrentThread() - _allocated : -1;
            Samples.Enqueue(new(_phase, _cycle, thread, thread == _uiThread,
                Stopwatch.GetElapsedTime(_started, _start).TotalSeconds, elapsed, allocated));
        }
    }
}
#endif

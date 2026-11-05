#if PERFORMANCE_UI_PROFILING
using FolderRewind.Models;
using FolderRewind.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinRT.Interop;

namespace FolderRewind.Services.Development;

internal static partial class PerformanceUiProfiling
{
    private readonly record struct NavigationSignal(double QueuedSeconds, double ObservedSeconds, int Cycle,
        string Phase, string Signal, string Status, string RootType, int Thread, bool IsUi, double DurationMs);
    private readonly record struct WindowState(double Seconds, int Cycle, string Phase, int Thread, bool IsUi,
        long Foreground, uint ForegroundProcess, bool AppForeground,
        long Main, bool MainVisible, bool MainIconic, bool MainForeground,
        long Preview, bool PreviewVisible, bool PreviewIconic, bool PreviewForeground);
    private readonly record struct TaskProgressState(double Seconds, int Cycle, string Phase, int Thread, bool IsUi,
        string Status, bool PageLoaded, bool ListLoaded, int Tasks, int CompletedTasks, int CompletedRawIndeterminateTasks,
        int RealizedContainers, int ProgressBars, int LoadedProgressBars, int LocallyVisibleProgressBars,
        int IndeterminateProgressBars, int CompletedIndeterminateProgressBars, int RunningIndeterminateProgressBars,
        int UnknownTaskProgressBars, double ObservationMs);
    private static readonly ConcurrentQueue<NavigationSignal> NavigationSignals = new();
    private static readonly ConcurrentQueue<WindowState> WindowStates = new();
    private static readonly ConcurrentQueue<TaskProgressState> TaskProgressStates = new();
    private static readonly ConcurrentQueue<string> ObservationErrors = new();
    // UI-thread-only; at most one outstanding observation for each of the two navigation phases.
    private static readonly Dictionary<string, NavigationObservation> PendingNavigation = new(StringComparer.Ordinal);

    internal static void MarkBoundary(string phase, Window main, Window preview)
    {
        if (!Volatile.Read(ref _recording)) return;
        var at = Stopwatch.GetTimestamp();
        var cycle = Volatile.Read(ref _cycle);
        var thread = Environment.CurrentManagedThreadId;
        Samples.Enqueue(new(phase, cycle, thread, thread == _uiThread,
            Stopwatch.GetElapsedTime(_started, at).TotalSeconds, 0, -1));
        CaptureWindowState(phase, cycle, main, preview);
        if (phase == "Tasks.Delay.Completed") CaptureTaskProgressState(phase, cycle, main);
    }

    internal static void ObserveNavigation(string phase, Window main, Window preview)
    {
        if (!Volatile.Read(ref _recording)) return;
        MarkBoundary(phase + ".Returned", main, preview);
        if (PendingNavigation.TryGetValue(phase, out var previous)) previous.Stop("superseded");
        var observation = new NavigationObservation(phase, Volatile.Read(ref _cycle), main, preview);
        PendingNavigation.Add(phase, observation);
        observation.Start();
    }

    private static void CaptureWindowState(string phase, int cycle, Window main, Window preview)
    {
        var thread = Environment.CurrentManagedThreadId;
        if (thread != _uiThread) throw new InvalidOperationException("Window profiling must run on the UI thread.");
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var foregroundProcess);
        var mainHandle = WindowNative.GetWindowHandle(main);
        var previewHandle = WindowNative.GetWindowHandle(preview);
        WindowStates.Enqueue(new(Stopwatch.GetElapsedTime(_started).TotalSeconds, cycle, phase, thread, true,
            foreground.ToInt64(), foregroundProcess, foreground != IntPtr.Zero && foregroundProcess == (uint)Environment.ProcessId,
            mainHandle.ToInt64(), IsWindowVisible(mainHandle), IsIconic(mainHandle), mainHandle != IntPtr.Zero && foreground == mainHandle,
            previewHandle.ToInt64(), IsWindowVisible(previewHandle), IsIconic(previewHandle), previewHandle != IntPtr.Zero && foreground == previewHandle));
    }

    private static void CaptureTaskProgressState(string phase, int cycle, Window main)
    {
        if (!Volatile.Read(ref _recording)) return;
        var started = Stopwatch.GetTimestamp();
        var thread = Environment.CurrentManagedThreadId;
        var status = "observed";
        var pageLoaded = false;
        var listLoaded = false;
        var tasks = 0;
        var completed = 0;
        var completedRawIndeterminate = 0;
        var containers = 0;
        var bars = 0;
        var loadedBars = 0;
        var visibleBars = 0;
        var indeterminateBars = 0;
        var completedIndeterminateBars = 0;
        var runningIndeterminateBars = 0;
        var unknownTaskBars = 0;
        try
        {
            if (thread != _uiThread) throw new InvalidOperationException("Task progress profiling must run on the UI thread.");
            var page = FindVisual<BackupTasksPage>(main.Content as DependencyObject);
            var list = FindVisual<ListView>(page);
            if (page is null || list is null || list.Name != "TaskList") status = "unavailable";
            else
            {
                pageLoaded = page.IsLoaded;
                listLoaded = list.IsLoaded;
                tasks = page.ViewModel.TasksView.Count;
                foreach (var item in page.ViewModel.TasksView)
                    if (item is BackupTask { IsCompleted: true } task)
                    {
                        completed++;
                        if (task.IsIndeterminate) completedRawIndeterminate++;
                    }
                Visit(list, null);
            }
        }
        catch (Exception error)
        {
            status = "error";
            ObservationErrors.Enqueue(phase + ".TaskProgress: " + error);
        }
        // Value records only: the observer must not extend a page/container's lifetime.
        TaskProgressStates.Enqueue(new(Stopwatch.GetElapsedTime(_started, started).TotalSeconds, cycle, phase,
            thread, thread == _uiThread, status, pageLoaded, listLoaded, tasks, completed, completedRawIndeterminate,
            containers, bars, loadedBars, visibleBars, indeterminateBars, completedIndeterminateBars,
            runningIndeterminateBars, unknownTaskBars, Stopwatch.GetElapsedTime(started).TotalMilliseconds));

        void Visit(DependencyObject node, BackupTask? task)
        {
            if (node is FrameworkElement { DataContext: BackupTask context }) task = context;
            if (node is ListViewItem container)
            {
                containers++;
                if (container.Content is BackupTask item) task = item;
            }
            if (node is ProgressBar bar)
            {
                bars++;
                if (bar.IsLoaded) loadedBars++;
                // Local visibility/size does not establish viewport intersection or absence of occlusion.
                if (bar.IsLoaded && bar.Visibility == Visibility.Visible && bar.ActualWidth > 0 && bar.ActualHeight > 0) visibleBars++;
                if (task is null) unknownTaskBars++;
                if (bar.IsIndeterminate)
                {
                    indeterminateBars++;
                    if (task?.IsCompleted == true) completedIndeterminateBars++;
                    else if (task is not null) runningIndeterminateBars++;
                }
            }
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                Visit(VisualTreeHelper.GetChild(node, index), task);
        }
    }

    private static T? FindVisual<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) return null;
        if (root is T match) return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(root, index)) is { } child) return child;
        return null;
    }

    private sealed class NavigationObservation(string phase, int cycle, Window main, Window preview)
    {
        private const int Normal = 1, Low = 2, Layout = 4, Render = 8;
        private readonly long _queued = Stopwatch.GetTimestamp();
        private readonly FrameworkElement? _root = main.Content as FrameworkElement;
        private int _pending = Normal | Low | Layout | Render;
        private bool _stopped;

        internal void Start()
        {
            if (_root is not null) _root.LayoutUpdated += OnLayout;
            else Record(Layout, "NextRootLayout", "unavailable");
            CompositionTarget.Rendering += OnRender;
            if (!main.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () => Observe(Normal, "QueueNormal")) ||
                !main.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Observe(Low, "QueueLow")))
            {
                ObservationErrors.Enqueue(phase + ": dispatcher rejected a navigation observation.");
                Stop("error");
            }
        }

        private void OnLayout(object? sender, object args) => Observe(Layout, "NextRootLayout");
        private void OnRender(object? sender, object args) => Observe(Render, "NextAppRender");

        private void Observe(int signal, string name)
        {
            if (_stopped || (_pending & signal) == 0 || !Volatile.Read(ref _recording)) return;
            try
            {
                Record(signal, name, "observed");
                if (signal == Layout && _root is not null) _root.LayoutUpdated -= OnLayout;
                if (signal == Render) CompositionTarget.Rendering -= OnRender;
                CaptureWindowState(phase + "." + name, cycle, main, preview);
                if (phase == "Tasks.Navigate" && (signal == Layout || signal == Render))
                    CaptureTaskProgressState(phase + "." + name, cycle, main);
                if (_pending == 0) Stop("complete");
            }
            catch (Exception error)
            {
                ObservationErrors.Enqueue(phase + "." + name + ": " + error);
                Stop("error");
            }
        }

        private void Record(int signal, string name, string status)
        {
            _pending &= ~signal;
            var observed = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_queued, observed).TotalMilliseconds;
            var thread = Environment.CurrentManagedThreadId;
            NavigationSignals.Enqueue(new(Stopwatch.GetElapsedTime(_started, _queued).TotalSeconds,
                Stopwatch.GetElapsedTime(_started, observed).TotalSeconds, cycle, phase, name, status,
                _root?.GetType().Name ?? string.Empty, thread, thread == _uiThread, elapsed));
            if (status == "observed")
                Samples.Enqueue(new(phase + "." + name, cycle, thread, thread == _uiThread,
                    Stopwatch.GetElapsedTime(_started, _queued).TotalSeconds, elapsed, -1));
        }

        internal void Stop(string status)
        {
            if (_stopped) return;
            _stopped = true;
            if (_root is not null) _root.LayoutUpdated -= OnLayout;
            CompositionTarget.Rendering -= OnRender;
            if ((_pending & Normal) != 0) Record(Normal, "QueueNormal", status);
            if ((_pending & Low) != 0) Record(Low, "QueueLow", status);
            if ((_pending & Layout) != 0) Record(Layout, "NextRootLayout", status);
            if ((_pending & Render) != 0) Record(Render, "NextAppRender", status);
            if (PendingNavigation.TryGetValue(phase, out var current) && ReferenceEquals(current, this))
                PendingNavigation.Remove(phase);
        }
    }

    private static async Task StopNavigationObservationsAsync(DispatcherQueue queue)
    {
        // Only the diagnostic writer waits for cleanup. The exercise never awaits these observations.
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() =>
        {
            try
            {
                foreach (var observation in PendingNavigation.Values.ToArray()) observation.Stop("cutoff");
                finished.TrySetResult();
            }
            catch (Exception error) { finished.TrySetException(error); }
        })) throw new InvalidOperationException("Dispatcher rejected navigation profiling cleanup.");
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    private static async Task WriteNavigationObservationsAsync(string root)
    {
        var navigation = new StringBuilder("queued_s,observed_s,cycle,phase,signal,status,root_type,thread_id,is_ui,queue_to_signal_ms\n");
        foreach (var row in NavigationSignals)
            navigation.Append(CultureInfo.InvariantCulture, $"{row.QueuedSeconds:F6},{row.ObservedSeconds:F6},{row.Cycle},{row.Phase},{row.Signal},{row.Status},{row.RootType},{row.Thread},{row.IsUi},{row.DurationMs:F6}\n");
        await File.WriteAllTextAsync(Path.Combine(root, "ui-profile-navigation.csv"), navigation.ToString()).ConfigureAwait(false);
        var windows = new StringBuilder("at_s,cycle,phase,thread_id,is_ui,foreground_hwnd,foreground_pid,app_foreground,main_hwnd,main_visible,main_iconic,main_foreground,preview_hwnd,preview_visible,preview_iconic,preview_foreground\n");
        foreach (var row in WindowStates)
            windows.Append(CultureInfo.InvariantCulture, $"{row.Seconds:F6},{row.Cycle},{row.Phase},{row.Thread},{row.IsUi},{row.Foreground},{row.ForegroundProcess},{row.AppForeground},{row.Main},{row.MainVisible},{row.MainIconic},{row.MainForeground},{row.Preview},{row.PreviewVisible},{row.PreviewIconic},{row.PreviewForeground}\n");
        await File.WriteAllTextAsync(Path.Combine(root, "ui-profile-window-state.csv"), windows.ToString()).ConfigureAwait(false);
        var progress = new StringBuilder("at_s,cycle,phase,thread_id,is_ui,status,page_loaded,list_loaded,tasks,completed_tasks,completed_raw_indeterminate_tasks,realized_containers,progress_bars,loaded_progress_bars,locally_visible_progress_bars,indeterminate_progress_bars,completed_indeterminate_progress_bars,running_indeterminate_progress_bars,unknown_task_progress_bars,observation_ms\n");
        foreach (var row in TaskProgressStates)
            progress.Append(CultureInfo.InvariantCulture, $"{row.Seconds:F6},{row.Cycle},{row.Phase},{row.Thread},{row.IsUi},{row.Status},{row.PageLoaded},{row.ListLoaded},{row.Tasks},{row.CompletedTasks},{row.CompletedRawIndeterminateTasks},{row.RealizedContainers},{row.ProgressBars},{row.LoadedProgressBars},{row.LocallyVisibleProgressBars},{row.IndeterminateProgressBars},{row.CompletedIndeterminateProgressBars},{row.RunningIndeterminateProgressBars},{row.UnknownTaskProgressBars},{row.ObservationMs:F6}\n");
        await File.WriteAllTextAsync(Path.Combine(root, "ui-profile-task-progress.csv"), progress.ToString()).ConfigureAwait(false);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
}
#endif

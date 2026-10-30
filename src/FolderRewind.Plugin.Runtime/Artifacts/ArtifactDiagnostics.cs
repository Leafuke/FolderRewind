using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace FolderRewind.Plugin.Runtime.Artifacts;

/// <summary>Opt-in stage telemetry; no filesystem paths or source content in tags.</summary>
public static class ArtifactDiagnostics
{
    public static readonly ActivitySource Source = new("FolderRewind.Artifacts");
    private static readonly ConcurrentDictionary<int, Process> Processes = new();
    public static IDisposable? ObserveProcess(Process process, string operation)
        => Source.HasListeners() ? new ProcessObservation(process, operation) : null;
    public static long SampleChildWorkingSet()
    {
        long total = 0;
        foreach (var process in Processes.Values)
            try { process.Refresh(); total += process.WorkingSet64; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return total;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
    private sealed class ProcessObservation : IDisposable
    {
        private readonly Process _process;
        private readonly Activity? _activity;
        public ProcessObservation(Process process, string operation)
        { _process = process; Processes[process.Id] = process; _activity = Source.StartActivity("process." + operation); }
        public void Dispose()
        {
            try
            {
                if (OperatingSystem.IsWindows() && GetProcessIoCounters(_process.Handle, out var io))
                { _activity?.SetTag("child.read.bytes", checked((long)io.ReadBytes)); _activity?.SetTag("child.write.bytes", checked((long)io.WriteBytes)); }
            }
            finally { Processes.TryRemove(_process.Id, out _); _activity?.Dispose(); }
        }
    }
}

using System;
using System.Diagnostics;
using System.Threading;

namespace FolderRewind.Services.Diagnostics
{
    public static class MemoryTelemetryService
    {
        private static Timer? _timer;
        private static long _peakWorkingSet;
        private static long _peakManagedMemory;
        private static readonly Stopwatch _uptime = Stopwatch.StartNew();

        public static bool IsEnabled { get; private set; }

        public static void Start()
        {
#if DEBUG
            IsEnabled = true;
#else
            IsEnabled = ConfigService.CurrentConfig?.GlobalSettings?.EnablePerformanceTelemetry == true;
#endif
            if (!IsEnabled) return;

            _timer = new Timer(_ =>
            {
                try
                {
                    using var process = Process.GetCurrentProcess();
                    var workingSet = process.WorkingSet64;
                    var managedMemory = GC.GetTotalMemory(false);

                    InterlockedExchangeMax(ref _peakWorkingSet, workingSet);
                    InterlockedExchangeMax(ref _peakManagedMemory, managedMemory);

                    var uptime = _uptime.Elapsed;
                    LogService.Log(
                        $"[Telemetry] Uptime={uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2} " +
                        $"WS={workingSet / 1024 / 1024}MB " +
                        $"GC={managedMemory / 1024 / 1024}MB " +
                        $"PeakWS={_peakWorkingSet / 1024 / 1024}MB " +
                        $"PeakGC={_peakManagedMemory / 1024 / 1024}MB " +
                        $"Gen0={GC.CollectionCount(0)} " +
                        $"Gen1={GC.CollectionCount(1)} " +
                        $"Gen2={GC.CollectionCount(2)}");
                }
                catch { }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        public static void Stop()
        {
            _timer?.Dispose();
            _timer = null;
            IsEnabled = false;
        }

        public static void LogSnapshot(string label)
        {
            if (!IsEnabled) return;

            try
            {
                using var process = Process.GetCurrentProcess();
                var workingSet = process.WorkingSet64;
                var managedMemory = GC.GetTotalMemory(false);
                LogService.Log(
                    $"[Telemetry:Snapshot] {label} " +
                    $"WS={workingSet / 1024 / 1024}MB " +
                    $"GC={managedMemory / 1024 / 1024}MB");
            }
            catch { }
        }

        private static void InterlockedExchangeMax(ref long target, long value)
        {
            long snapshot;
            do
            {
                snapshot = target;
            }
            while (value > snapshot &&
                Interlocked.CompareExchange(ref target, value, snapshot) != snapshot);
        }
    }
}

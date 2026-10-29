using System.Diagnostics;
using System.Threading;

namespace FolderRewind.Services;

internal static class StartupTimingService
{
    private static long _started;
    private static int _homeReported;
    public static void Start() => _started = Stopwatch.GetTimestamp();
    public static void Mark(string stage) => LogService.LogInfo(
        $"[Startup] {stage}: {(long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds}ms", "Startup");
    public static void HomeLoaded()
    {
        if (Interlocked.Exchange(ref _homeReported, 1) != 0) return;
        Mark("Home loaded");
        // Historical scripts used this label for first-page readiness, not background warmup.
        Mark("App ready");
    }
}

using System;

namespace FolderRewind.Services;

public readonly record struct BackupPerformanceValues(int CpuThreads, bool LowPriority);
public static class BackupPerformancePolicy
{
    public static int LightThreads(int maximum) => Math.Max(1, Math.Max(1, maximum) / 2);
    public static int VeryLightThreads(int maximum) => Math.Min(2, Math.Max(1, maximum));
    public static BackupPerformanceValues Get(int preset, int maximum, BackupPerformanceValues current) => preset switch
    {
        0 => new(0, false),
        1 => new(LightThreads(maximum), true),
        2 => new(VeryLightThreads(maximum), true),
        _ => current
    };
    public static int Derive(BackupPerformanceValues values, int maximum, int lastApplied = 3)
    {
        if (values == new BackupPerformanceValues(0, false)) return 0;
        if (!values.LowPriority) return 3;
        var light = values.CpuThreads == LightThreads(maximum);
        var veryLight = values.CpuThreads == VeryLightThreads(maximum);
        if (light && veryLight) return lastApplied is 1 or 2 ? lastApplied : 1;
        return light ? 1 : veryLight ? 2 : 3;
    }
}

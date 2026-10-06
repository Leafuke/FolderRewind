using System;
using System.Collections.Generic;

namespace FolderRewind.Services;

public readonly record struct BackupPerformanceValues(int CpuThreads, bool LowPriority);
public enum BackupPerformancePreset
{
    Auto = 0, Light = 1, VeryLight = 2, Custom = 3,
    GamePriority = 4, UltraFast = 5, SpaceSaving = 6
}

[Flags]
public enum BackupPerformanceFields { None = 0, Threads = 1, Priority = 2, Method = 4, Level = 8 }
public readonly record struct BackupPerformanceSnapshot(int CpuThreads, bool LowPriority, string Method, int CompressionLevel);
public sealed record BackupPerformancePresetDefinition(BackupPerformancePreset Id)
{
    public string NameKey => $"ConfigSettingsDialog_PerformancePreset_{Id}";
    public string DescriptionKey => $"Performance_Description_{Id}";
}

public static class BackupPerformancePolicy
{
    public static IReadOnlyList<BackupPerformancePresetDefinition> Presets { get; } = Array.AsReadOnly(new[]
    {
        new BackupPerformancePresetDefinition(BackupPerformancePreset.Auto),
        new BackupPerformancePresetDefinition(BackupPerformancePreset.Light),
        new BackupPerformancePresetDefinition(BackupPerformancePreset.VeryLight),
        new BackupPerformancePresetDefinition(BackupPerformancePreset.GamePriority),
        new BackupPerformancePresetDefinition(BackupPerformancePreset.UltraFast),
        new BackupPerformancePresetDefinition(BackupPerformancePreset.SpaceSaving),
        new BackupPerformancePresetDefinition(BackupPerformancePreset.Custom)
    });

    public static int OptionIndex(BackupPerformancePreset id)
    {
        for (var index = 0; index < Presets.Count; index++)
            if (Presets[index].Id == id) return index;
        return Presets.Count - 1;
    }

    public static BackupPerformanceSnapshot Get(BackupPerformancePreset preset, int maximum, BackupPerformanceSnapshot current)
    {
        maximum = Math.Max(1, maximum);
        if (preset is BackupPerformancePreset.GamePriority)
            return new(Math.Max(1, maximum / 8), true, "zstd", 3);
        if (preset is BackupPerformancePreset.UltraFast)
            return new(0, false, "zstd", 1);
        if (preset is BackupPerformancePreset.SpaceSaving)
            return current with { CpuThreads = maximum, LowPriority = true,
                CompressionLevel = string.Equals(current.Method, "zstd", StringComparison.OrdinalIgnoreCase) ? 19 : 9 };
        var resourceValues = Get((int)preset, maximum, new BackupPerformanceValues(current.CpuThreads, current.LowPriority));
        return current with { CpuThreads = resourceValues.CpuThreads, LowPriority = resourceValues.LowPriority };
    }

    public static BackupPerformancePreset Derive(BackupPerformanceSnapshot current, int maximum, BackupPerformancePreset lastApplied)
    {
        if (lastApplied != BackupPerformancePreset.Custom && Enum.IsDefined(lastApplied)
            && Get(lastApplied, maximum, current) == current) return lastApplied;
        foreach (var preset in new[] { BackupPerformancePreset.GamePriority, BackupPerformancePreset.UltraFast, BackupPerformancePreset.SpaceSaving })
            if (Get(preset, maximum, current) == current) return preset;
        return (BackupPerformancePreset)Derive(new BackupPerformanceValues(current.CpuThreads, current.LowPriority), maximum, (int)lastApplied);
    }

    public static BackupPerformanceFields ChangedFields(BackupPerformanceSnapshot before, BackupPerformanceSnapshot after)
        => (before.CpuThreads != after.CpuThreads ? BackupPerformanceFields.Threads : BackupPerformanceFields.None)
            | (before.LowPriority != after.LowPriority ? BackupPerformanceFields.Priority : BackupPerformanceFields.None)
            | (before.Method != after.Method ? BackupPerformanceFields.Method : BackupPerformanceFields.None)
            | (before.CompressionLevel != after.CompressionLevel ? BackupPerformanceFields.Level : BackupPerformanceFields.None);

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

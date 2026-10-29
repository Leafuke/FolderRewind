using System;
using System.Collections.Generic;

namespace FolderRewind.Services;

public enum AutomationTriggerKind
{
    None = 0,
    Scheduled = 1,
    Interval = 2
}

public readonly record struct AutomationScheduleCandidate(
    int MonthSelection,
    int DaySelection,
    int Hour,
    int Minute,
    DateTime LastTriggeredUtc)
{
    public bool ShouldTriggerNow(DateTime nowLocal)
    {
        if (nowLocal.Hour != Hour || nowLocal.Minute != Minute) return false;
        if (DaySelection == 0) return true;
        if (nowLocal.Day != DaySelection) return false;
        return MonthSelection == 0 || nowLocal.Month == MonthSelection;
    }
}

public readonly record struct AutomationTriggerDecision(
    AutomationTriggerKind Kind,
    int ScheduleEntryIndex,
    int IntervalMinutes)
{
    public static AutomationTriggerDecision None => new(AutomationTriggerKind.None, -1, 0);
}

public static class AutomationSchedulePolicy
{
    private static readonly TimeSpan ScheduledTriggerDeduplicationWindow = TimeSpan.FromMinutes(2);

    public static AutomationTriggerDecision Evaluate(
        DateTime nowLocal,
        DateTime utcNow,
        bool scheduledMode,
        IReadOnlyList<AutomationScheduleCandidate> scheduleEntries,
        bool intervalMode,
        int intervalMinutes,
        DateTime lastAutoBackupUtc)
    {
        ArgumentNullException.ThrowIfNull(scheduleEntries);

        if (scheduledMode)
        {
            for (var index = 0; index < scheduleEntries.Count; index++)
            {
                var entry = scheduleEntries[index];
                if (!entry.ShouldTriggerNow(nowLocal)) continue;
                if (entry.LastTriggeredUtc != DateTime.MinValue
                    && utcNow - entry.LastTriggeredUtc < ScheduledTriggerDeduplicationWindow)
                    continue;

                return new AutomationTriggerDecision(AutomationTriggerKind.Scheduled, index, 0);
            }
        }

        if (!intervalMode)
            return AutomationTriggerDecision.None;

        var normalizedInterval = Math.Clamp(intervalMinutes, 1, 10080);
        var isDue = lastAutoBackupUtc == DateTime.MinValue
            || utcNow - lastAutoBackupUtc >= TimeSpan.FromMinutes(normalizedInterval);
        return isDue
            ? new AutomationTriggerDecision(AutomationTriggerKind.Interval, -1, normalizedInterval)
            : AutomationTriggerDecision.None;
    }
}

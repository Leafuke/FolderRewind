using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class AutomationPolicyTests
{
    [TestMethod]
    public void ScheduleTrigger_TakesPrecedenceOverInterval()
    {
        var utcNow = new DateTime(2026, 9, 2, 6, 30, 0, DateTimeKind.Utc);
        var localNow = new DateTime(2026, 9, 2, 14, 30, 0, DateTimeKind.Local);
        var entries = new[]
        {
            new AutomationScheduleCandidate(0, 0, 14, 30, utcNow.AddMinutes(-3))
        };

        var decision = AutomationSchedulePolicy.Evaluate(
            localNow,
            utcNow,
            scheduledMode: true,
            entries,
            intervalMode: true,
            intervalMinutes: 1,
            lastAutoBackupUtc: DateTime.MinValue);

        Assert.AreEqual(AutomationTriggerKind.Scheduled, decision.Kind);
        Assert.AreEqual(0, decision.ScheduleEntryIndex);
    }

    [TestMethod]
    public void ScheduleTrigger_IsDeduplicatedForTwoMinutes()
    {
        var utcNow = new DateTime(2026, 9, 2, 6, 30, 0, DateTimeKind.Utc);
        var localNow = new DateTime(2026, 9, 2, 14, 30, 0, DateTimeKind.Local);
        var entries = new[]
        {
            new AutomationScheduleCandidate(0, 0, 14, 30, utcNow.AddMinutes(-1))
        };

        var decision = AutomationSchedulePolicy.Evaluate(
            localNow,
            utcNow,
            scheduledMode: true,
            entries,
            intervalMode: false,
            intervalMinutes: 60,
            lastAutoBackupUtc: DateTime.MinValue);

        Assert.AreEqual(AutomationTriggerKind.None, decision.Kind);
    }

    [TestMethod]
    public void IntervalTrigger_ClampsMinimumAndWaitsUntilDue()
    {
        var utcNow = new DateTime(2026, 9, 2, 6, 30, 0, DateTimeKind.Utc);

        var notDue = AutomationSchedulePolicy.Evaluate(
            utcNow.ToLocalTime(),
            utcNow,
            scheduledMode: false,
            [],
            intervalMode: true,
            intervalMinutes: 0,
            lastAutoBackupUtc: utcNow.AddSeconds(-30));
        var due = AutomationSchedulePolicy.Evaluate(
            utcNow.ToLocalTime(),
            utcNow,
            scheduledMode: false,
            [],
            intervalMode: true,
            intervalMinutes: 0,
            lastAutoBackupUtc: utcNow.AddMinutes(-1));

        Assert.AreEqual(AutomationTriggerKind.None, notDue.Kind);
        Assert.AreEqual(AutomationTriggerKind.Interval, due.Kind);
        Assert.AreEqual(1, due.IntervalMinutes);
    }

    [TestMethod]
    public void ConditionTracker_TriggersOnlyOnLockedToUnlockedTransition()
    {
        var tracker = new AutomationConditionStateTracker();

        Assert.AreEqual(
            AutomationConditionTransition.None,
            tracker.Observe("config|folder|level.dat", AutomationConditionFileState.Locked));
        Assert.AreEqual(
            AutomationConditionTransition.None,
            tracker.Observe("config|folder|level.dat", AutomationConditionFileState.Locked));
        Assert.AreEqual(
            AutomationConditionTransition.BecameUnlocked,
            tracker.Observe("config|folder|level.dat", AutomationConditionFileState.Unlocked));
        Assert.AreEqual(
            AutomationConditionTransition.BecameLocked,
            tracker.Observe("config|folder|level.dat", AutomationConditionFileState.Locked));
    }

    [TestMethod]
    public void ConditionTracker_CleansInactiveKeys()
    {
        var tracker = new AutomationConditionStateTracker();
        tracker.Observe("active", AutomationConditionFileState.Missing);
        tracker.Observe("removed", AutomationConditionFileState.Locked);

        tracker.RetainOnly(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "active" });

        Assert.AreEqual(1, tracker.Count);
        Assert.AreEqual(
            AutomationConditionTransition.None,
            tracker.Observe("removed", AutomationConditionFileState.Unlocked));
    }
}

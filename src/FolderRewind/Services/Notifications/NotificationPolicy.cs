using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

internal static class NotificationPolicy
{
    public static NotificationDeliveryPlan Evaluate(NotificationEvent item, NotificationEnvironment environment)
    {
        if (environment.Suppressed) return default;
        var record = item.KeepInCenter && item.Category != NotificationCategory.Progress;
        if (!environment.Enabled || item.SuppressAlert || item.Category == NotificationCategory.Progress) return new(record, false, false, false, false);
        var automaticSuccess = item.Content is { Trigger: Models.NotificationTrigger.Automatic,
            Result: Models.NotificationResultKind.Success or Models.NotificationResultKind.NoChanges }
            && item.Importance == NotificationImportance.Info && item.Severity is NotificationSeverity.Success or NotificationSeverity.Informational;
        if (automaticSuccess) return new(record, false, false, false, false);
        var eligible = item.Category is NotificationCategory.BusinessResult or NotificationCategory.Attention
            || item.Product is not null && item.Category == NotificationCategory.Announcement && item.Importance >= NotificationImportance.Important;
        var threshold = environment.ToastLevel switch
        {
            ToastNotificationLevel.All => true,
            ToastNotificationLevel.ImportantAndAbove => item.Importance >= NotificationImportance.Important,
            ToastNotificationLevel.ErrorOnly => item.Importance >= NotificationImportance.Error,
            _ => false
        };
        var readingResult = environment.CenterOpen && record;
        var sound = eligible && !readingResult && environment.CompletionSoundEnabled && item.CompletionSoundSuccess.HasValue;
        return new(record, !item.SystemOnly && (!readingResult || item.IsPersistent) && (environment.Foreground || item.IsPersistent),
            eligible && !environment.Foreground && threshold, sound, sound);
    }
}

/// <summary>Only successful submissions advance either per-operation or per-channel failure limits.</summary>
internal sealed class NotificationDeliveryLedger(TimeProvider clock)
{
    private const int Capacity = 2048;
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);
    private readonly Dictionary<(NotificationChannel, string), (long At, NotificationImportance Priority)> _failures = new();
    private readonly Dictionary<(NotificationChannel, string), (long At, NotificationImportance Priority)> _operations = new();
    public int FailureCount => _failures.Count;

    public bool Allows(NotificationEvent item, NotificationChannel channel)
    {
        Prune();
        if (item.AlertOnce && _operations.TryGetValue((channel, item.Id), out var operation) && item.Importance <= operation.Priority) return false;
        return !item.IsFailure || !_failures.TryGetValue((channel, item.FailureKey), out var failure)
            || item.Importance > failure.Priority;
    }

    public void Submitted(NotificationEvent item, NotificationChannel channel)
    {
        Prune();
        var value = (clock.GetTimestamp(), item.Importance);
        if (item.AlertOnce) _operations[(channel, item.Id)] = value;
        if (item.IsFailure) _failures[(channel, item.FailureKey)] = value;
        Bound(_operations); Bound(_failures);
    }

    private void Prune()
    {
        foreach (var key in _failures.Where(p => clock.GetElapsedTime(p.Value.At) >= Cooldown).Select(p => p.Key).ToArray()) _failures.Remove(key);
        foreach (var key in _operations.Where(p => clock.GetElapsedTime(p.Value.At) >= TimeSpan.FromHours(24)).Select(p => p.Key).ToArray()) _operations.Remove(key);
    }
    private static void Bound(Dictionary<(NotificationChannel, string), (long At, NotificationImportance Priority)> values)
    { while (values.Count > Capacity) values.Remove(values.MinBy(p => p.Value.At).Key); }
}

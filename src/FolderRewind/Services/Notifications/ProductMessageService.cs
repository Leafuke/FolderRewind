using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>Product lifecycle and bounded content, projected into the existing notification center.</summary>
internal static class ProductMessageService
{
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static readonly object InitLock = new();
    private static Task? _initialization;
    private static Timer? _reminderTimer;
    private static readonly Dictionary<string, ProductMessageState> Undo = new(StringComparer.Ordinal);
    private static DateTimeOffset _undoAt;
    private static bool _started;
    private static DateTimeOffset _nextDueCheck;
    internal static event Action? Changed;
    internal static string PersistenceError { get; private set; } = "";
    internal static bool IsRefreshing { get; private set; }
    internal static string UpdateKey => AppUpdateService.Latest is { } update
        ? $"update:{update.InstallChannel}:{update.LatestVersion}" : "";
    private static Dictionary<string, ProductMessageState> States =>
        ConfigService.CurrentConfig.GlobalSettings.ProductMessageStates ??= new(StringComparer.Ordinal);
    private static string CachePath => Path.Combine(ConfigService.ConfigDirectory, "cache", "product-messages-v1.json");
    internal static ProductMessageState State(string key) => States.GetValueOrDefault(key) ?? new();
    internal static IReadOnlyList<AnnouncementItem> Notices => NoticeService.Applicable();

    internal static Task InitializeAsync()
    { lock (InitLock) return _initialization ??= InitializeCoreAsync(); }

    private static async Task InitializeCoreAsync()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                await using var stream = File.OpenRead(CachePath);
                var bytes = await NoticeService.ReadBoundedAsync(stream, CancellationToken.None);
                var cache = JsonSerializer.Deserialize(bytes, ProductMessageJsonContext.Default.ProductMessageCacheDocument);
                if (cache is { SchemaVersion: 1 })
                {
                    NoticeService.Restore(cache.Notices, cache.NoticesFetchedUtc);
                    if (cache.Release is { Assets: not null } release) AppUpdateService.Restore(release, cache.UpdateFetchedUtc);
                }
            }
        }
        catch (Exception error) { LogService.LogError("Product content cache could not be read.", nameof(ProductMessageService), error); }
        NotificationService.Core.Delivered += item =>
        {
            if (item.Product is not { } product) return;
            UiDispatcherService.Post(() =>
            {
                if (States.TryGetValue(product.Key, out var state)) state.AlertDelivered = true;
                TaskObserver.Observe(SaveStateAsync(), nameof(ProductMessageService));
            });
        };
        await UiDispatcherService.RunOnUiAsync(() => Reconcile(false));
        _reminderTimer = new Timer(_ => TaskObserver.Observe(RemindDueAsync(), nameof(ProductMessageService)),
            null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    internal static async Task StartAsync(CancellationToken ct)
    {
        await InitializeAsync();
        if (_started) return;
        _started = true;
        var settings = ConfigService.CurrentConfig.GlobalSettings;
        await RefreshAsync(settings.EnableNotices, settings.EnableUpdateReminder, false, ct);
    }

    internal static async Task RefreshAsync(bool notices, bool update, bool manual, CancellationToken ct = default)
    {
        await InitializeAsync();
        await RefreshGate.WaitAsync(ct);
        try
        {
            IsRefreshing = true; Notify();
            await Task.WhenAll(notices ? NoticeService.CheckAsync(ct) : Task.CompletedTask,
                update ? AppUpdateService.CheckAsync(manual, ct) : Task.CompletedTask);
            await UiDispatcherService.RunOnUiAsync(() => Reconcile(!manual));
            await SaveCacheAsync();
            await SaveStateAsync();
        }
        finally { IsRefreshing = false; RefreshGate.Release(); Notify(); }
    }

    private static async Task RemindDueAsync()
    {
        if (IsRefreshing) return;
        await UiDispatcherService.RunOnUiAsync(() => Reconcile(false));
        if (!NativeWindowActivationService.IsAppForeground() || DateTimeOffset.UtcNow < _nextDueCheck) return;
        var due = await UiDispatcherService.RunOnUiAsync(() => States.Any(p => p.Value.SnoozedUntil <= DateTimeOffset.UtcNow
            && !p.Value.Archived && !p.Value.Ignored
            && (p.Key.StartsWith("notice:", StringComparison.Ordinal) ? ConfigService.CurrentConfig.GlobalSettings.EnableNotices
                : ConfigService.CurrentConfig.GlobalSettings.EnableUpdateReminder)));
        if (!due) return;
        _nextDueCheck = DateTimeOffset.UtcNow.AddHours(1);
        await RefreshGate.WaitAsync();
        try
        {
            var settings = ConfigService.CurrentConfig.GlobalSettings;
            await Task.WhenAll(settings.EnableNotices ? NoticeService.CheckAsync(CancellationToken.None) : Task.CompletedTask,
                settings.EnableUpdateReminder ? AppUpdateService.CheckAsync(true) : Task.CompletedTask);
            await UiDispatcherService.RunOnUiAsync(() => Reconcile(false, true));
            await SaveCacheAsync();
            await SaveStateAsync();
        }
        finally { RefreshGate.Release(); }
    }

    // All state mutation and notification projection happen on the UI dispatcher.
    private static void Reconcile(bool allowNewAlert, bool allowDue = false)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in States.Where(p => p.Value is null).Select(p => p.Key).ToArray()) States.Remove(key);
        var active = new HashSet<string>(StringComparer.Ordinal);
        var settings = ConfigService.CurrentConfig.GlobalSettings;
        var current = AppRuntimeInfo.GetApplicationVersion();
        var update = AppUpdateService.Latest;
        var updateKey = UpdateKey;
        var merged = new List<AnnouncementItem>();
        foreach (var notice in Notices)
        {
            var state = EnsureState(notice.Key, notice.ExpiresAt);
            if (!state.Discovered && notice.LegacyVersions.Contains(settings.NoticeLastSeenVersion)) state.IsRead = true;
            if (notice.Kind == "release" && update is not null
                && AppUpdatePolicy.ParseReleaseVersion("v" + notice.ReleaseVersion) == update.Target.LatestVersion)
            { merged.Add(notice); continue; }
            var releaseVersion = AppUpdatePolicy.ParseReleaseVersion("v" + notice.ReleaseVersion);
            if (notice.Kind == "release" && releaseVersion is not null && current >= releaseVersion)
            { state.Discovered = true; continue; }
            var text = NoticeService.Text(notice);
            Publish(notice.Key, notice.Revision, notice.PublishedAt, text.Title, text.Summary,
                new(NotificationTargetKind.AnnouncementDetails, ProductId: notice.Id),
                notice.Kind == "criticalCompatibility" ? NotificationImportance.Important : NotificationImportance.Info,
                NotificationCategory.Announcement, state, settings.EnableNotices, allowNewAlert, allowDue, active);
        }
        if (update is not null)
        {
            var state = EnsureState(updateKey, now.AddDays(30));
            if (!state.Discovered && merged.Any(n => State(n.Key).Discovered))
            {
                state.Discovered = true;
                state.IsRead = merged.All(n => State(n.Key).IsRead);
                state.Archived = merged.All(n => State(n.Key).Archived);
                state.SnoozedUntil = merged.Select(n => State(n.Key).SnoozedUntil).Max();
            }
            var summary = I18n.Format("Product_UpdateSummary", update.CurrentVersion, update.LatestVersion);
            if (update.InstallChannel == InstallChannel.Store) summary += Environment.NewLine + I18n.GetString("Update_Dialog_StoreDelayHint");
            if (merged.FirstOrDefault() is { } releaseNotice) summary += Environment.NewLine + NoticeService.Text(releaseNotice).Summary;
            Publish(updateKey, update.LatestVersion, AppUpdateService.Metadata?.PublishedAt ?? AppUpdateService.CheckedUtc ?? now,
                I18n.GetString("Product_UpdateTitle"), summary,
                new(NotificationTargetKind.ApplicationUpdate, ProductId: update.LatestVersion), NotificationImportance.Info,
                NotificationCategory.AppUpdate, state, settings.EnableUpdateReminder, allowNewAlert,
                allowDue && AppUpdateService.Status == AppUpdateService.CheckStatus.Available, active);
            foreach (var notice in merged)
            {
                var noticeState = EnsureState(notice.Key, notice.ExpiresAt);
                noticeState.Discovered = true; noticeState.IsRead = state.IsRead;
                noticeState.Archived = state.Archived || state.Ignored; noticeState.SnoozedUntil = state.SnoozedUntil;
            }
        }
        foreach (var record in NotificationCenterService.Snapshot().Where(r => r.Product is not null && !active.Contains(r.Id)))
            NotificationService.Core.Retire(record.Id);
        foreach (var key in States.Where(p => p.Value.ExpiresUtc != default && p.Value.ExpiresUtc < now.AddDays(-30)).Select(p => p.Key).ToArray())
            States.Remove(key);
        if (States.Count > 512)
        {
            var retained = Notices.Select(n => n.Key).Append(updateKey).ToHashSet(StringComparer.Ordinal);
            foreach (var key in States.Where(p => !retained.Contains(p.Key)).OrderBy(p => p.Value.ExpiresUtc)
                .Take(States.Count - 512).Select(p => p.Key).ToArray()) States.Remove(key);
        }
        Notify();
    }

    private static ProductMessageState EnsureState(string key, DateTimeOffset expires)
    {
        if (!States.TryGetValue(key, out var state)) States[key] = state = new();
        state.ExpiresUtc = expires;
        return state;
    }

    private static void Publish(string key, string revision, DateTimeOffset published, string title, string summary,
        NotificationTarget target, NotificationImportance importance, NotificationCategory category,
        ProductMessageState state, bool enabled, bool newAlerts, bool dueAlerts, HashSet<string> active)
    {
        var due = dueAlerts && state.SnoozedUntil <= DateTimeOffset.UtcNow && enabled;
        var alert = enabled && !state.IsRead && !state.Discovered && newAlerts && state.SnoozedUntil is null || due;
        if (due) { state.ReminderRevision = state.SnoozedUntil?.ToString("O") ?? ""; state.SnoozedUntil = null; }
        var reminder = state.ReminderRevision;
        state.Discovered = true;
        if (state.Archived || state.Ignored) return;
        active.Add(key);
        if (!due && NotificationCenterService.Find(key) is { } previous && previous.Product?.ContentRevision == revision
            && previous.Title == title && previous.Message == summary && previous.Target == target && previous.IsRead == state.IsRead) return;
        NotificationService.ShowNotification(new()
        {
            RecordId = key, Product = new(key, revision, published), Target = target,
            Category = category, KeepInCenter = true, RestoreReadState = state.IsRead,
            SuppressAlert = !alert, AlertOnce = false, AutoCloseMs = importance == NotificationImportance.Important ? 8000 : 5000,
            Title = title, Message = summary, Details = summary,
            ResultKey = revision + "|" + summary + "|" + reminder,
            Severity = importance == NotificationImportance.Important ? NotificationSeverity.Warning : NotificationSeverity.Informational
        }, importance);
    }

    internal static void MarkReadRecords(IEnumerable<NotificationRecord> records)
    {
        var products = records.Where(r => r.Product is not null).ToArray();
        if (products.Length == 0) return;
        foreach (var record in products)
            if (States.TryGetValue(record.Product!.Key, out var state))
            { state.IsRead = true; SyncMergedChoices(record.Product.Key, state); }
        TaskObserver.Observe(SaveStateAsync(), nameof(ProductMessageService)); Notify();
    }
    internal static void Cleared(IEnumerable<NotificationRecord> records)
    {
        Undo.Clear(); _undoAt = DateTimeOffset.UtcNow;
        var products = records.Where(r => r.Product is not null).ToArray();
        if (products.Length == 0) return;
        foreach (var record in products)
            if (States.TryGetValue(record.Product!.Key, out var state))
            {
                NotificationService.Core.CancelPresentation(record.Id);
                Undo[record.Id] = state.Copy(); state.Archived = true; state.SnoozedUntil = null;
                SyncMergedChoices(record.Product.Key, state);
            }
        TaskObserver.Observe(SaveStateAsync(), nameof(ProductMessageService)); Notify();
    }
    internal static void UndoCleared(IEnumerable<NotificationRecord> restored)
    {
        if (DateTimeOffset.UtcNow - _undoAt < TimeSpan.FromSeconds(10))
            foreach (var record in restored)
                if (Undo.TryGetValue(record.Id, out var state))
                { States[record.Id] = state; SyncMergedChoices(record.Id, state); }
        Undo.Clear(); TaskObserver.Observe(SaveStateAsync(), nameof(ProductMessageService)); Notify();
    }

    private static void SyncMergedChoices(string key, ProductMessageState state)
    {
        if (key != UpdateKey || AppUpdateService.Latest is not { } update) return;
        foreach (var notice in Notices.Where(n => n.Kind == "release"
            && AppUpdatePolicy.ParseReleaseVersion("v" + n.ReleaseVersion) == update.Target.LatestVersion))
        {
            var other = EnsureState(notice.Key, notice.ExpiresAt);
            other.IsRead = state.IsRead; other.Archived = state.Archived || state.Ignored;
            other.Discovered = true; other.SnoozedUntil = state.SnoozedUntil;
        }
    }

    internal static async Task SetChoiceAsync(string key, string choice)
    {
        if (string.IsNullOrEmpty(key)) return;
        await UiDispatcherService.RunOnUiAsync(() =>
        {
            // A release announcement and its update card represent one reminder choice.
            if (AppUpdateService.Latest is { } update && Notices.Any(n => n.Key == key && n.Kind == "release"
                && AppUpdatePolicy.ParseReleaseVersion("v" + n.ReleaseVersion) == update.Target.LatestVersion)) key = UpdateKey;
            if (!States.TryGetValue(key, out var state)) return;
            switch (choice)
            {
                case "read": state.IsRead = true; NotificationService.Core.MarkRead(key); break;
                case "snooze": NotificationService.Core.Retire(key); state.SnoozedUntil = DateTimeOffset.UtcNow.AddDays(7); state.Archived = state.Ignored = false; break;
                case "ignore": state.Ignored = true; state.SnoozedUntil = null; break;
                case "archive": state.Archived = true; state.SnoozedUntil = null; break;
                case "restore": state.Archived = state.Ignored = false; state.SnoozedUntil = null; break;
            }
            SyncMergedChoices(key, state);
            Reconcile(false);
        });
        await SaveStateAsync();
    }

    private static async Task SaveStateAsync()
    {
        var result = await ConfigService.SaveAsync();
        PersistenceError = result.Success ? "" : I18n.GetString("Product_StateSaveFailed");
        Notify();
    }
    private static async Task SaveCacheAsync()
    {
        try
        {
            var cache = new ProductMessageCacheDocument { Notices = NoticeService.Document, NoticesFetchedUtc = NoticeService.FetchedUtc,
                Release = AppUpdateService.Metadata, UpdateFetchedUtc = AppUpdateService.CheckedUtc ?? default };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(cache, ProductMessageJsonContext.Default.ProductMessageCacheDocument);
            if (bytes.Length > NoticeService.MaximumBytes) throw new InvalidDataException("Product cache exceeds size bound.");
            await AtomicFileService.WriteAsync(CachePath, (stream, ct) => stream.WriteAsync(bytes, ct).AsTask());
        }
        catch (Exception error) { LogService.LogError("Product content cache could not be saved.", nameof(ProductMessageService), error); }
    }
    private static void Notify() => NotificationCoordinator.Notify(Changed);
}

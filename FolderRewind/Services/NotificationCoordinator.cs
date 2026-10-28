using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed class NotificationCoordinator
{
    private readonly object _sync = new();
    private readonly Func<NotificationEnvironment> _environment;
    private readonly INotificationSystemSender _system;
    private readonly INotificationSoundSender _sound;
    private readonly SemaphoreSlim _systemGate = new(1, 1), _soundGate = new(1, 1);
    private readonly LatestNotificationTracker _latest = new();
    private readonly Dictionary<string, (string Revision, DateTimeOffset At)> _revisions = new(StringComparer.Ordinal);
    private readonly NotificationDeliveryLedger _ledger;
    public NotificationStore Store { get; }
    public event Action? Changed;
    public event Action<NotificationBannerRequest>? BannerRequested;

    public NotificationCoordinator(TimeProvider clock, Func<NotificationEnvironment> environment,
        INotificationSystemSender system, INotificationSoundSender sound)
    { _environment = environment; _system = system; _sound = sound; _ledger = new(clock); Store = new(clock); }

    public async Task PublishAsync(NotificationEvent item)
    {
        try
        {
            var environment = _environment();
            NotificationDeliveryPlan plan;
            bool changed = false, quiet;
            lock (_sync)
            {
                plan = NotificationPolicy.Evaluate(item, environment);
                if (environment.Suppressed || !_latest.Accept(item.Correlation)) return;
                if (plan.Record)
                {
                    changed = Store.Upsert(item);
                    if (!changed) return;
                }
                _revisions[item.GroupKey] = (item.Revision, item.OccurredUtc);
                while (_revisions.Count > 2048) _revisions.Remove(_revisions.MinBy(p => p.Value.At).Key);
                quiet = !_ledger.Allows(item, NotificationChannel.Banner);
            }
            if (changed) Notify(Changed);
            if (plan.Banner) Notify(BannerRequested, new(item, environment.Generation, quiet, PresentationGeneration: environment.PresentationGeneration));
            // These gates serialize channel submissions, not record updates or UI work.
            var systemStatus = NotificationSendStatus.Unavailable;
            if (plan.System) systemStatus = await SendSystemAsync(item, environment.Generation, plan.MuteSystem).ConfigureAwait(false);
            // A channel setting change can cancel the popup without disabling the completion sound.
            // A canceled send that is still eligible was merged or invalidated and must remain quiet.
            if (plan.Sound && (!plan.System || systemStatus != NotificationSendStatus.Canceled
                || !NotificationPolicy.Evaluate(item, _environment()).System))
                await SendSoundAsync(item, environment.Generation,
                    plan.System && systemStatus != NotificationSendStatus.Canceled, environment.PresentationGeneration).ConfigureAwait(false);
        }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine($"[NotificationCoordinator] {error}"); }
    }

    public bool CanShowBanner(NotificationBannerRequest request)
        => PrepareBanner(request, false) is not null;

    public bool CanQueueBanner(NotificationBannerRequest request)
    {
        var environment = _environment();
        lock (_sync) return IsCurrent(request.Event, request.Generation, environment)
            && PresentationCurrent(request, environment)
            && NotificationPolicy.Evaluate(request.Event, environment).Banner;
    }

    public NotificationBannerRequest? PrepareBanner(NotificationBannerRequest request, bool updatingVisible)
    {
        var environment = _environment();
        lock (_sync)
        {
            if (!IsCurrent(request.Event, request.Generation, environment) || !environment.Foreground
                || !PresentationCurrent(request, environment) || !NotificationPolicy.Evaluate(request.Event, environment).Banner) return null;
            if (request.QuietUpdate || request.Resume || _ledger.Allows(request.Event, NotificationChannel.Banner)) return request;
            // A preceding display may have been acknowledged after this update was published.
            return updatingVisible ? request with { QuietUpdate = true } : null;
        }
    }

    private static bool PresentationCurrent(NotificationBannerRequest request, NotificationEnvironment environment)
        => !request.Event.KeepInCenter || request.Event.IsPersistent || request.PresentationGeneration == environment.PresentationGeneration;

    private bool IsCurrent(NotificationEvent item, int generation, NotificationEnvironment environment)
        => environment.Enabled && !environment.Suppressed && generation == environment.Generation
            && _revisions.TryGetValue(item.GroupKey, out var latest) && latest.Revision == item.Revision;

    private bool CanSend(NotificationEvent item, int generation, NotificationChannel channel)
    {
        var environment = _environment();
        var plan = NotificationPolicy.Evaluate(item, environment);
        lock (_sync)
            return IsCurrent(item, generation, environment)
                && (channel == NotificationChannel.System ? plan.System : plan.Sound)
                && _ledger.Allows(item, channel);
    }

    private async Task<NotificationSendStatus> SendSystemAsync(NotificationEvent item, int generation, bool muteAudio)
    {
        await _systemGate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool Guard() => CanSend(item, generation, NotificationChannel.System);
            if (!Guard()) return NotificationSendStatus.Canceled;
            // Freeze the audio choice for this event so a setting change cannot cause both sounds.
            var status = await _system.SendAsync(item, muteAudio, Guard).ConfigureAwait(false);
            if (status == NotificationSendStatus.Submitted)
                lock (_sync) _ledger.Submitted(item, NotificationChannel.System);
            return status;
        }
        catch (Exception error)
        { System.Diagnostics.Debug.WriteLine($"[NotificationSystem] {error}"); return NotificationSendStatus.Failed; }
        finally { _systemGate.Release(); }
    }

    private async Task SendSoundAsync(NotificationEvent item, int generation, bool systemAttempted, int presentationGeneration)
    {
        await _soundGate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool Guard()
            {
                if (!CanSend(item, generation, NotificationChannel.Sound)) return false;
                var environment = _environment();
                if (item.KeepInCenter && presentationGeneration != environment.PresentationGeneration) return false;
                // A failure merged out of the foreground banner must not ring independently.
                lock (_sync) return systemAttempted || !environment.Foreground
                    || _ledger.Allows(item, NotificationChannel.Banner)
                    || _lastBannerRevision == item.Revision;
            }
            if (!Guard()) return;
            var status = await _sound.SendAsync(item.CompletionSoundSuccess!.Value, Guard).ConfigureAwait(false);
            if (status == NotificationSendStatus.Submitted)
                lock (_sync) _ledger.Submitted(item, NotificationChannel.Sound);
        }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine($"[NotificationSound] {error}"); }
        finally { _soundGate.Release(); }
    }

    private string? _lastBannerRevision;
    public void ReportBannerShown(NotificationBannerRequest request)
    {
        lock (_sync)
        {
            _lastBannerRevision = request.Event.Revision;
            if (!request.QuietUpdate && !request.Resume) _ledger.Submitted(request.Event, NotificationChannel.Banner);
        }
    }
    public void MarkRead(string id, string? revision = null) { if (Store.MarkRead(id, revision)) Notify(Changed); }
    public void MarkAllRead() { Store.MarkAllRead(); Notify(Changed); }
    public void ClearRead() { Store.ClearRead(); Notify(Changed); }
    public void UndoClear() { Store.UndoClear(); Notify(Changed); }

    internal static void Notify(Action? handlers)
    {
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
    }
    private static void Notify(Action<NotificationBannerRequest>? handlers, NotificationBannerRequest request)
    {
        if (handlers is null) return;
        foreach (Action<NotificationBannerRequest> handler in handlers.GetInvocationList())
            try { handler(request); } catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
    }
}

internal sealed class FallbackNotificationSender(INotificationSystemSender primary, INotificationSystemSender fallback) : INotificationSystemSender
{
    public async Task<NotificationSendStatus> SendAsync(NotificationEvent notification, bool muteAudio, Func<bool> canSend)
    {
        if (!canSend()) return NotificationSendStatus.Canceled;
        NotificationSendStatus result;
        try { result = await primary.SendAsync(notification, muteAudio, canSend).ConfigureAwait(false); }
        catch { result = NotificationSendStatus.Failed; }
        if (result is not (NotificationSendStatus.Failed or NotificationSendStatus.Unavailable)) return result;
        if (!canSend()) return NotificationSendStatus.Canceled;
        try { return await fallback.SendAsync(notification, muteAudio, canSend).ConfigureAwait(false); }
        catch { return NotificationSendStatus.Failed; }
    }
}

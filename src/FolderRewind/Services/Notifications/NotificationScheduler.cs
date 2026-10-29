using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

/// <summary>UI-owner state machine. No timers, controls, callbacks or wall-clock sleeps.</summary>
internal sealed class NotificationScheduler(TimeProvider clock, int capacity = 32)
{
    private sealed class Entry(NotificationBannerRequest request, long queued)
    {
        public NotificationBannerRequest Request = request;
        public long Queued = queued;
        public double Remaining = request.Event.AutoCloseMs;
    }
    private readonly List<Entry> _queue = [];
    private Entry? _current;
    private long _started;
    private bool _hover, _focus, _foreground = true;
    public NotificationBannerRequest? Current => _current?.Request;
    public int Revision { get; private set; }
    public int PendingCount => _queue.Count;
    public bool HasWork => _current is not null || _queue.Count != 0;
    private bool Protected => _hover || _focus;
    public double RemainingMilliseconds => _current is null ? 0 : Remaining(_current);

    public void Enqueue(NotificationBannerRequest request)
    {
        ExpirePending();
        if (!_foreground && !request.Event.IsPersistent) return;
        var group = request.Event.GroupKey;
        var replaces = _current?.Request.Event.GroupKey == group;
        var queued = _queue.RemoveAll(e => e.Request.Event.GroupKey == group) != 0;
        if (request.QuietUpdate && !replaces && !queued) return;
        _queue.Add(new(request, clock.GetTimestamp()));
        Bound();
        Reconsider();
    }

    public void SetInteraction(bool hover, bool keyboardFocus)
    {
        if (!Protected && (hover || keyboardFocus) && _current is not null) _current.Remaining = Remaining(_current);
        var wasProtected = Protected;
        _hover = hover; _focus = keyboardFocus;
        if (wasProtected && !Protected) { _started = clock.GetTimestamp(); Reconsider(); }
    }

    public void SetForeground(bool foreground)
    {
        if (_foreground == foreground) return;
        _foreground = foreground;
        if (!foreground)
        {
            _hover = _focus = false;
            _queue.RemoveAll(e => !e.Request.Event.IsPersistent);
            if (_current is { Request.Event.IsPersistent: false }) SetCurrent(null);
        }
        Reconsider();
    }

    public void Advance()
    {
        ExpirePending();
        if (!Protected && _current is { Request.Event.IsPersistent: false } current && Remaining(current) <= 0)
            SetCurrent(null);
        Reconsider();
    }

    public void Close()
    {
        _hover = _focus = false;
        if (_current is { } current) _queue.RemoveAll(e => e.Request.Event.GroupKey == current.Request.Event.GroupKey);
        SetCurrent(null);
        Reconsider();
    }

    public void Clear()
    {
        _queue.Clear(); _hover = _focus = false;
        SetCurrent(null);
    }

    public void DiscardRecorded()
    {
        _queue.RemoveAll(e => e.Request.Event.KeepInCenter);
        if (_current?.Request.Event.KeepInCenter == true)
        { _hover = _focus = false; SetCurrent(null); }
        Reconsider();
    }

    private double Remaining(Entry entry) => entry.Request.Event.IsPersistent || Protected ? entry.Remaining
        : Math.Max(0, entry.Remaining - clock.GetElapsedTime(_started).TotalMilliseconds);

    private void Reconsider()
    {
        if (Protected) return;
        ExpirePending();
        var replacement = _current is null ? null : _queue.FirstOrDefault(e => e.Request.Event.GroupKey == _current.Request.Event.GroupKey);
        if (replacement is not null) { _queue.Remove(replacement); SetCurrent(replacement); }
        var best = _queue.OrderByDescending(Priority).ThenBy(e => e.Queued).FirstOrDefault();
        if (best is null) return;
        if (_current is not null && Priority(best) <= Priority(_current)) return;
        if (_current is { } interrupted)
        {
            interrupted.Remaining = Remaining(interrupted);
            interrupted.Queued = clock.GetTimestamp();
            interrupted.Request = interrupted.Request with { Resume = true };
            if (interrupted.Request.Event.IsPersistent || interrupted.Remaining > 0) _queue.Add(interrupted);
        }
        _queue.Remove(best);
        SetCurrent(best);
        Bound();
    }

    private void SetCurrent(Entry? value)
    {
        if (ReferenceEquals(value, _current)) return;
        _current = value; _started = clock.GetTimestamp(); Revision++;
    }

    private static int Priority(Entry entry) => entry.Request.Event.Severity == NotificationSeverity.Error ? 3
        : entry.Request.Event.IsPersistent ? 0 : entry.Request.Event.Severity == NotificationSeverity.Warning ? 2 : 1;
    private void ExpirePending() => _queue.RemoveAll(e => !e.Request.Event.IsPersistent
        && e.Request.Event.GroupKey != _current?.Request.Event.GroupKey
        && e.Request.Event.Severity <= NotificationSeverity.Success && clock.GetElapsedTime(e.Queued) > TimeSpan.FromSeconds(30));
    private void Bound()
    {
        while (_queue.Count > Math.Max(1, capacity))
            _queue.Remove(_queue.Where(e => e.Request.Event.GroupKey != _current?.Request.Event.GroupKey)
                .OrderBy(Priority).ThenBy(e => e.Queued).First());
    }
}

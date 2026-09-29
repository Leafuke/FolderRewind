using FolderRewind.History.Domain;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace FolderRewind.History.Application;

public enum HistoryChangeKind
{
    RepositoryInitialized = 0,
    PacksImported = 1,
    TransactionCommitted = 2,
    LocalStateChanged = 3,
    IndexRebuilt = 4,
    MigrationCompleted = 5,
    CloudUnionChanged = 6
}

public sealed record HistoryChange(
    long Sequence,
    HistoryConfigId ConfigId,
    HistoryChangeKind Kind,
    ImmutableArray<string> ObjectIds,
    DateTimeOffset PublishedAtUtc);

public sealed class HistoryChangeFeed
{
    private readonly object _sync = new();
    private readonly Dictionary<long, Action<HistoryChange>> _subscribers = new();
    private long _nextSubscriberId;
    private long _sequence;

    public long CurrentSequence => Interlocked.Read(ref _sequence);

    public IDisposable Subscribe(Action<HistoryChange> subscriber)
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        var id = Interlocked.Increment(ref _nextSubscriberId);
        lock (_sync)
        {
            _subscribers.Add(id, subscriber);
        }

        return new Subscription(this, id);
    }

    public HistoryChange Publish(
        HistoryConfigId configId,
        HistoryChangeKind kind,
        IEnumerable<string>? objectIds = null)
    {
        var change = new HistoryChange(
            Interlocked.Increment(ref _sequence),
            configId,
            kind,
            objectIds is null ? [] : [.. objectIds],
            DateTimeOffset.UtcNow);
        Action<HistoryChange>[] subscribers;
        lock (_sync)
        {
            subscribers = _subscribers.Values.ToArray();
        }

        foreach (var subscriber in subscribers)
        {
            try
            {
                subscriber(change);
            }
            catch
            {
                // ChangeFeed 是提交后的通知边界；观察者失败不能反向破坏已经提交的历史事实。
            }
        }

        return change;
    }

    private void Unsubscribe(long id)
    {
        lock (_sync)
        {
            _subscribers.Remove(id);
        }
    }

    private sealed class Subscription(HistoryChangeFeed owner, long id) : IDisposable
    {
        private HistoryChangeFeed? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unsubscribe(id);
    }
}

using System;
using System.Collections.Specialized;

namespace FolderRewind.Services;

/// <summary>Owns the exact collection/handler pair, including when a model replaces its collection.</summary>
internal sealed class CollectionChangedSubscription : IDisposable
{
    private INotifyCollectionChanged? _source;
    private NotifyCollectionChangedEventHandler? _handler;

    public bool SetSource(INotifyCollectionChanged? source, NotifyCollectionChangedEventHandler handler)
    {
        if (ReferenceEquals(source, _source) && handler == _handler) return false;
        Dispose();
        _source = source;
        _handler = handler;
        if (_source is not null) _source.CollectionChanged += _handler;
        return true;
    }

    public void Dispose()
    {
        if (_source is not null) _source.CollectionChanged -= _handler;
        _source = null;
        _handler = null;
    }
}

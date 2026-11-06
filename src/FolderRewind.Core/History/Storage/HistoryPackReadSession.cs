using FolderRewind.Services;

namespace FolderRewind.History.Storage;

/// <summary>Validated typed facts live only for one repository operation, never across disk reads.</summary>
internal sealed class HistoryPackReadSession : IDisposable
{
    private sealed record Fact(byte[] CanonicalSnapshot, object Value);
    private readonly Dictionary<HistoryPackObject, Fact> _facts = new(ReferenceEqualityComparer.Instance);
    private readonly HistoryOperationDiagnostics? _diagnostic = HistoryOperationDiagnostics.Current;
    private long _decoded, _reused;
    private bool _disposed;

    internal void RequireActive() => ObjectDisposedException.ThrowIf(_disposed, this);

    // The codec transfers its private Canonicalize result here. HistoryPackObject's
    // public byte[] is a separate clone, so callers cannot mutate this witness.
    internal void RememberValidated(HistoryPackObject item, byte[] ownedCanonicalSnapshot, object value)
    {
        RequireActive();
        _facts.Add(item, new(ownedCanonicalSnapshot, value));
        _decoded++;
    }

    internal object DeserializeKnown(HistoryPackObject item, HistoryPackCodec codec)
    {
        RequireActive();
        if (!_facts.TryGetValue(item, out var fact)) return codec.DeserializeKnown(item);
        if (!fact.CanonicalSnapshot.AsSpan().SequenceEqual(item.CanonicalPayload))
            throw new HistoryIntegrityConflictException($"Validated payload changed for {item.Kind}/{item.Id}.");
        _reused++;
        return fact.Value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _diagnostic?.Count("history-known-facts-decoded", _decoded);
        _diagnostic?.Count("history-known-facts-reused", _reused);
        _facts.Clear();
    }
}

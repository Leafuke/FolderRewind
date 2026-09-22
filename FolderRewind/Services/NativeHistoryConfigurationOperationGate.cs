using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.History.Domain;

namespace FolderRewind.Services;

/// <summary>
/// Serializes capture preparation, Native History commit, and workspace-changing restores
/// for one configuration. Different configurations remain independent.
/// </summary>
internal static class NativeHistoryConfigurationOperationGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    public static ValueTask<Lease> EnterAsync(
        string configId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(configId, out var parsed) || parsed == Guid.Empty)
            throw new ArgumentException("A stable configuration ID is required.", nameof(configId));
        return EnterHistoryAsync(new HistoryConfigId(configId), cancellationToken);
    }

    internal static async ValueTask<Lease> EnterHistoryAsync(HistoryConfigId configId, CancellationToken cancellationToken = default)
    {
        var gate = Gates.GetOrAdd(configId.Value, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(configId, gate);
    }

    internal sealed class Lease(HistoryConfigId configId, SemaphoreSlim gate) : IAsyncDisposable
    {
        private int _released;

        internal void Require(HistoryConfigId expected)
        {
            if (configId != expected || Volatile.Read(ref _released) != 0)
                throw new InvalidOperationException("An active operation lease for this configuration is required.");
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}

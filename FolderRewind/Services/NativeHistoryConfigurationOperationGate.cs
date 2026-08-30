using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>
/// Serializes capture preparation, Native History commit, and workspace-changing restores
/// for one configuration. Different configurations remain independent.
/// </summary>
internal static class NativeHistoryConfigurationOperationGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    public static async ValueTask<IAsyncDisposable> EnterAsync(
        string configId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(configId, out var parsed) || parsed == Guid.Empty)
            throw new ArgumentException("A stable configuration ID is required.", nameof(configId));
        var gate = Gates.GetOrAdd(parsed.ToString("N"), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}

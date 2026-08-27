using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed class HistoryRuntimeManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<HistoryRuntime>>> _runtimes =
        new(StringComparer.Ordinal);
    private bool _disposed;

    public async Task<HistoryRuntime> GetOrCreateAsync(
        HistoryConfigId configId,
        Func<HistoryConfigId, CancellationToken, Task<FileHistoryRepository>> repositoryFactory,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        var key = configId.Value;
        var lazy = _runtimes.GetOrAdd(
            key,
            _ => new Lazy<Task<HistoryRuntime>>(
                () => CreateRuntimeAsync(configId, repositoryFactory, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            _runtimes.TryRemove(new KeyValuePair<string, Lazy<Task<HistoryRuntime>>>(key, lazy));
            throw;
        }
    }

    public bool TryGet(HistoryConfigId configId, out HistoryRuntime? runtime)
    {
        runtime = null;
        if (!_runtimes.TryGetValue(configId.Value, out var lazy)
            || !lazy.IsValueCreated
            || !lazy.Value.IsCompletedSuccessfully)
        {
            return false;
        }

        runtime = lazy.Value.Result;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var created = _runtimes.Values
            .Where(lazy => lazy.IsValueCreated)
            .Select(lazy => lazy.Value)
            .ToArray();
        _runtimes.Clear();
        foreach (var task in created)
        {
            try
            {
                await (await task.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static async Task<HistoryRuntime> CreateRuntimeAsync(
        HistoryConfigId configId,
        Func<HistoryConfigId, CancellationToken, Task<FileHistoryRepository>> repositoryFactory,
        CancellationToken cancellationToken)
    {
        var repository = await repositoryFactory(configId, cancellationToken).ConfigureAwait(false);
        var runtime = new HistoryRuntime(repository);
        try
        {
            await runtime.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return runtime;
        }
        catch
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

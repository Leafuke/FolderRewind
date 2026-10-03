using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>
/// Serializes detached configuration snapshots through one writer. Adjacent pending writes are
/// coalesced to the newest revision while every caller still observes the resulting write.
/// </summary>
internal sealed class ConfigWriteCoordinator : IAsyncDisposable
{
    private readonly Channel<WorkItem> _queue = Channel.CreateUnbounded<WorkItem>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _writer;
    private readonly Action _savedCallback;
    private readonly Task _worker;
    private long _nextRevision;
    private long _persistedRevision;

    public ConfigWriteCoordinator(
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> writer,
        Action? savedCallback = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _savedCallback = savedCallback ?? (() => { });
        _worker = ProcessQueueAsync();
    }

    internal long PersistedRevision => Interlocked.Read(ref _persistedRevision);

    public Task<ConfigSaveResult> EnqueueAsync(
        byte[] payload,
        bool publishSavedEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();

        var request = new SaveRequest(
            Interlocked.Increment(ref _nextRevision),
            payload,
            publishSavedEvent,
            cancellationToken);
        if (!_queue.Writer.TryWrite(request))
        {
            request.Dispose();
            return Task.FromResult(new ConfigSaveResult
            {
                Success = false,
                ErrorMessage = "Configuration writer is not accepting new requests."
            });
        }

        return request.Completion.Task;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new FlushRequest();
        if (!_queue.Writer.TryWrite(request))
        {
            await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await request.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (item is FlushRequest flush)
            {
                flush.Completion.TrySetResult(null);
                continue;
            }

            var requests = new List<SaveRequest> { (SaveRequest)item };
            while (_queue.Reader.TryPeek(out var next) && next is SaveRequest)
            {
                if (_queue.Reader.TryRead(out var pending))
                {
                    requests.Add((SaveRequest)pending);
                }
            }

            await PersistLatestAsync(requests).ConfigureAwait(false);
        }
    }

    private async Task PersistLatestAsync(List<SaveRequest> requests)
    {
        SaveRequest? latest = null;
        foreach (var request in requests)
        {
            if (!request.Completion.Task.IsCanceled)
            {
                latest = request;
            }
        }

        if (latest is null)
        {
            DisposeRequests(requests);
            return;
        }

        ConfigSaveResult result;
        try
        {
            // Once an atomic write has started, an individual caller cancellation must not leave
            // the persisted revision ambiguous. Cancellation only stops requests still in queue.
            await _writer(latest.Payload, CancellationToken.None).ConfigureAwait(false);
            Interlocked.Exchange(ref _persistedRevision, latest.Revision);
            result = new ConfigSaveResult { Success = true };

            if (requests.Exists(request => request.PublishSavedEvent && !request.Completion.Task.IsCanceled))
            {
                try
                {
                    _savedCallback();
                }
                catch
                {
                    // A subscriber must not turn a successful durable write into a save failure.
                }
            }
        }
        catch (Exception ex)
        {
            result = new ConfigSaveResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Exception = ex
            };
        }

        foreach (var request in requests)
        {
            request.Completion.TrySetResult(result);
            request.Dispose();
        }
    }

    private static void DisposeRequests(IEnumerable<SaveRequest> requests)
    {
        foreach (var request in requests)
        {
            request.Dispose();
        }
    }

    private abstract class WorkItem;

    private sealed class FlushRequest : WorkItem
    {
        public TaskCompletionSource<object?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SaveRequest : WorkItem, IDisposable
    {
        private readonly CancellationTokenRegistration _cancellationRegistration;

        public SaveRequest(
            long revision,
            byte[] payload,
            bool publishSavedEvent,
            CancellationToken cancellationToken)
        {
            Revision = revision;
            Payload = payload;
            PublishSavedEvent = publishSavedEvent;
            Completion = new TaskCompletionSource<ConfigSaveResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _cancellationRegistration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(() => Completion.TrySetCanceled(cancellationToken))
                : default;
        }

        public long Revision { get; }

        public byte[] Payload { get; }

        public bool PublishSavedEvent { get; }

        public TaskCompletionSource<ConfigSaveResult> Completion { get; }

        public void Dispose() => _cancellationRegistration.Dispose();
    }
}

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed class AsyncOperationQueue
{
    private readonly Channel<IQueuedOperation> _operations = Channel.CreateUnbounded<IQueuedOperation>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    public AsyncOperationQueue()
    {
        _ = ProcessQueueAsync();
    }

    public Task<T> EnqueueAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        var queuedOperation = new QueuedOperation<T>(operation, cancellationToken);
        if (!_operations.Writer.TryWrite(queuedOperation))
        {
            throw new InvalidOperationException("The asynchronous operation queue is not accepting requests.");
        }

        return queuedOperation.Completion;
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var operation in _operations.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await operation.ExecuteAsync().ConfigureAwait(false);
        }
    }

    private interface IQueuedOperation
    {
        Task ExecuteAsync();
    }

    private sealed class QueuedOperation<T> : IQueuedOperation
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<CancellationToken, Task<T>> _operation;
        private readonly CancellationToken _cancellationToken;
        private readonly CancellationTokenRegistration _cancellationRegistration;

        public QueuedOperation(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            _operation = operation;
            _cancellationToken = cancellationToken;
            _cancellationRegistration = cancellationToken.Register(
                static state =>
                {
                    var operationState = (QueuedOperation<T>)state!;
                    operationState._completion.TrySetCanceled(operationState._cancellationToken);
                },
                this);
        }

        public Task<T> Completion => _completion.Task;

        public async Task ExecuteAsync()
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                _cancellationRegistration.Dispose();
                return;
            }

            try
            {
                _completion.TrySetResult(await _operation(_cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_cancellationToken);
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
            finally
            {
                _cancellationRegistration.Dispose();
            }
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public sealed class AsyncPeriodicLoop : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _period;
    private readonly bool _runImmediately;
    private readonly Func<CancellationToken, Task> _iteration;
    private readonly Action<Exception> _reportFailure;
    private CancellationTokenSource? _stopSource;
    private Task? _runTask;

    public AsyncPeriodicLoop(
        TimeProvider timeProvider,
        TimeSpan period,
        bool runImmediately,
        Func<CancellationToken, Task> iteration,
        Action<Exception> reportFailure)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(iteration);
        ArgumentNullException.ThrowIfNull(reportFailure);
        if (period <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(period), period, "The period must be positive.");

        _timeProvider = timeProvider;
        _period = period;
        _runImmediately = runImmediately;
        _iteration = iteration;
        _reportFailure = reportFailure;
    }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _runTask is { IsCompleted: false };
            }
        }
    }

    public Task StartAsync(CancellationToken stoppingToken = default)
    {
        lock (_sync)
        {
            if (_runTask is { IsCompleted: false })
                return Task.CompletedTask;

            _stopSource?.Dispose();
            _stopSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _runTask = RunAsync(_stopSource.Token);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? source;
        Task? task;
        lock (_sync)
        {
            source = _stopSource;
            task = _runTask;
        }

        if (source is null || task is null)
            return;

        try
        {
            source.Cancel();
            await task.ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(source, _stopSource))
                {
                    _stopSource = null;
                    _runTask = null;
                }
            }

            source.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_period, _timeProvider);
            if (_runImmediately)
                await RunIterationAsync(cancellationToken).ConfigureAwait(false);

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await RunIterationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunIterationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _iteration(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                _reportFailure(ex);
            }
            catch
            {
            }
        }
    }
}

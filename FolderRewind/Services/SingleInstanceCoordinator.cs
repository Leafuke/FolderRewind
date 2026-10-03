using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>Owns the mutex on the entry thread; activation is a bounded, user-only pipe protocol.</summary>
internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const int MaximumRequestBytes = 32;
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<Func<bool>> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _server;
    private bool _disposed;
    private volatile bool _closing;

    private SingleInstanceCoordinator(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        _server = Task.Run(ListenAsync);
    }

    public static SingleInstanceCoordinator? TryAcquire(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        var mutex = new Mutex(false, @"Global\" + identity);
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (acquired) return new SingleInstanceCoordinator(mutex, identity);
        mutex.Dispose();
        return null;
    }

    public void SetReady(Func<bool> activate) => _ready.TrySetResult(activate);
    public void SetClosing(bool closing) => _closing = closing;

    public static async Task<bool> NotifyAsync(string identity, bool startup, CancellationToken cancellationToken = default, Action<int>? allowForeground = null)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", identity, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(budget.Token).ConfigureAwait(false);
            var process = new byte[4];
            await pipe.ReadExactlyAsync(process, budget.Token).ConfigureAwait(false);
            if (!startup) allowForeground?.Invoke(BitConverter.ToInt32(process));
            await pipe.WriteAsync(Encoding.ASCII.GetBytes(startup ? "STARTUP\n" : "ACTIVATE\n"), budget.Token).ConfigureAwait(false);
            await pipe.FlushAsync(budget.Token).ConfigureAwait(false);
            var response = new byte[1];
            await pipe.ReadExactlyAsync(response, budget.Token).ConfigureAwait(false);
            return response[0] == 1;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }

    private async Task ListenAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var requestBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
                requestBudget.CancelAfter(TimeSpan.FromSeconds(5));
                await pipe.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), requestBudget.Token).ConfigureAwait(false);
                await pipe.FlushAsync(requestBudget.Token).ConfigureAwait(false);
                var request = await ReadRequestAsync(pipe, requestBudget.Token).ConfigureAwait(false);
                if (_closing || request is not ("ACTIVATE" or "STARTUP"))
                {
                    await pipe.WriteAsync(new byte[] { 0 }, requestBudget.Token).ConfigureAwait(false);
                    continue;
                }
                var activate = await _ready.Task.WaitAsync(requestBudget.Token).ConfigureAwait(false);
                bool accepted = !_closing && (request == "STARTUP" || activate());
                await pipe.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, requestBudget.Token).ConfigureAwait(false);
                await pipe.FlushAsync(requestBudget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException) { }
        }
    }

    internal static async Task<string?> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        var data = new byte[MaximumRequestBytes];
        var single = new byte[1];
        for (int count = 0; count < data.Length; count++)
        {
            if (await stream.ReadAsync(single, token).ConfigureAwait(false) != 1) return null;
            if (single[0] == (byte)'\n') return Encoding.ASCII.GetString(data, 0, count);
            if (single[0] < 32 || single[0] > 126) return null;
            data[count] = single[0];
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        try { _server.GetAwaiter().GetResult(); }
        finally
        {
            _stop.Dispose();
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}

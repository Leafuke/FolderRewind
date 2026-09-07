using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Operations;

/// <summary>
/// Wraps the Host mutation continuation so a restore coordinator can enter the
/// mutation phase at most once.
/// </summary>
public sealed class RestoreMutationContinuationGate
{
    private readonly RestoreMutationContinuation _continuation;
    private readonly object _sync = new();
    private Task<OperationOutcome>? _invocation;
    private bool _closed;

    public RestoreMutationContinuationGate(RestoreMutationContinuation continuation)
        => _continuation = continuation ?? throw new ArgumentNullException(nameof(continuation));

    public bool WasInvoked { get { lock (_sync) return _invocation is not null; } }

    public ValueTask<OperationOutcome> InvokeAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<OperationOutcome> completion;
        lock (_sync)
        {
            if (_closed || _invocation is not null)
                throw new InvalidOperationException("The restore continuation is closed or already invoked.");
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _invocation = completion.Task;
        }
        _ = RunAsync(completion, cancellationToken);
        return new ValueTask<OperationOutcome>(completion.Task);
    }

    public Task CloseAndDrainAsync()
    {
        lock (_sync)
        {
            _closed = true;
            return _invocation ?? Task.CompletedTask;
        }
    }

    private async Task RunAsync(TaskCompletionSource<OperationOutcome> completion, CancellationToken token)
    {
        try { completion.TrySetResult(await _continuation(token).ConfigureAwait(false)); }
        catch (OperationCanceledException ex) { completion.TrySetCanceled(ex.CancellationToken); }
        catch (Exception ex) { completion.TrySetException(ex); }
    }
}

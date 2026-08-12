using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Operations;

/// <summary>
/// Wraps the Host mutation continuation so a restore coordinator can enter the
/// mutation phase at most once.
/// </summary>
public sealed class RestoreMutationContinuationGate
{
    private readonly RestoreMutationContinuation _continuation;
    private int _invoked;

    public RestoreMutationContinuationGate(RestoreMutationContinuation continuation)
        => _continuation = continuation ?? throw new ArgumentNullException(nameof(continuation));

    public bool WasInvoked => Volatile.Read(ref _invoked) != 0;

    public ValueTask<OperationOutcome> InvokeAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _invoked, 1) != 0)
        {
            throw new InvalidOperationException("The restore mutation continuation can be invoked only once.");
        }

        return _continuation(cancellationToken);
    }
}

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static class ProcessWaitService
{
    /// <returns>False only for a timeout; caller cancellation is propagated after cleanup.</returns>
    public static async Task<bool> WaitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { /* Process exited concurrently. */ }
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }
}

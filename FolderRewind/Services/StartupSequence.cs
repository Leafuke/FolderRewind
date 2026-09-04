using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed class StartupSequence(Action<Exception> reportError)
{
    private int _started;
    public async Task RunAsync(IEnumerable<Func<CancellationToken, Task>> stages, CancellationToken token)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        foreach (var stage in stages)
        {
            if (token.IsCancellationRequested) return;
            try { await stage(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex) { reportError(ex); }
        }
    }
}

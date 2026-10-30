using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed record MigrationResumeResult(string ConfigId, bool Backup, bool Cloud, string Diagnostic);

/// <summary>Each callback commits one project; later failures or cancellation never undo earlier decisions.</summary>
internal static class MigrationBatchConfirmation
{
    internal static async Task<IReadOnlyList<MigrationResumeResult>> RunAsync<T>(IEnumerable<T> requests,
        Func<T, string> identity, Func<T, CancellationToken, Task<MigrationResumeResult>> confirm,
        Func<Exception, string> diagnostic, CancellationToken token = default)
    {
        var results = new List<MigrationResumeResult>();
        foreach (var request in requests)
        {
            if (token.IsCancellationRequested) break;
            try { results.Add(await confirm(request, token).ConfigureAwait(false)); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) { results.Add(new(identity(request), false, false, diagnostic(error))); }
        }
        return results;
    }
}

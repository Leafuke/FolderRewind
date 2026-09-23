using FolderRewind.History.Application;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Merge;

internal static class MergePostActions
{
    internal static async Task<HistoryRestoreResult> CompleteAsync(HistoryRestoreResult result,
        Func<CancellationToken, Task> synchronize, Func<CancellationToken, Task> invalidate)
    {
        if (!result.Succeeded || !result.TargetCommitted) return result;
        try { await synchronize(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex)
        {
            string detail = ex.Message;
            try { await invalidate(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) { detail += " " + cleanup.Message; }
            return result with { Status = HistoryRestoreStatus.CommittedWithPostActionWarning,
                Diagnostic = $"Merge committed; capture cache synchronization failed: {detail}",
                MergeDiagnostic = new(MergeDiagnosticCode.PostActionWarning) };
        }
        return result;
    }
}

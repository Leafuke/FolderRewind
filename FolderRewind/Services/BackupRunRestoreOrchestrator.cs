using FolderRewind.Models;
using System;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static class BackupRunRestoreOrchestrator
{
    public static async Task<BackupRunRestoreResult> RestoreAsync(
        BackupRunRecord run,
        Func<BackupRunSourceRecord, Task<BackupRunRestoreSourceResult>> restoreSourceAsync)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(restoreSourceAsync);
        var result = new BackupRunRestoreResult { RunId = run.RunId };
        foreach (var source in run.Sources)
        {
            if (source.Status is BackupRunSourceStatus.Failed or BackupRunSourceStatus.Unavailable
                || string.IsNullOrWhiteSpace(source.HistoryItemId))
            {
                result.Sources.Add(new BackupRunRestoreSourceResult
                {
                    FolderPath = source.FolderPath,
                    ErrorMessage = string.IsNullOrWhiteSpace(source.ErrorMessage)
                        ? "No recoverable archive reference is available for this source."
                        : source.ErrorMessage
                });
                continue;
            }

            try
            {
                result.Sources.Add(await restoreSourceAsync(source).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                result.Sources.Add(new BackupRunRestoreSourceResult
                {
                    FolderPath = source.FolderPath,
                    ErrorMessage = ex.Message
                });
            }
        }
        return result;
    }
}

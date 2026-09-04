using FolderRewind.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static class FolderScopeEditController
{
    public static async Task<bool> ApplyAsync(BackupSourceScope before, BackupSourceScope after, bool isBroadRoot,
        Func<bool, CancellationToken, Task<bool>> confirmExpansion,
        Action<BackupSourceScope> assign, Func<Task<ConfigSaveResult>> save, string failureMessage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (before.Mode == BackupSourceScopeMode.Include && after.Mode == BackupSourceScopeMode.All)
        {
            if (!await confirmExpansion(false, token)) return false;
            if (isBroadRoot && !await confirmExpansion(true, token)) return false;
        }
        await ConfigEditTransaction.ApplyAsync(() => assign(after), () => assign(before), save, failureMessage, token);
        return true;
    }
}

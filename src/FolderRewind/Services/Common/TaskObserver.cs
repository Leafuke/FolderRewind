using System;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static class TaskObserver
{
    public static void Observe(Task task, string source) => _ = ObserveAsync(task, ex => LogService.LogError("Background operation failed.", source, ex));

    internal static async Task ObserveAsync(Task task, Action<Exception> reportError)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { reportError(ex); }
    }

    public static async Task SaveConfigAsync()
    {
        var result = await ConfigService.SaveAsync();
        if (!result.Success) throw new System.IO.IOException(result.ErrorMessage, result.Exception);
    }
}

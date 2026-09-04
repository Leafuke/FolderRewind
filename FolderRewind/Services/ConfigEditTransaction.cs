using FolderRewind.Models;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>
/// Applies a UI-owned edit and compensates it when persistence explicitly fails.
/// Cancellation is accepted before the edit, not while its durable result is unknown.
/// Delegates and continuations run on the caller's synchronization context.
/// </summary>
internal static class ConfigEditTransaction
{
    public static async Task ApplyAsync(
        Action apply,
        Action rollback,
        Func<Task<ConfigSaveResult>> save,
        string failureMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(rollback);
        ArgumentNullException.ThrowIfNull(save);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            apply();
            ThrowIfFailed(await save(), failureMessage);
        }
        catch (Exception originalError)
        {
            try
            {
                rollback();
                // Other producers may already have queued a snapshot containing the temporary
                // edit. Enqueue the compensated state after them, even when the caller canceled.
                ThrowIfFailed(await save(), failureMessage);
            }
            catch (Exception rollbackError)
            {
                throw new ConfigEditRollbackException(failureMessage, originalError, rollbackError);
            }
            throw;
        }
    }

    private static void ThrowIfFailed(ConfigSaveResult result, string failureMessage)
    {
        if (!result.Success)
        {
            throw new IOException(
                string.IsNullOrWhiteSpace(result.ErrorMessage) ? failureMessage : result.ErrorMessage,
                result.Exception);
        }
    }
}

/// <summary>Persistence is uncertain; callers must preserve credentials needed by any queued snapshot.</summary>
internal sealed class ConfigEditRollbackException(string message, Exception originalError, Exception rollbackError)
    : IOException(message, new AggregateException(originalError, rollbackError));

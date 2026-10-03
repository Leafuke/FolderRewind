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
    internal static Task ReplaceItemAsync<T>(
        System.Collections.Generic.IList<T> items, T? original, T? replacement,
        Func<Task<ConfigSaveResult>> save, string failureMessage) where T : class
    {
        var index = original is null ? items.Count : items.IndexOf(original);
        if (index < 0) throw new InvalidOperationException("The edited item is no longer in the configuration.");
        return ApplyAsync(
            () =>
            {
                if (original is null) items.Add(replacement!);
                else if (replacement is null) items.RemoveAt(index);
                else items[index] = replacement;
            },
            () =>
            {
                if (original is null) items.Remove(replacement!);
                else if (replacement is null)
                {
                    if (!items.Contains(original)) items.Insert(index, original);
                }
                else if (!ReferenceEquals(items[index], original)) items[index] = original;
            }, save, failureMessage);
    }

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

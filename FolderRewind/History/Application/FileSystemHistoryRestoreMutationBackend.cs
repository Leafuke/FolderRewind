using FolderRewind.History.Domain;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

/// <summary>
/// Same-volume directory snapshot mutation backend used by Native Restore. Clean applies to an empty target;
/// Overwrite first restores the old tree into the empty target and then layers materialized content over it.
/// </summary>
public sealed class FileSystemHistoryRestoreMutationBackend : IHistoryRestoreMutationBackend
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200)
    ];

    public HistoryRestoreRollbackSnapshot PlanRollback(
        HistoryRestoreSourceBinding source,
        HistoryTransactionId transactionId)
    {
        var target = Path.GetFullPath(source.TargetDirectory);
        var parent = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException("Restore target has no parent directory.");
        if (File.Exists(target))
            throw new InvalidOperationException("Restore target is a file, not a directory.");
        bool hadOriginal = Directory.Exists(target);
        string rollback = Path.Combine(
            parent,
            $".{Path.GetFileName(target)}.restore-{transactionId}.rollback");
        if (Directory.Exists(rollback) || File.Exists(rollback))
            throw new IOException("Restore rollback path already exists.");
        return new HistoryRestoreRollbackSnapshot(
            source.SourceId,
            target,
            rollback,
            hadOriginal);
    }

    public async Task PrepareRollbackAsync(
        HistoryRestoreRollbackSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parent = Path.GetDirectoryName(snapshot.TargetDirectory)
            ?? throw new InvalidOperationException("Restore target has no parent directory.");
        Directory.CreateDirectory(parent);
        if (snapshot.HadOriginalTarget)
        {
            if (!Directory.Exists(snapshot.RollbackDirectory))
            {
                if (!Directory.Exists(snapshot.TargetDirectory))
                    throw new DirectoryNotFoundException("Restore target disappeared before rollback preparation.");
                await MoveDirectoryWithRetryAsync(
                    snapshot.TargetDirectory,
                    snapshot.RollbackDirectory,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else if (File.Exists(snapshot.TargetDirectory))
        {
            throw new InvalidOperationException("Restore target became a file before rollback preparation.");
        }
        Directory.CreateDirectory(snapshot.TargetDirectory);
    }

    public async Task ApplyAsync(
        HistoryRestoreSourceBinding source,
        string stagingDirectory,
        HistoryRestoreApplyMode applyMode,
        HistoryRestoreRollbackSnapshot rollbackSnapshot,
        CancellationToken cancellationToken)
    {
        if (applyMode == HistoryRestoreApplyMode.Overwrite && rollbackSnapshot.HadOriginalTarget)
        {
            await CopyTreeAsync(rollbackSnapshot.RollbackDirectory, rollbackSnapshot.TargetDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
        await CopyTreeAsync(stagingDirectory, rollbackSnapshot.TargetDirectory, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RollbackAsync(
        HistoryRestoreRollbackSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.HadOriginalTarget)
        {
            if (Directory.Exists(snapshot.RollbackDirectory))
            {
                await DeleteDirectoryWithRetryAsync(snapshot.TargetDirectory, cancellationToken).ConfigureAwait(false);
                await MoveDirectoryWithRetryAsync(
                    snapshot.RollbackDirectory,
                    snapshot.TargetDirectory,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (!Directory.Exists(snapshot.TargetDirectory))
            {
                throw new DirectoryNotFoundException("Restore rollback snapshot is missing.");
            }
        }
        else
        {
            await DeleteDirectoryWithRetryAsync(snapshot.TargetDirectory, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task CommitAsync(
        HistoryRestoreRollbackSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await DeleteDirectoryWithRetryAsync(snapshot.RollbackDirectory, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyTreeAsync(
        string sourceDirectory,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Materialized directory '{source}' is missing.");
        Directory.CreateDirectory(targetDirectory);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(targetDirectory, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(targetDirectory, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task MoveDirectoryWithRetryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
        => ExecuteWithRetryAsync(
            () => Directory.Move(source, destination),
            cancellationToken);

    private static Task DeleteDirectoryWithRetryAsync(string path, CancellationToken cancellationToken)
        => ExecuteWithRetryAsync(
            () =>
            {
                if (!Directory.Exists(path)) return;
                NormalizeDeletionAttributes(path);
                Directory.Delete(path, recursive: true);
            },
            cancellationToken);

    private static async Task ExecuteWithRetryAsync(Action operation, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                operation();
                return;
            }
            catch (Exception ex) when (IsRetryable(ex) && attempt < RetryDelays.Length)
            {
                await Task.Delay(RetryDelays[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsRetryable(Exception exception)
        => exception is IOException or UnauthorizedAccessException;

    private static void NormalizeDeletionAttributes(string root)
    {
        var pending = new Stack<string>();
        var directories = new List<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            directories.Add(current);
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    TryClearReadOnly(entry, attributes);
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }
                try { File.SetAttributes(entry, FileAttributes.Normal); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        for (int index = directories.Count - 1; index >= 0; index--)
        {
            try
            {
                var attributes = File.GetAttributes(directories[index]);
                TryClearReadOnly(directories[index], attributes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void TryClearReadOnly(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReadOnly) == 0) return;
        try { File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

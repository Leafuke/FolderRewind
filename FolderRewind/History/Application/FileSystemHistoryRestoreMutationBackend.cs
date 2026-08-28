using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

/// <summary>
/// Same-volume directory snapshot mutation backend used by Native Restore. Exact applies to an empty target;
/// Overlay first restores the old tree into the empty target and then layers materialized content over it.
/// </summary>
public sealed class FileSystemHistoryRestoreMutationBackend : IHistoryRestoreMutationBackend
{
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

    public Task PrepareRollbackAsync(
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
                Directory.Move(snapshot.TargetDirectory, snapshot.RollbackDirectory);
            }
        }
        else if (File.Exists(snapshot.TargetDirectory))
        {
            throw new InvalidOperationException("Restore target became a file before rollback preparation.");
        }
        Directory.CreateDirectory(snapshot.TargetDirectory);
        return Task.CompletedTask;
    }

    public async Task ApplyAsync(
        HistoryRestoreSourceBinding source,
        string stagingDirectory,
        MaterializationFidelity fidelity,
        HistoryRestoreRollbackSnapshot rollbackSnapshot,
        CancellationToken cancellationToken)
    {
        if (fidelity == MaterializationFidelity.Overlay && rollbackSnapshot.HadOriginalTarget)
        {
            await CopyTreeAsync(rollbackSnapshot.RollbackDirectory, rollbackSnapshot.TargetDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
        await CopyTreeAsync(stagingDirectory, rollbackSnapshot.TargetDirectory, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task RollbackAsync(
        HistoryRestoreRollbackSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.HadOriginalTarget)
        {
            if (Directory.Exists(snapshot.RollbackDirectory))
            {
                DeleteDirectory(snapshot.TargetDirectory);
                Directory.Move(snapshot.RollbackDirectory, snapshot.TargetDirectory);
            }
            else if (!Directory.Exists(snapshot.TargetDirectory))
            {
                throw new DirectoryNotFoundException("Restore rollback snapshot is missing.");
            }
        }
        else
        {
            DeleteDirectory(snapshot.TargetDirectory);
        }
        return Task.CompletedTask;
    }

    public Task CommitAsync(
        HistoryRestoreRollbackSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteDirectory(snapshot.RollbackDirectory);
        return Task.CompletedTask;
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

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        }
        Directory.Delete(path, recursive: true);
    }
}

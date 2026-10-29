using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.Models;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        var target = HistoryRestoreWorkspacePlanner.ResolvePath(source.TargetDirectory);
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
            hadOriginal, hadOriginal ? null : $".folderrewind-restore-{transactionId}.owner");
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
        else
        {
            if (snapshot.NewTargetOwnershipMarker is null) throw new InvalidOperationException("New restore target requires an ownership marker.");
            Directory.CreateDirectory(snapshot.RollbackDirectory);
            var marker = Path.Combine(snapshot.RollbackDirectory, snapshot.NewTargetOwnershipMarker);
            using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Flush(true);
            // Directory.Move fails if another writer has created the target; never adopt that directory.
            await MoveDirectoryWithRetryAsync(snapshot.RollbackDirectory, snapshot.TargetDirectory, cancellationToken).ConfigureAwait(false);
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
        var boundary = CreateBoundaryMatcher(source);
        var directoryBoundary = CreateBoundaryMatcher(source, isDirectory: true);
        if (rollbackSnapshot.HadOriginalTarget)
        {
            await CopyTreeAsync(
                    rollbackSnapshot.RollbackDirectory,
                    rollbackSnapshot.TargetDirectory,
                    applyMode == HistoryRestoreApplyMode.Overwrite
                        ? null
                        : relativePath => !boundary(relativePath),
                    cancellationToken, applyMode == HistoryRestoreApplyMode.Overwrite ? null : relative => !directoryBoundary(relative))
                .ConfigureAwait(false);
        }
        await CopyTreeAsync(
                stagingDirectory,
                rollbackSnapshot.TargetDirectory,
                relativePath => boundary(relativePath),
                cancellationToken, directoryBoundary)
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
            if (snapshot.NewTargetOwnershipMarker is not null && File.Exists(Path.Combine(snapshot.TargetDirectory, snapshot.NewTargetOwnershipMarker)))
                await DeleteDirectoryWithRetryAsync(snapshot.TargetDirectory, cancellationToken).ConfigureAwait(false);
            await DeleteDirectoryWithRetryAsync(snapshot.RollbackDirectory, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task CommitAsync(
        HistoryRestoreRollbackSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!snapshot.HadOriginalTarget && snapshot.NewTargetOwnershipMarker is not null)
            File.Delete(Path.Combine(snapshot.TargetDirectory, snapshot.NewTargetOwnershipMarker));
        await DeleteDirectoryWithRetryAsync(snapshot.RollbackDirectory, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyTreeAsync(
        string sourceDirectory,
        string targetDirectory,
        Func<string, bool>? include,
        CancellationToken cancellationToken,
        Func<string, bool>? includeDirectory = null)
    {
        var source = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Materialized directory '{source}' is missing.");
        Directory.CreateDirectory(targetDirectory);
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(file => (FullPath: file, RelativePath: Path.GetRelativePath(source, file).Replace('\\', '/')))
            .Where(file => include is null || include(file.RelativePath)).ToArray();
        var directories = SourceDirectorySnapshot.Read(source, include ?? (_ => true), files.Select(file => file.RelativePath), cancellationToken,
            includeDirectory: includeDirectory);
        SourceDirectorySnapshot.Validate(directories, files.Select(file => file.RelativePath));
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(ArtifactPathRules.ResolveUnderRoot(targetDirectory, directory));
        }
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = ArtifactPathRules.ResolveUnderRoot(targetDirectory, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            HistoryOperationDiagnostics.Current?.Count("copy-tree-bytes", input.Length);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static Func<string, bool> CreateBoundaryMatcher(HistoryRestoreSourceBinding source, bool isDirectory = false)
    {
        var scope = new Storage.HistoryRestoreTreeScope(source);
        return relativePath => scope.Includes(relativePath.Replace('\\', '/'), isDirectory);
    }

    private static Task MoveDirectoryWithRetryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
        => ExecuteWithRetryAsync(
            () => Directory.Move(source, destination),
            cancellationToken);

    private static Task DeleteDirectoryWithRetryAsync(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path)) return Task.CompletedTask;
        if (!OperatingSystem.IsWindows()) return ExecuteWithRetryAsync(() => Directory.Delete(path, recursive: true), cancellationToken);
        // 旧执行协议也不能通过清除只读位修改范围外的同一硬链接对象。
        // 固定一次归属集合；重试不接管后来出现的条目，也不跟随链接。
        var owned = HistoryRestoreFileSystemPlanStore.CaptureTree(path);
        return ExecuteWithRetryAsync(() =>
        {
            foreach (var item in owned.OrderBy(item => item.Identity.Directory).ThenByDescending(item => item.Path.Length))
            {
                var current = Storage.WindowsRestoreFileSystem.TryIdentity(item.Path);
                if (current is null) continue;
                if (current != item.Identity) throw new IOException("Legacy restore cleanup object changed; preserve data.");
                Storage.WindowsRestoreFileSystem.DeleteOwned(item.Path, item.Identity);
            }
        }, cancellationToken);
    }

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

}

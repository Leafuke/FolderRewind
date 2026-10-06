using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>
/// 崩溃安全文件写入原语：先写同目录隐藏临时文件（WriteThrough 直落盘），
/// 再用 File.Replace 原子替换（目标不存在则 Move）。所有配置/历史/元数据持久化都经由此类，
/// 保证任一时刻磁盘上要么是完整的旧文件、要么是完整的新文件。
/// </summary>
internal static class AtomicFileService
{
    /// <summary>
    /// 同步原子写入；write 委托负责把全部内容写入临时流。
    /// </summary>
    public static void Write(string destinationPath, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(write);

        string fullDestinationPath = Path.GetFullPath(destinationPath);
        string directory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new InvalidOperationException("The destination path has no parent directory.");
        Directory.CreateDirectory(directory);

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullDestinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.WriteThrough))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            InstallSameVolume(temporaryPath, fullDestinationPath);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// 异步原子写入；异常或替换失败时 finally 尽力删除残留的临时文件。
    /// </summary>
    public static async Task WriteAsync(
        string destinationPath,
        Func<Stream, CancellationToken, Task> writeAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(writeAsync);

        string fullDestinationPath = Path.GetFullPath(destinationPath);
        string directory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new InvalidOperationException("The destination path has no parent directory.");
        Directory.CreateDirectory(directory);

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullDestinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await writeAsync(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            InstallSameVolume(temporaryPath, fullDestinationPath);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
            }
        }
    }

    private static void InstallSameVolume(string temporaryPath, string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            File.Move(temporaryPath, destinationPath);
            return;
        }

        try
        {
            File.Replace(
                temporaryPath,
                destinationPath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: true);
        }
        catch (IOException)
        {
            // Some Windows filesystems reject ReplaceFile without a backup target even though an
            // atomic same-volume overwrite is supported. MoveFileEx (used by File.Move overwrite)
            // preserves the complete-old-or-complete-new contract for this fallback.
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
    }
}

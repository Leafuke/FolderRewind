using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static class AtomicFileService
{
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
            DeleteTemporary(temporaryPath);
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
            DeleteTemporary(temporaryPath);
        }
    }

    private static void InstallSameVolume(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Replace(
                temporaryPath,
                destinationPath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporaryPath, destinationPath);
        }
    }

    private static void DeleteTemporary(string temporaryPath)
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

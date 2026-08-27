using System;
using System.IO;

namespace FolderRewind.Services;

/// <summary>
/// Rolling 归档的同文件系统 copy-on-write 原语。Committed baseline 永远只读；
/// 所有更新发生在唯一 staging copy，验证成功后再 create-once 安装为新归档。
/// </summary>
internal sealed class RollingArchiveTransaction : IDisposable
{
    private string? _stagingPath;

    private RollingArchiveTransaction(string baselinePath, string stagingPath)
    {
        BaselinePath = baselinePath;
        _stagingPath = stagingPath;
    }

    public string BaselinePath { get; }
    public string StagingPath => _stagingPath
        ?? throw new InvalidOperationException("Rolling transaction is already committed or disposed.");

    public static RollingArchiveTransaction Create(string baselinePath, string destinationDirectory)
    {
        var baseline = Path.GetFullPath(baselinePath ?? throw new ArgumentNullException(nameof(baselinePath)));
        var destination = Path.GetFullPath(
            destinationDirectory ?? throw new ArgumentNullException(nameof(destinationDirectory)));
        if (!File.Exists(baseline))
        {
            throw new FileNotFoundException("Rolling baseline archive is missing.", baseline);
        }

        Directory.CreateDirectory(destination);
        var extension = Path.GetExtension(baseline);
        var staging = Path.Combine(destination, $".rolling-{Guid.NewGuid():N}.staging{extension}");
        using (var source = new FileStream(baseline, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var target = new FileStream(
                   staging,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   64 * 1024,
                   FileOptions.WriteThrough))
        {
            source.CopyTo(target);
            target.Flush(flushToDisk: true);
        }

        return new RollingArchiveTransaction(baseline, staging);
    }

    public void Commit(string finalPath)
    {
        var staging = StagingPath;
        var final = Path.GetFullPath(finalPath ?? throw new ArgumentNullException(nameof(finalPath)));
        var stagingDirectory = Path.GetDirectoryName(staging)
            ?? throw new InvalidOperationException("Rolling staging path has no parent directory.");
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(final), stagingDirectory))
        {
            throw new InvalidOperationException("Rolling final archive must use the staging filesystem directory.");
        }
        if (File.Exists(final))
        {
            throw new IOException("Rolling final archive already exists; committed archives are create-once.");
        }

        File.Move(staging, final, overwrite: false);
        _stagingPath = null;
    }

    public void Dispose()
    {
        var staging = _stagingPath;
        _stagingPath = null;
        if (string.IsNullOrWhiteSpace(staging)) return;
        try
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
        catch
        {
        }
    }
}


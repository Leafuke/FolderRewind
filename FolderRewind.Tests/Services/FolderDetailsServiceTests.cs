using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.Services;
using Xunit;

namespace FolderRewind.Tests.Services;

public class FolderDetailsServiceTests
{
    [Fact]
    public async Task ComputeStatisticsAsync_counts_bytes_files_and_directories()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "region"));
            await File.WriteAllTextAsync(Path.Combine(root, "level.dat"), "abc");
            await File.WriteAllTextAsync(Path.Combine(root, "region", "r.0.0.mca"), "12345");

            var snapshot = await FolderDetailsService.ComputeStatisticsAsync(root, CancellationToken.None);

            Assert.Equal(2, snapshot.FileCount);
            Assert.Equal(1, snapshot.DirectoryCount);
            Assert.Equal(8, snapshot.TotalBytes);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ComputeStatisticsAsync_honors_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            FolderDetailsService.ComputeStatisticsAsync(@"D:\does-not-matter", cts.Token));
    }
}

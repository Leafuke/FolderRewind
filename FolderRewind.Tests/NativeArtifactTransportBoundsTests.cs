using System.Diagnostics;
using System.Security.Cryptography;
using FolderRewind.Services;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Tests;

[TestClass]
public sealed class NativeArtifactTransportBoundsTests
{
    [TestMethod]
    public async Task TransferChecksExactLengthAndCumulativeBudgetBeforeWritingExcessBytes()
    {
        var budget = new ArtifactTransferBudget(5);
        await budget.CopyAsync(new MemoryStream([1, 2, 3]), Stream.Null, 3, default);
        using var output = new MemoryStream();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => budget.CopyAsync(new MemoryStream([4, 5, 6]), output, 3, default));
        Assert.AreEqual(0L, output.Length);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new ArtifactTransferBudget(5).CopyAsync(new MemoryStream([1, 2, 3, 4]), output, 3, default));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new ArtifactTransferBudget(5).CopyAsync(new MemoryStream([1]), output, 3, default));
    }

    [TestMethod]
    public async Task RealRcloneUsesReceiptAndSizeForReuseButBoundsDownloadAndFreshReadback()
    {
        var executable = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_RCLONE");
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable)) Assert.Inconclusive("Set FOLDERREWIND_TEST_RCLONE.");
        var root = Path.Combine(Path.GetTempPath(), "NativeTransferBounds", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var config = Path.Combine(root, "rclone.conf"); await File.WriteAllTextAsync(config, "[objects]\ntype = local\n");
            using var context = new RcloneExecutionContext(executable, config, root, "objects:repository", Path.Combine(root, "tasks"));
            var transport = new RcloneNativeHistoryTransport(context);
            var input = Path.Combine(root, "input"); await File.WriteAllBytesAsync(input, [1, 2, 3]);
            var hash = Convert.ToHexString(SHA256.HashData([1, 2, 3])); const string key = "config/object.7z";
            Assert.IsFalse(await transport.HasVerifiedObjectAsync(key, 3, hash, default));
            var budget = new ArtifactTransferBudget(3);
            await transport.UploadObjectAsync(key, input, 3, hash, budget, default);
            Assert.AreEqual(3L, budget.BytesRead);
            Assert.IsTrue(await transport.HasVerifiedObjectAsync(key, 3, hash, default));
            var remote = Path.Combine(root, "repository", "_folderrewind", "artifact-objects", "config", "object.7z");
            await File.WriteAllBytesAsync(remote, [3, 2, 1]);
            Assert.IsTrue(await transport.HasVerifiedObjectAsync(key, 3, hash, default), "Reuse is availability evidence; same-size damage needs explicit deep verification.");
            await File.WriteAllBytesAsync(remote, [1, 2, 3, 4]);
            var output = Path.Combine(root, "output");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => transport.DownloadObjectAsync(key, output, 3, new(3), default));
            Assert.IsFalse(File.Exists(output));
            await File.WriteAllTextAsync(output, "keep");
            await Assert.ThrowsAsync<IOException>(() => transport.DownloadObjectAsync(key, output, 4, new(4), default));
            Assert.AreEqual("keep", await File.ReadAllTextAsync(output));
            await File.WriteAllTextAsync(remote + ".verified.json", new string('x', 4097));
            Assert.IsFalse(await transport.HasVerifiedObjectAsync(key, 4, hash, default));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ProcessOutputLimitKillsTheWriterInsteadOfWaitingForTheTimeout()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "$s = 'x' * 8192; while ($true) { [Console]::Error.Write($s) }" }) start.ArgumentList.Add(argument);
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<IOException>(() => RcloneConnectionService.RunAsync(start, TimeSpan.FromSeconds(30), default));
        Assert.IsLessThan(15d, watch.Elapsed.TotalSeconds);
    }
}

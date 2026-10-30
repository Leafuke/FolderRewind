using System.Diagnostics;
using System.Security.Cryptography;
using FolderRewind.Services;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Tests;

[TestClass]
[TestCategory("ExternalTool")]
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
    public async Task RealRcloneUsesExistenceForReuseWithoutReadbackButBoundsRestoreDownloads()
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
            Assert.AreEqual(0L, budget.BytesRead);
            Assert.IsTrue(await transport.HasVerifiedObjectAsync(key, 3, hash, default));
            var remote = Path.Combine(root, "repository", "_folderrewind", "artifact-objects", "config", "object.7z");
            await File.WriteAllBytesAsync(remote, [3, 2, 1]);
            Assert.IsTrue(await transport.HasVerifiedObjectAsync(key, 3, hash, default), "Verification is disabled; reuse checks existence.");
            await File.WriteAllBytesAsync(remote, [1, 2, 3, 4]);
            var output = Path.Combine(root, "output");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => transport.DownloadObjectAsync(key, output, 3, new(3), default));
            Assert.IsFalse(File.Exists(output));
            await File.WriteAllTextAsync(output, "keep");
            await Assert.ThrowsAsync<IOException>(() => transport.DownloadObjectAsync(key, output, 4, new(4), default));
            Assert.AreEqual("keep", await File.ReadAllTextAsync(output));
            await File.WriteAllTextAsync(remote + ".verified.json", new string('x', 4097));
            Assert.IsTrue(await transport.HasVerifiedObjectAsync(key, 4, hash, default));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ProcessOutputLimitKillsTheWriterInsteadOfWaitingForTheTimeout()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FolderRewind-output-limit-tests"));
        var root = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
        Assert.IsTrue(root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(root);
        var ready = Path.Combine(root, "ready");
        var gate = Path.Combine(root, "go");
        static string Quote(string path) => "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var command = $"$ErrorActionPreference = 'Stop'; [IO.File]::WriteAllText({Quote(ready + ".tmp")}, $PID.ToString()); "
            + $"[IO.File]::Move({Quote(ready + ".tmp")}, {Quote(ready)}); "
            + $"while (-not [IO.File]::Exists({Quote(gate)})) {{ Start-Sleep -Milliseconds 10 }}; "
            + "$s = 'x' * 8192; while ($true) { [Console]::Error.Write($s) }";
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", command }) start.ArgumentList.Add(argument);
        using var cancellation = new CancellationTokenSource();
        Task<string>? run = null;
        try
        {
            var startup = Stopwatch.StartNew();
            run = RcloneConnectionService.RunAsync(start, TimeSpan.FromMinutes(2), cancellation.Token);
            while (!File.Exists(ready))
            {
                if (run.IsCompleted) { await run; Assert.Fail("Writer exited before reporting readiness."); }
                Assert.IsLessThan(60d, startup.Elapsed.TotalSeconds, "PowerShell did not report readiness.");
                await Task.Delay(20);
            }
            startup.Stop();
            using var writer = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(ready), System.Globalization.CultureInfo.InvariantCulture));
            var termination = Stopwatch.StartNew();
            await File.WriteAllTextAsync(gate, "go");
            var error = await Assert.ThrowsExactlyAsync<IOException>(() => run);
            termination.Stop();
            Console.WriteLine($"Output-limit writer PID={writer.Id}; startup={startup.Elapsed.TotalMilliseconds:F0}ms; termination={termination.Elapsed.TotalMilliseconds:F0}ms");
            Assert.AreEqual(I18n.GetString("CloudSetup_OutputTruncated"), error.Message);
            Assert.IsTrue(writer.HasExited, "The writer must be reaped before RunAsync completes.");
            Assert.IsLessThan(15d, termination.Elapsed.TotalSeconds);
        }
        finally
        {
            cancellation.Cancel();
            if (run is not null) { try { await run; } catch { /* Observe after cancellation and wait for process cleanup. */ } }
            Directory.Delete(root, recursive: true);
        }
        Assert.IsFalse(Directory.Exists(root), "The readiness fixture must be cleaned up.");
    }
}

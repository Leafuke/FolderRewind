using System.Diagnostics;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class ProcessWaitServiceTests
{
    [TestMethod]
    public async Task CallerCancellationIsNotTimeoutAndNextProcessCanComplete()
    {
        using var process = Start(wait: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            ProcessWaitService.WaitAsync(process, TimeSpan.FromSeconds(30), cancellation.Token));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var next = Start(wait: false);
        Assert.IsTrue(await ProcessWaitService.WaitAsync(next, TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [TestMethod]
    public async Task TimeoutTerminatesProcessAndReturnsFalse()
    {
        using var process = Start(wait: true);
        Assert.IsFalse(await ProcessWaitService.WaitAsync(process, TimeSpan.FromMilliseconds(150), CancellationToken.None));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(process.HasExited);
    }

    private static Process Start(bool wait) => Process.Start(new ProcessStartInfo
    {
        FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
        Arguments = wait ? "/d /c ping -n 30 127.0.0.1 >nul" : "/d /c exit 0",
        UseShellExecute = false,
        CreateNoWindow = true
    })!;
}

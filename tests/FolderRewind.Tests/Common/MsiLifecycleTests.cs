using FolderRewind.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO.Pipes;
using System.Text;

namespace FolderRewind.Tests;

[TestClass]
public sealed class MsiLifecycleTests
{
    [TestMethod]
    public void StartupEntryMustBelongToExactExecutableAndContainOnlyStartupArgument()
    {
        const string exe = @"C:\Programs\FolderRewind\FolderRewind.exe";
        Assert.IsTrue(ClassicStartupPolicy.IsOwnedCommand($"\"{exe}\" --startup", exe));
        Assert.IsFalse(ClassicStartupPolicy.IsOwnedCommand($"\"{exe}.malicious.exe\" --startup", exe));
        Assert.IsFalse(ClassicStartupPolicy.IsOwnedCommand($"\"{exe}\" --startup --other", exe));
        var other = @"C:\Other\FolderRewind.exe";
        Assert.IsFalse(ClassicStartupPolicy.IsOwnedCommand($"\"{other}\" --startup", exe));
        Assert.IsFalse(ClassicStartupPolicy.IsOwnedCommand(exe + " --startup", exe));
    }

    [TestMethod]
    public void StartupApprovedDistinguishesDisabledAndUnknownWithoutTreatingEitherAsEnabled()
    {
        const string exe = @"C:\Programs\FolderRewind\FolderRewind.exe";
        var command = $"\"{exe}\" --startup";
        var approval = new byte[12]; approval[0] = 3;
        Assert.AreEqual(ClassicStartupState.DisabledByUser, ClassicStartupPolicy.Evaluate(command, exe, approval));
        approval[0] = 2;
        Assert.AreEqual(ClassicStartupState.Enabled, ClassicStartupPolicy.Evaluate(command, exe, approval));
        approval[0] = 17;
        Assert.AreEqual(ClassicStartupState.Unknown, ClassicStartupPolicy.Evaluate(command, exe, approval));
        Assert.AreEqual(ClassicStartupState.Unknown, ClassicStartupPolicy.Evaluate(command, exe, new byte[1]));
        Assert.AreEqual(ClassicStartupState.Disabled, ClassicStartupPolicy.Evaluate(command, @"C:\Other\FolderRewind.exe", approval));
    }

    [TestMethod]
    public async Task PipeRejectsMalformedCommandsAndKeepsOneOwnerWhileStartupDoesNotActivate()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows named pipe ACL and global mutex test."); return; }
        var identity = "FolderRewind.Msi.Tests." + Guid.NewGuid().ToString("N");
        using var stop = new ManualResetEventSlim();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int activationCount = 0;
        Exception? threadError = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var primary = SingleInstanceCoordinator.TryAcquire(identity);
                if (primary == null) throw new InvalidOperationException("Primary could not own the instance.");
                primary.SetReady(() => { Interlocked.Increment(ref activationCount); return true; });
                ready.TrySetResult(true); stop.Wait();
            }
            catch (Exception error) { threadError = error; ready.TrySetException(error); }
        });
        thread.Start();
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(await Task.Run(() => { using var second = SingleInstanceCoordinator.TryAcquire(identity); return second == null; }));
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using (var pipe = new NamedPipeClientStream(".", identity, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await pipe.ConnectAsync(budget.Token);
                await pipe.ReadExactlyAsync(new byte[4], budget.Token);
                await pipe.WriteAsync(Encoding.ASCII.GetBytes("DELETE_CONFIG\n"), budget.Token);
                var rejected = new byte[1]; await pipe.ReadExactlyAsync(rejected, budget.Token);
                Assert.AreEqual((byte)0, rejected[0]);
            }
            Assert.IsTrue(await SingleInstanceCoordinator.NotifyAsync(identity, startup: true));
            Assert.AreEqual(0, activationCount);
            Assert.IsTrue(await SingleInstanceCoordinator.NotifyAsync(identity, startup: false));
            Assert.AreEqual(1, activationCount);
        }
        finally { stop.Set(); Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5))); }
        Assert.IsNull(threadError);
        using var recovered = SingleInstanceCoordinator.TryAcquire(identity);
        Assert.IsNotNull(recovered);
    }

    [TestMethod]
    public async Task ProtocolLimitsLengthAndRejectsBinaryOrIncompleteInput()
    {
        foreach (var request in new[] { new byte[] {0,10}, Encoding.ASCII.GetBytes(new string('A', 33) + "\n"), Encoding.ASCII.GetBytes("ACTIVATE") })
            Assert.IsNull(await SingleInstanceCoordinator.ReadRequestAsync(new MemoryStream(request), CancellationToken.None));
        Assert.AreEqual("ACTIVATE", await SingleInstanceCoordinator.ReadRequestAsync(new MemoryStream(Encoding.ASCII.GetBytes("ACTIVATE\n")), CancellationToken.None));
    }
}

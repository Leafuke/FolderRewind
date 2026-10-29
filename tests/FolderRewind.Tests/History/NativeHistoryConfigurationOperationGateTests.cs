using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class NativeHistoryConfigurationOperationGateTests
{
    [TestMethod]
    public async Task SameConfigurationSerializesWhileDifferentConfigurationRemainsIndependent()
    {
        var configId = Guid.NewGuid().ToString("D");
        var otherConfigId = Guid.NewGuid().ToString("D");
        var first = await NativeHistoryConfigurationOperationGate.EnterAsync(configId);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = Task.Run(async () =>
        {
            await using var lease = await NativeHistoryConfigurationOperationGate.EnterAsync(configId);
            secondEntered.SetResult();
        });

        await using (await NativeHistoryConfigurationOperationGate.EnterAsync(otherConfigId))
        {
            Assert.IsFalse(secondEntered.Task.IsCompleted);
        }
        Assert.IsFalse(secondEntered.Task.IsCompleted);

        await first.DisposeAsync();
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(secondEntered.Task.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task RecoveryLeaseMustBeActiveAndBelongToConfiguration()
    {
        var id = new FolderRewind.History.Domain.HistoryConfigId(Guid.NewGuid().ToString());
        var lease = await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(id);
        lease.Require(id);
        Assert.ThrowsExactly<InvalidOperationException>(() => lease.Require(new("another-config")));
        var waiting = NativeHistoryConfigurationOperationGate.EnterHistoryAsync(id).AsTask();
        Assert.IsFalse(waiting.IsCompleted);
        await lease.DisposeAsync();
        await using var next = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.ThrowsExactly<InvalidOperationException>(() => lease.Require(id));
    }
}

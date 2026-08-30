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
        await Task.Delay(50);
        Assert.IsFalse(secondEntered.Task.IsCompleted);

        await first.DisposeAsync();
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(secondEntered.Task.IsCompletedSuccessfully);
    }
}

using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class PluginStoreOperationControllerTests
{
    [TestMethod]
    public async Task PickerAndInstallationShareOneGateAndDoNotCancelEachOther()
    {
        using var controller = new PluginStoreOperationController(_ => Assert.Fail());
        controller.Activate();
        var picker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var install = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken admitted = default;
        var pending = controller.RunAsync(async (token, _) =>
        {
            admitted = token;
            await picker.Task;
            await install.Task;
        });
        await controller.RunAsync((_, _) => throw new Exception("Overlapping picker/refresh must be rejected"));
        picker.SetResult();
        Assert.IsTrue(controller.IsBusy);
        await controller.RunAsync((_, _) => throw new Exception("Overlapping install/enable must be rejected"));
        Assert.IsFalse(admitted.IsCancellationRequested);
        install.SetResult();
        await pending;
        Assert.IsTrue(controller.CanExecute);
    }

    [TestMethod]
    public async Task NavigationInvalidatesLateResultsEvenAfterReactivation()
    {
        using var controller = new PluginStoreOperationController(_ => Assert.Fail());
        controller.Activate();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = false;
        var pending = controller.RunAsync(async (token, isCurrent) =>
        {
            await completion.Task; // An admitted transaction can complete despite cancellation.
            Assert.IsTrue(token.IsCancellationRequested);
            if (isCurrent()) applied = true;
        });
        controller.Deactivate();
        controller.Activate();
        Assert.IsFalse(controller.CanExecute);
        completion.SetResult();
        await pending;
        Assert.IsFalse(applied);
        Assert.IsTrue(controller.CanExecute);
    }

    [TestMethod]
    public async Task CancellationIsQuietAndFailureReleasesGate()
    {
        var errors = new List<Exception>();
        using var controller = new PluginStoreOperationController(errors.Add);
        controller.Activate();
        var pending = controller.RunAsync((token, _) => Task.Delay(Timeout.Infinite, token));
        controller.Cancel();
        await pending;
        Assert.HasCount(0, errors);
        await controller.RunAsync((_, _) => throw new IOException("failed"));
        Assert.HasCount(1, errors);
        Assert.IsTrue(controller.CanExecute);
    }
}

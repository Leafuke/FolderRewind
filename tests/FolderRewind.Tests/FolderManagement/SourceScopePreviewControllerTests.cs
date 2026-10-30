using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SourceScopePreviewControllerTests
{
    private static BackupSourceScope Scope() => new() { Mode = BackupSourceScopeMode.Include };
    private static TaskCompletionSource<SourceScopePreviewResult> Completion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [TestMethod]
    public async Task ReversedCompletionOrderPublishesOnlyLatestResult()
    {
        var first = Completion(); var second = Completion(); var calls = 0;
        using var controller = new SourceScopePreviewController((_, _, _) => ++calls == 1 ? first.Task : second.Task);
        var older = controller.RefreshAsync("C:\\data", Scope());
        var latest = controller.RefreshAsync("C:\\data", Scope());
        second.SetResult(new(2, 200));
        await latest;
        first.SetResult(new(1, 100));
        await older;
        Assert.AreEqual(new SourceScopePreviewResult(2, 200), controller.Result);
        Assert.IsFalse(controller.IsPreviewing);
    }

    [TestMethod]
    public async Task InvalidAndBroadRootRequestsImmediatelyClearBusyAndIgnoreOldFailure()
    {
        var completion = Completion();
        using var controller = new SourceScopePreviewController((_, _, _) => completion.Task);
        var pending = controller.RefreshAsync("C:\\data", Scope());
        Assert.IsTrue(controller.IsPreviewing);
        await controller.RefreshAsync("C:\\data", null, "invalid rule");
        Assert.IsFalse(controller.IsPreviewing);
        Assert.AreEqual("invalid rule", controller.Error);
        completion.SetException(new IOException("old failure"));
        await pending;
        Assert.AreEqual(SourceScopePreviewState.Invalid, controller.State);
        await controller.RefreshAsync("C:\\", new BackupSourceScope());
        Assert.AreEqual(SourceScopePreviewState.Skipped, controller.State);
        Assert.IsFalse(controller.IsPreviewing);
    }

    [TestMethod]
    public async Task CancelAndDisposeSuppressLateUpdatesEvenWhenEnumeratorIgnoresToken()
    {
        var completion = Completion();
        using var controller = new SourceScopePreviewController((_, _, _) => completion.Task);
        var pending = controller.RefreshAsync("C:\\data", Scope());
        controller.Cancel();
        Assert.IsFalse(controller.IsPreviewing);
        Assert.AreEqual(SourceScopePreviewState.Canceled, controller.State);
        var changes = 0;
        controller.StateChanged += () => changes++;
        controller.Dispose();
        completion.SetResult(new(3, 300));
        await pending;
        await controller.RefreshAsync("C:\\data", Scope());
        Assert.AreEqual(0, changes);
        Assert.IsNull(controller.Result);
    }

    [TestMethod]
    public async Task FailureCanBeRetriedAndCancellationIsImmediate()
    {
        var calls = 0;
        using var controller = new SourceScopePreviewController((_, _, token) =>
            ++calls == 1 ? Task.FromException<SourceScopePreviewResult>(new IOException("denied"))
                : WaitAsync(token));
        await controller.RefreshAsync("C:\\data", Scope());
        Assert.AreEqual(SourceScopePreviewState.Failed, controller.State);
        Assert.AreEqual("denied", controller.Error);
        var retry = controller.RefreshAsync("C:\\data", Scope());
        controller.Cancel();
        Assert.IsFalse(controller.IsPreviewing);
        await retry;
        Assert.AreEqual(SourceScopePreviewState.Canceled, controller.State);

        static async Task<SourceScopePreviewResult> WaitAsync(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return new(0, 0);
        }
    }
}

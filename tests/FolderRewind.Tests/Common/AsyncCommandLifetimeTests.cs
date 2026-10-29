using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class AsyncCommandLifetimeTests
{
    [TestMethod]
    public async Task SlowCommandRejectsOverlapAndRecovers()
    {
        using var lifetime = new AsyncCommandLifetime(_ => Assert.Fail());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var first = lifetime.RunAsync(async _ => { calls++; await release.Task; });
        Assert.IsTrue(lifetime.IsBusy);
        await lifetime.RunAsync(_ => { calls++; return Task.CompletedTask; });
        Assert.AreEqual(1, calls);
        release.SetResult();
        await first;
        Assert.IsTrue(lifetime.CanExecute);
    }

    [TestMethod]
    public async Task DeactivationCancelsWithoutErrorAndCanReactivate()
    {
        using var lifetime = new AsyncCommandLifetime(_ => Assert.Fail());
        var first = lifetime.RunAsync(token => Task.Delay(Timeout.InfiniteTimeSpan, token));
        lifetime.Deactivate();
        await first;
        Assert.IsFalse(lifetime.CanExecute);
        lifetime.Activate();
        var called = false;
        await lifetime.RunAsync(_ => { called = true; return Task.CompletedTask; });
        Assert.IsTrue(called);
    }

    [TestMethod]
    public async Task FailureIsObservedAndNextCommandStillRuns()
    {
        var errors = new List<Exception>();
        using var lifetime = new AsyncCommandLifetime(errors.Add);
        await lifetime.RunAsync(_ => throw new IOException("failure"));
        Assert.HasCount(1, errors);
        Assert.IsTrue(lifetime.CanExecute);
        await lifetime.RunAsync(_ => Task.CompletedTask);
    }

    [TestMethod]
    public async Task DisposeCancelsAndRejectsNewWork()
    {
        var lifetime = new AsyncCommandLifetime(_ => Assert.Fail());
        var operation = lifetime.RunAsync(token => Task.Delay(Timeout.InfiniteTimeSpan, token));
        lifetime.Dispose();
        lifetime.Dispose();
        await operation;
        await lifetime.RunAsync(_ => throw new Exception("must not execute"));
        Assert.IsFalse(lifetime.CanExecute);
    }
}

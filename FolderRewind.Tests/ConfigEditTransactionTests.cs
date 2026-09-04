using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class ConfigEditTransactionTests
{
    [TestMethod]
    public async Task SuccessfulSaveKeepsEditAndDoesNotRollback()
    {
        var value = "before";
        var rollbackCalled = false;
        await ConfigEditTransaction.ApplyAsync(
            () => value = "after",
            () => rollbackCalled = true,
            () =>
            {
                Assert.AreEqual("after", value);
                return Task.FromResult(new ConfigSaveResult { Success = true });
            },
            "failed");

        Assert.AreEqual("after", value);
        Assert.IsFalse(rollbackCalled);
    }

    [TestMethod]
    public async Task ExplicitFailureRollsBackBeforeThrowing()
    {
        var configs = new List<string> { "first", "second", "third" };
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => ConfigEditTransaction.ApplyAsync(
            () => configs.RemoveAt(1),
            () => configs.Insert(1, "second"),
            FailOnce(new ConfigSaveResult { ErrorMessage = "disk full" }),
            "failed"));

        Assert.AreEqual("disk full", error.Message);
        CollectionAssert.AreEqual(new[] { "first", "second", "third" }, configs);
    }

    [TestMethod]
    public async Task FailedCreationRemovesUncommittedConfig()
    {
        var configs = new List<string> { "existing" };
        var navigationCalled = false;
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
        {
            await ConfigEditTransaction.ApplyAsync(
                () => configs.Add("new"),
                () => configs.Remove("new"),
                FailOnce(new ConfigSaveResult()),
                "localized failure");
            navigationCalled = true;
        });

        CollectionAssert.AreEqual(new[] { "existing" }, configs);
        Assert.IsFalse(navigationCalled);
    }

    [TestMethod]
    public async Task ThrownSaveFailureRestoresEdit()
    {
        var value = "before";
        var saveCount = 0;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ConfigEditTransaction.ApplyAsync(
            () => value = "after",
            () => value = "before",
            () => ++saveCount == 1
                ? throw new InvalidOperationException("writer failed")
                : Task.FromResult(new ConfigSaveResult { Success = true }),
            "failed"));

        Assert.AreEqual("before", value);
    }

    [TestMethod]
    public async Task CancellationBeforeEditDoesNotMutateOrSave()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => ConfigEditTransaction.ApplyAsync(
            () => calls++,
            () => calls++,
            () =>
            {
                calls++;
                return Task.FromResult(new ConfigSaveResult { Success = true });
            },
            "failed",
            cancellation.Token));

        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task CancellationAfterEditWaitsForDurableResult()
    {
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<ConfigSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var value = "before";
        var operation = ConfigEditTransaction.ApplyAsync(
            () => value = "after",
            () => value = "before",
            () => completion.Task,
            "failed",
            cancellation.Token);
        cancellation.Cancel();

        Assert.IsFalse(operation.IsCompleted);
        completion.SetResult(new ConfigSaveResult { Success = true });
        await operation;
        Assert.AreEqual("after", value);
    }

    [TestMethod]
    public async Task SaveFailureAfterCancellationStillRestoresEdit()
    {
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<ConfigSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var value = "before";
        var saveCount = 0;
        var operation = ConfigEditTransaction.ApplyAsync(
            () => value = "after",
            () => value = "before",
            () => ++saveCount == 1 ? completion.Task : Task.FromResult(new ConfigSaveResult { Success = true }),
            "failed",
            cancellation.Token);
        cancellation.Cancel();
        completion.SetResult(new ConfigSaveResult());

        await Assert.ThrowsExactlyAsync<IOException>(() => operation);
        Assert.AreEqual("before", value);
        Assert.AreEqual(2, saveCount);
    }

    [TestMethod]
    public async Task CompensatedSnapshotFollowsConcurrentTemporarySnapshot()
    {
        var value = "before";
        var written = string.Empty;
        var firstWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeCount = 0;
        await using var writer = new ConfigWriteCoordinator(async (payload, _) =>
        {
            if (Interlocked.Increment(ref writeCount) == 1)
            {
                firstWriteStarted.SetResult();
                await releaseFirstWrite.Task;
                throw new IOException("first write failed");
            }
            written = System.Text.Encoding.UTF8.GetString(payload.Span);
        });
        Task<ConfigSaveResult> Save() => writer.EnqueueAsync(System.Text.Encoding.UTF8.GetBytes(value), false);
        var transaction = ConfigEditTransaction.ApplyAsync(() => value = "after", () => value = "before", Save, "failed");
        await firstWriteStarted.Task;
        var concurrentSave = Save(); // Another producer captures the temporary edit.
        releaseFirstWrite.SetResult();

        await Assert.ThrowsExactlyAsync<IOException>(() => transaction);
        Assert.IsTrue((await concurrentSave).Success);
        await writer.FlushAsync();
        Assert.AreEqual("before", written);
        Assert.AreEqual("before", value);
    }

    [TestMethod]
    public async Task CompensationFailureReportsBothErrors()
    {
        var value = "before";
        var calls = 0;
        var error = await Assert.ThrowsExactlyAsync<ConfigEditRollbackException>(() => ConfigEditTransaction.ApplyAsync(
            () => value = "after",
            () => value = "before",
            () => Task.FromResult(new ConfigSaveResult { ErrorMessage = $"write {++calls} failed" }),
            "localized failure"));

        Assert.AreEqual("before", value);
        Assert.AreEqual(2, calls);
        var aggregate = (AggregateException)error.InnerException!;
        Assert.AreEqual("write 1 failed", aggregate.InnerExceptions[0].Message);
        Assert.AreEqual("write 2 failed", aggregate.InnerExceptions[1].Message);
    }

    private static Func<Task<ConfigSaveResult>> FailOnce(ConfigSaveResult failure)
    {
        var count = 0;
        return () => Task.FromResult(++count == 1 ? failure : new ConfigSaveResult { Success = true });
    }
}

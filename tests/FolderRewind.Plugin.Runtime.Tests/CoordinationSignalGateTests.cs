using FolderRewind.Plugin.Runtime.Operations;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class CoordinationSignalGateTests
{
    [TestMethod]
    public async Task BorrowNeverAcquiresOrWaitsAndCannotReleaseTheParent()
    {
        var gate = new CoordinationSignalGate(); var owner = Guid.NewGuid();
        Assert.IsNull(gate.TryBorrow(owner)); Assert.IsFalse(gate.IsBusy);
        await using var parent = await gate.EnterAsync(owner, default);
        Assert.IsNull(gate.TryBorrow(Guid.NewGuid()));
        var borrowed = gate.TryBorrow(owner); Assert.IsNotNull(borrowed);
        Assert.IsFalse(borrowed.OwnsChannel);
        await borrowed.DisposeAsync(); Assert.IsTrue(gate.IsOwnedBy(owner));
        gate.MarkUncertain(); Assert.IsNull(gate.TryBorrow(owner));
        await parent.DisposeAsync(); Assert.IsFalse(gate.IsBusy);
    }

    [TestMethod]
    public async Task OwnTerminalPrecedesUnrelatedTerminalAndNextRunWithoutNestedDeadlock()
    {
        var gate = new CoordinationSignalGate(); var owner = Guid.NewGuid(); var sent = new List<string>();
        var lease = await gate.EnterAsync(owner, default);
        await using (await gate.EnterAsync(owner, default)) { }
        Assert.IsTrue(await gate.DispatchAsync(null, _ => { sent.Add("unrelated"); return Task.FromResult(true); }, default));
        Assert.IsTrue(await gate.DispatchAsync(owner, _ => { sent.Add("owner"); return Task.FromResult(true); }, default));
        var next = gate.EnterAsync(Guid.NewGuid(), default).AsTask(); Assert.IsFalse(next.IsCompleted);
        Assert.IsEmpty(sent);
        Assert.IsTrue(await gate.FlushOwnerAsync(owner, default));
        await lease.DisposeAsync(); await using var following = await next;
        CollectionAssert.AreEqual(new[] { "owner", "unrelated" }, sent);
    }

    [TestMethod]
    public async Task FailedTerminalRequiresIdleResetAndCannotLeakIntoNextRun()
    {
        var gate = new CoordinationSignalGate(); var owner = Guid.NewGuid(); var fail = true;
        var lease = await gate.EnterAsync(owner, default);
        await gate.DispatchAsync(owner, _ => Task.FromResult(!fail), default);
        Assert.IsFalse(await gate.FlushOwnerAsync(owner, default));
        Assert.IsFalse(await gate.ResetAsync(default));
        await lease.DisposeAsync();
        Assert.IsTrue(gate.NeedsReset);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.EnterAsync(Guid.NewGuid(), default).AsTask());
        fail = false; Assert.IsTrue(await gate.ResetAsync(default)); Assert.IsFalse(gate.NeedsReset);
        await using var next = await gate.EnterAsync(Guid.NewGuid(), default);
    }

    [TestMethod]
    public async Task IdleSenderCannotCrossAdmissionAndActualSendReceivesCancellation()
    {
        var gate = new CoordinationSignalGate(); var started = new TaskCompletionSource();
        using var cancellation = new CancellationTokenSource();
        var observedCancellation = false;
        var send = gate.DispatchAsync(null, async token =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); return true; }
            catch (OperationCanceledException) { observedCancellation = true; throw; }
        }, cancellation.Token);
        await started.Task;
        var admission = gate.EnterAsync(Guid.NewGuid(), default).AsTask();
        Assert.IsFalse(admission.IsCompleted);
        cancellation.Cancel();
        Assert.IsFalse(await send); Assert.IsTrue(observedCancellation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => admission);
        Assert.IsTrue(gate.NeedsReset);
    }

    [TestMethod]
    public async Task QueueIsBoundedAndAnActiveDisconnectInvalidatesItsOwner()
    {
        var gate = new CoordinationSignalGate(); var owner = Guid.NewGuid();
        await using var admission = await gate.EnterAsync(owner, default);
        var invalidated = admission.Invalidated;
        gate.MarkUncertain(); Assert.IsTrue(invalidated.IsCancellationRequested);
        for (var index = 0; index < 1024; index++)
            Assert.IsTrue(await gate.DispatchAsync(null, _ => Task.FromResult(true), default));
        Assert.IsFalse(await gate.DispatchAsync(null, _ => Task.FromResult(true), default));
        Assert.IsFalse(await admission.CompleteAsync(default));
        Assert.IsTrue(await gate.ResetAsync(default));
        await using var next = await gate.EnterAsync(Guid.NewGuid(), default);
        Assert.IsFalse(next.Invalidated.IsCancellationRequested);
    }
}

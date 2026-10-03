using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Operations;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class RestoreMutationContinuationGateTests
{
    [TestMethod]
    public async Task CallbackEndClosesUnusedContinuation()
    {
        var calls = 0;
        var gate = new RestoreMutationContinuationGate(_ =>
        {
            calls++;
            return ValueTask.FromResult(OperationOutcome.Success);
        });
        await gate.CloseAndDrainAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => gate.InvokeAsync(default).AsTask());
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task CallbackEndWaitsForStartedMutationAndPreservesRecoveryResult()
    {
        var release = new TaskCompletionSource<OperationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new RestoreMutationContinuationGate(_ => new(release.Task));
        var mutation = gate.InvokeAsync(default).AsTask();
        var drain = gate.CloseAndDrainAsync();
        Assert.IsFalse(drain.IsCompleted);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => gate.InvokeAsync(default).AsTask());
        release.SetResult(OperationOutcome.RecoveryRequired);
        await drain;
        Assert.AreEqual(OperationOutcome.RecoveryRequired, await mutation);
    }

    [TestMethod]
    public void ProposalsRejectEscapesAndConflictsAndFreezePluginMemory()
    {
        var bytes = new byte[] { 1 };
        var result = new RestoreStagingPreparationResult([new("level.dat", bytes)], []);
        var frozen = RestoreStagingProposalValidator.ValidateAndFreeze(result, _ => true);
        bytes[0] = 2;
        Assert.AreEqual((byte)1, frozen[0].Content.Span[0]);
        foreach (var files in new RestoreStagedFileProposal[][]
        {
            [new("../escape", bytes)],
            [new("file", bytes), new("FILE", bytes)],
            [new("dir", bytes), new("dir/file", bytes)]
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                RestoreStagingProposalValidator.ValidateAndFreeze(new(files, []), _ => true));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RestoreStagingProposalValidator.ValidateAndFreeze(result, _ => false));
    }
}

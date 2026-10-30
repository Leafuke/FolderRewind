using System.Threading;
using System.Threading.Tasks;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.History.Representation;

internal sealed class NativeArtifactCaptureCommitParticipant(NativeArtifactStore store, NativeArtifactPending intent) : ICaptureCommitParticipant
{
    public void EndOperation() => store.ReleasePendingOperation(intent.Id);

    public async Task PrepareCommitAsync(PackId packId, HistoryTransactionId transactionId, CancellationToken token)
    {
        using var held = await store.EnterAsync(token);
        intent = intent with { HistoryPackId = packId.ToString(), HistoryTransactionId = transactionId.ToString(), Phase = "ArtifactGraphCommitted" };
        await store.SavePendingAsync(intent, token);
    }

    public async Task AbortCommitAsync(CancellationToken token)
    {
        using var held = await store.EnterAsync(token);
        try { store.CompletePending(intent.Id); }
        finally { EndOperation(); }
    }

    public async Task HistoryCommittedAsync(CancellationToken token)
    {
        using var held = await store.EnterAsync(token);
        await store.SavePendingAsync(intent with { Phase = "HistoryPackCommitted" }, token);
    }

    public async Task CompleteCommitAsync(CancellationToken token)
    {
        using var held = await store.EnterAsync(token);
        await store.SavePendingAsync(intent with { Phase = "LocalStateApplied" }, token);
        await store.SavePendingAsync(intent with { Phase = "Complete" }, token);
        store.CompletePending(intent.Id);
    }
}

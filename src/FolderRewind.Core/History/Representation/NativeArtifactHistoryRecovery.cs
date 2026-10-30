using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.History.Representation;

public static class NativeArtifactHistoryRecovery
{
    public static async Task RecoverAsync(HistoryRuntime history, NativeArtifactStore store, CancellationToken token)
    {
        var packs = await history.Repository.ReadAllPacksAsync(token);
        var codec = new HistoryPackCodec();
        await store.RecoverAsync((intent, _) =>
        {
            if (intent.HistoryPackId is null) return Task.FromResult(false);
            var pack = packs.SingleOrDefault(p => p.Pack.PackId.ToString() == intent.HistoryPackId)?.Pack;
            if (pack is null) return Task.FromResult(false);
            if (pack.TransactionId.ToString() != intent.HistoryTransactionId)
                throw new InvalidDataException("Native pending transaction disagrees with immutable History.");
            var representation = pack.Objects.Where(o => o.Kind == HistoryObjectKinds.VersionRepresentation)
                .Select(o => (VersionRepresentation)codec.DeserializeKnown(o))
                .SingleOrDefault(r => r.RepresentationId.ToString() == intent.RepresentationId);
            if (representation is null || representation.VersionId.ToString() != intent.VersionId
                || !representation.RepresentationSpecificMetadata.TryGetValue("artifactRootId", out var root)
                || !Guid.TryParse(root, out var id) || id != intent.Root.Value)
                throw new InvalidDataException("Native pending root is not bound by its committed pack.");
            return Task.FromResult(true);
        }, token);
    }
}

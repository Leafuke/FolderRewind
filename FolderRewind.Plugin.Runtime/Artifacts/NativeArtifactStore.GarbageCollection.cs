using System.Security.Cryptography;
using System.Text;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Artifacts;

public sealed record NativeArtifactReclaimedObject(ArtifactId ArtifactId, Guid FolderId, long PhysicalBytes);
public sealed record NativeArtifactCollectionReceipt(string Id, IReadOnlyList<NativeArtifactReclaimedObject> Objects);

public sealed partial class NativeArtifactStore
{
    private sealed record GarbageEntry(ArtifactLedgerEntry Node, long PhysicalBytes);
    private sealed record GarbageIntent(int Version, string Id, string RootHash, IReadOnlyList<GarbageEntry> Entries, bool Complete);
    private string GarbageRoot => Path.Combine(Root, "gc");
    private string GarbageRootHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        OperatingSystem.IsWindows() ? Root.ToUpperInvariant() : Root)));

    private GarbageIntent[] ReadGarbageIntents()
    {
        if (!Directory.Exists(GarbageRoot)) return [];
        HostArtifactReadService.EnsureNoReparsePoints(Root, GarbageRoot);
        var paths = Directory.EnumerateFiles(GarbageRoot, "*.json").Take(4097).ToArray();
        if (paths.Length > 4096) throw new InvalidDataException("Native collection receipt quota exceeded.");
        long metadataBytes = 0; int entries = 0;
        var intents = paths.Select(path =>
        {
            var bytes = ArtifactMetadataFile.ReadBytes(path, 16 * 1024 * 1024);
            metadataBytes += bytes.Length;
            if (metadataBytes > 16 * 1024 * 1024) throw new InvalidDataException("Native collection metadata quota exceeded.");
            var intent = System.Text.Json.JsonSerializer.Deserialize<GarbageIntent>(bytes)
                ?? throw new InvalidDataException("Empty Native collection intent.");
            if (intent.Version != 1 || intent.RootHash != GarbageRootHash || intent.Entries is null
                || !Guid.TryParseExact(intent.Id, "N", out _) || intent.Id != Path.GetFileNameWithoutExtension(path)
                || intent.Entries.Any(e => e is null || e.Node is null || e.PhysicalBytes < 0
                    || e.Node.ContentRelativePath != "artifacts/" + e.Node.ArtifactId.Value.ToString("N")))
                throw new InvalidDataException("Invalid Native collection ownership evidence.");
            entries += intent.Entries.Count;
            if (entries > 4096) throw new InvalidDataException("Native collection entry quota exceeded.");
            ArtifactLedgerValidator.Validate(new(ArtifactLedgerValidator.CurrentSchemaVersion, new("gc"),
                intent.Entries.Select(e => e.Node with { Dependencies = [] }).ToArray()));
            return intent;
        }).ToArray();
        return intents;
    }

    // Caller owns the store gate. A prepared intent only authorizes bytes whose nodes are absent from the current graph.
    private async Task RecoverGarbageWithoutGateAsync(CancellationToken token)
    {
        var intents = ReadGarbageIntents(); // Validate the complete set before any physical deletion.
        var graph = await Ledger.LoadAsync(token);
        var currentIds = graph.Artifacts.Select(a => a.ArtifactId).ToHashSet();
        var currentPaths = graph.Artifacts.Select(a => a.ContentRelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var intent in intents.Where(i => !i.Complete))
        {
            var retired = intent.Entries.Where(e => !currentIds.Contains(e.Node.ArtifactId)).ToArray();
            foreach (var entry in retired)
            {
                token.ThrowIfCancellationRequested();
                if (currentPaths.Contains(entry.Node.ContentRelativePath))
                    throw new InvalidDataException("Native collection path was adopted by another live node.");
                HostArtifactWorkspace.DeleteOwnedDirectory(ArtifactPathRules.ResolveUnderRoot(Root, entry.Node.ContentRelativePath));
                _observe?.Invoke("GcPhysicalDelete:after");
            }
            if (retired.Length == 0) File.Delete(Path.Combine(GarbageRoot, intent.Id + ".json"));
            else await WriteAsync(Path.Combine(GarbageRoot, intent.Id + ".json"), intent with { Entries = retired, Complete = true }, token);
        }
    }

    public async Task<IReadOnlyList<NativeArtifactCollectionReceipt>> CollectDetailedAsync(IEnumerable<ArtifactId> roots, CancellationToken token)
    {
        using var held = await EnterAsync(token);
        return await CollectDetailedWithoutGateAsync(roots, token);
    }

    private async Task<IReadOnlyList<NativeArtifactCollectionReceipt>> CollectDetailedWithoutGateAsync(IEnumerable<ArtifactId> roots, CancellationToken token)
    {
        await RecoverGarbageWithoutGateAsync(token);
        var ledger = await Ledger.LoadAsync(token);
        var ids = ledger.Artifacts.Select(a => a.ArtifactId).ToHashSet();
        var pending = Pending();
        var transferRoots = Transfers().Where(t => !t.Complete).Select(t => t.Root).ToArray();
        var explicitRoots = roots.Distinct().ToArray();
        if (explicitRoots.Any(root => !ids.Contains(root)))
            throw new InvalidDataException("A protected Native root is absent; collection safety cannot be proved.");
        var protectedRoots = explicitRoots.Concat(pending.Select(p => p.Root)).Concat(transferRoots).Where(ids.Contains).Distinct().ToArray();
        var garbage = (await Ledger.GarbageCollectUnreachableAsync(protectedRoots, dryRun: true, cancellationToken: token)).ToHashSet();
        if (garbage.Count > 0)
        {
            var entries = ledger.Artifacts.Where(a => garbage.Contains(a.ArtifactId)).Select(node =>
            {
                var directory = ArtifactPathRules.ResolveUnderRoot(Root, node.ContentRelativePath);
                HostArtifactReadService.EnsureNoReparsePoints(Root, directory);
                if (node.ContentRelativePath != "artifacts/" + node.ArtifactId.Value.ToString("N"))
                    throw new InvalidDataException("Native collection target is outside its object namespace.");
                var bytes = Directory.Exists(directory) ? HostArtifactReadService.EnumerateSafeFiles(directory).Sum(f => new FileInfo(f.FullPath).Length) : 0;
                return new GarbageEntry(node, bytes);
            }).ToArray();
            var intent = new GarbageIntent(1, Guid.NewGuid().ToString("N"), GarbageRootHash, entries, false);
            var previous = ReadGarbageIntents();
            var metadataSize = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(intent).Length;
            if (previous.Sum(i => i.Entries.Count) + entries.Length > 4096
                || metadataSize + (Directory.Exists(GarbageRoot) ? Directory.EnumerateFiles(GarbageRoot, "*.json").Sum(p => new FileInfo(p).Length) : 0) > 16 * 1024 * 1024)
            {
                if (previous.Any(i => i.Complete))
                    return previous.Where(i => i.Complete).Select(i => new NativeArtifactCollectionReceipt(i.Id,
                        i.Entries.Select(e => new NativeArtifactReclaimedObject(e.Node.ArtifactId, e.Node.FolderId, e.PhysicalBytes)).ToArray())).ToArray();
                throw new InvalidDataException("Native collection metadata must fit its bounded recovery budget.");
            }
            await WriteAsync(Path.Combine(GarbageRoot, intent.Id + ".json"), intent, token);
            _observe?.Invoke("GcPrepared:after");
            await Ledger.GarbageCollectUnreachableAsync(protectedRoots, cancellationToken: token, retainPayloads: true);
            _observe?.Invoke("GcGraphCommitted:after");
            await RecoverGarbageWithoutGateAsync(token);
        }
        return ReadGarbageIntents().Where(i => i.Complete).Select(i => new NativeArtifactCollectionReceipt(i.Id,
            i.Entries.Select(e => new NativeArtifactReclaimedObject(e.Node.ArtifactId, e.Node.FolderId, e.PhysicalBytes)).ToArray())).ToArray();
    }

    // The prepared capture still owns the store gate; preserve every node that preceded this attempt.
    internal async Task DiscardPreparedArtifactsAsync(ArtifactLedgerDocument before, NativeArtifactPending pending, CancellationToken token)
    {
        var earlierReceipts = ReadGarbageIntents().Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        CompletePending(pending.Id);
        var receipts = await CollectDetailedWithoutGateAsync(before.Artifacts.Select(a => a.ArtifactId), token);
        var priorIds = before.Artifacts.Select(a => a.ArtifactId).ToHashSet();
        if ((await Ledger.LoadAsync(token)).Artifacts.Any(a => !priorIds.Contains(a.ArtifactId)))
            throw new IOException("Rejected Native candidate could not be reclaimed within the recovery budget.");
        foreach (var receipt in receipts.Where(r => !earlierReceipts.Contains(r.Id)))
            File.Delete(Path.Combine(GarbageRoot, receipt.Id + ".json"));
    }

    public async Task AcknowledgeCollectionsAsync(IEnumerable<string> ids, CancellationToken token)
    {
        using var held = await EnterAsync(token);
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        foreach (var intent in ReadGarbageIntents().Where(i => i.Complete && wanted.Contains(i.Id)))
            File.Delete(Path.Combine(GarbageRoot, intent.Id + ".json"));
    }

    public async Task<IReadOnlyList<ArtifactId>> CollectAsync(IEnumerable<ArtifactId> roots, CancellationToken token)
    {
        var receipts = await CollectDetailedAsync(roots, token);
        await AcknowledgeCollectionsAsync(receipts.Select(r => r.Id), token);
        return receipts.SelectMany(r => r.Objects.Select(o => o.ArtifactId)).Distinct().ToArray();
    }
}

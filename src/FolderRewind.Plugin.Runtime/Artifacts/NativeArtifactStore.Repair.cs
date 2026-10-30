using System.Security.Cryptography;
using System.Text.Json;

namespace FolderRewind.Plugin.Runtime.Artifacts;

public sealed partial class NativeArtifactStore
{
    private sealed record RepairIntent(int Version, string Id, string RootHash, ArtifactLedgerEntry Node, NativeEnvelope Envelope);
    private string RepairRoot => Path.Combine(Root, "repairs");

    private RepairIntent[] ReadRepairIntents()
    {
        if (!Directory.Exists(RepairRoot)) return [];
        HostArtifactReadService.EnsureNoReparsePoints(Root, RepairRoot);
        var paths = Directory.EnumerateFiles(RepairRoot, "*.json").Take(4097).ToArray();
        if (paths.Length > 4096 || paths.Sum(p => new FileInfo(p).Length) > 16 * 1024 * 1024)
            throw new InvalidDataException("Native repair metadata quota exceeded.");
        return paths.Select(path =>
        {
            var intent = ArtifactMetadataFile.ReadJson<RepairIntent>(path, 1024 * 1024)
                ?? throw new InvalidDataException("Empty Native repair intent.");
            if (intent.Version != 1 || intent.RootHash != GarbageRootHash || intent.Node is null || intent.Envelope is not { Version: 1 }
                || !Guid.TryParseExact(intent.Id, "N", out _) || intent.Id != Path.GetFileNameWithoutExtension(path)
                || intent.Node.ContentRelativePath != "artifacts/" + intent.Node.ArtifactId.Value.ToString("N"))
                throw new InvalidDataException("Invalid Native repair ownership evidence.");
            ArtifactLedgerValidator.Validate(new(ArtifactLedgerValidator.CurrentSchemaVersion, new("repair"),
                [intent.Node with { Dependencies = [] }]));
            return intent;
        }).ToArray();
    }

    private async Task<bool> IsRepairPayloadAsync(string directory, RepairIntent intent, CancellationToken token)
    {
        var node = intent.Node;
        HostArtifactReadService.EnsureNoReparsePoints(Root, directory);
        var payload = Path.Combine(directory, "payload.7z");
        if (!File.Exists(payload) || new FileInfo(payload).Length != node.StorageSize) return false;
        var envelope = Path.Combine(directory, "envelope.json");
        if (!File.Exists(envelope) || ArtifactMetadataFile.ReadJson<NativeEnvelope>(envelope, 4096) != intent.Envelope) return false;
        await using var input = File.OpenRead(payload);
        return StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(await SHA256.HashDataAsync(input, token)), node.StorageSha256);
    }

    // Caller owns the store gate. Verified replacement bytes live outside disposable import staging.
    private async Task RepairAsync(ArtifactLedgerEntry node, string stagedDirectory, CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("N");
        HostArtifactWorkspace.CreatePrivateDirectory(RepairRoot);
        var directory = Path.Combine(RepairRoot, id);
        var intent = new RepairIntent(1, id, GarbageRootHash, node,
            ArtifactMetadataFile.ReadJson<NativeEnvelope>(Path.Combine(stagedDirectory, "envelope.json"), 4096) ?? throw new InvalidDataException());
        var previous = ReadRepairIntents();
        if (previous.Length >= 4096 || JsonSerializer.SerializeToUtf8Bytes(intent).Length
            + Directory.EnumerateFiles(RepairRoot, "*.json").Sum(p => new FileInfo(p).Length) > 16 * 1024 * 1024)
            throw new InvalidDataException("Native repair metadata quota exceeded.");
        await WriteAsync(Path.Combine(RepairRoot, id + ".json"), intent, token);
        HostArtifactWorkspace.CreatePrivateDirectory(directory);
        Directory.Move(stagedDirectory, Path.Combine(directory, "new"));
        _observe?.Invoke("RepairPrepared:after");
        await RecoverRepairsWithoutGateAsync(token);
    }

    private async Task RecoverRepairsWithoutGateAsync(CancellationToken token)
    {
        var intents = ReadRepairIntents();
        var graph = await Ledger.LoadAsync(token);
        foreach (var intent in intents)
        {
            var current = graph.Artifacts.SingleOrDefault(n => n.ArtifactId == intent.Node.ArtifactId);
            if (current is null || !JsonSerializer.SerializeToUtf8Bytes(current).AsSpan()
                .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(intent.Node)))
                throw new InvalidDataException("Native repair no longer matches the live node.");
        }
        foreach (var intent in intents)
        {
            var directory = Path.Combine(RepairRoot, intent.Id);
            var replacement = Path.Combine(directory, "new"); var old = Path.Combine(directory, "old");
            var destination = ArtifactPathRules.ResolveUnderRoot(Root, intent.Node.ContentRelativePath);
            HostArtifactReadService.EnsureNoReparsePoints(Root, directory);
            HostArtifactReadService.EnsureNoReparsePoints(Root, destination);
            if (Directory.Exists(replacement))
            {
                if (!await IsRepairPayloadAsync(replacement, intent, token))
                    throw new InvalidDataException("Native repair replacement differs from its verified identity.");
                if (Directory.Exists(destination))
                {
                    if (Directory.Exists(old)) throw new InvalidDataException("Ambiguous Native repair state.");
                    Directory.Move(destination, old);
                }
                _observe?.Invoke("RepairOldMoved:after");
                Directory.Move(replacement, destination);
                _observe?.Invoke("RepairInstalled:after");
            }
            else if (!await IsRepairPayloadAsync(destination, intent, token))
            {
                // Interrupted before replacement ownership transferred: retain the original for a later retry.
                if (!Directory.Exists(destination) && Directory.Exists(old)) Directory.Move(old, destination);
            }
            HostArtifactWorkspace.DeleteOwnedDirectory(directory);
            File.Delete(Path.Combine(RepairRoot, intent.Id + ".json"));
        }
    }
}

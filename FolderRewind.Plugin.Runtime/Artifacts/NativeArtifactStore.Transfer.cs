using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Artifacts;

public sealed record NativeArtifactClosure(int Version, ArtifactId Root, IReadOnlyList<ArtifactLedgerEntry> Nodes);

public sealed partial class NativeArtifactStore
{
    // A closure is transferred as one immutable object. Its entries are dependency-first and
    // its root descriptor is last. Only a completely verified object can become a Replica.
    public async Task ExportAsync(ArtifactId root, string destination, CancellationToken token)
    {
        await using var read = await OpenAsync(root, token);
        var ledger = await Ledger.LoadAsync(token);
        var closure = NativeArtifactClosureLimits.Validate(ledger, root);
        var map = ledger.Artifacts.ToDictionary(n => n.ArtifactId);
        var nodes = closure.TopologicallySorted.Select(id => map[id]).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        using var space = ArtifactSpaceBudget.Shared.Reserve(new ArtifactSpaceRequest(destination,
            checked(nodes.Sum(n => n.StorageSize) + nodes.Length * 8192L + 16 * 1024 * 1024)));
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var node in nodes)
        {
            var directory = ArtifactPathRules.ResolveUnderRoot(Root, node.ContentRelativePath);
            if (!File.Exists(Path.Combine(directory, "envelope.json"))) throw new InvalidDataException("Native transfer requires an envelope.");
            foreach (var name in new[] { "payload.7z", "envelope.json" })
            {
                var entry = zip.CreateEntry(node.ArtifactId.Value.ToString("N") + "/" + name, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                await using var target = entry.Open();
                await using var source = File.OpenRead(Path.Combine(directory, name));
                await source.CopyToAsync(target, token);
            }
        }
        var descriptor = zip.CreateEntry("closure.json", CompressionLevel.NoCompression);
        descriptor.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using var manifest = descriptor.Open();
        // Local publication transaction/availability is not immutable transfer identity.
        var descriptors = nodes.Select(n => n with { ContentRelativePath = "artifacts/" + n.ArtifactId.Value.ToString("N"),
            TransactionId = n.ArtifactId.Value.ToString("N"), LocalAvailability = ArtifactAvailability.Available,
            CloudAvailability = ArtifactAvailability.Pending }).ToArray();
        await JsonSerializer.SerializeAsync(manifest, new NativeArtifactClosure(1, root, descriptors), cancellationToken: token);
    }

    public async Task<string> ImportAsync(string bundle, ArtifactId expectedRoot, string expectedStorageHash,
        string configId, Guid folderId, CancellationToken token)
    {
        using var held = await EnterAsync(token);
        await RecoverRepairsWithoutGateAsync(token);
        using var zip = ZipFile.OpenRead(bundle);
        var descriptor = zip.GetEntry("closure.json") ?? throw new InvalidDataException("Artifact closure descriptor missing.");
        if (descriptor.Length > 16 * 1024 * 1024 || zip.Entries.Count > 8193) throw new InvalidDataException("Artifact closure metadata quota exceeded.");
        using var manifest = descriptor.Open();
        var closure = JsonSerializer.Deserialize<NativeArtifactClosure>(await ArtifactMetadataFile.ReadStreamAsync(manifest, 16 * 1024 * 1024, token)) ?? throw new InvalidDataException();
        if (closure.Version != 1 || closure.Root != expectedRoot || closure.Nodes.Count > 4096
            || closure.Nodes.Any(n => n.ConfigId != configId || n.FolderId != folderId)) throw new InvalidDataException("Foreign Artifact closure.");
        var graph = new ArtifactLedgerDocument(ArtifactLedgerValidator.CurrentSchemaVersion, new("import"), closure.Nodes);
        ArtifactLedgerValidator.Validate(graph);
        var reachable = NativeArtifactClosureLimits.Validate(graph, expectedRoot);
        if (reachable.Reachable.Count != closure.Nodes.Count || zip.Entries.Count != closure.Nodes.Count * 2 + 1
            || zip.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count)
            throw new InvalidDataException("Artifact closure contains unrelated or duplicate entries.");
        var rootNode = closure.Nodes.Single(n => n.ArtifactId == expectedRoot);
        if (!StringComparer.OrdinalIgnoreCase.Equals(rootNode.StorageSha256, expectedStorageHash))
            throw new InvalidDataException("Artifact root disagrees with committed History.");
        if (closure.Nodes.Sum(n => n.StorageSize) > ArtifactTransformLimits.Default.MaximumBytes)
            throw new InvalidDataException("Artifact closure exceeds byte quota.");
        var current = await Ledger.LoadAsync(token);
        var transaction = Guid.NewGuid().ToString("N");
        await using var staging = Ledger.CreateStagingArea(transaction, ArtifactTransformLimits.Default.MaximumFiles, ArtifactTransformLimits.Default.MaximumBytes);
        var facts = new Dictionary<ArtifactStagingHandle, StagedArtifactFacts>();
        var additions = new List<ArtifactLedgerEntry>();
        foreach (var node in closure.Nodes)
        {
            // The complete declared sum was checked above. Each attempt is bounded to this node;
            // a discarded failed realization must not spend the replacement's decoded allowance.
            ArtifactExtractionBudget NodeBudget() => new(node.LogicalSize, node.LogicalEntryCount!.Value);
            var old = current.Artifacts.SingleOrDefault(n => n.ArtifactId == node.ArtifactId);
            if (old is not null)
            {
                if (old.ConfigId != node.ConfigId || old.FolderId != node.FolderId || old.Format != node.Format
                    || old.FormatVersion != node.FormatVersion || old.RestoreStrategyId != node.RestoreStrategyId
                    || old.LogicalSha256 != node.LogicalSha256 || old.StorageSha256 != node.StorageSha256
                    || old.LogicalSize != node.LogicalSize || old.StorageSize != node.StorageSize || old.LogicalEntryCount != node.LogicalEntryCount
                    || old.Completeness != node.Completeness || old.CoreCaptureMode != node.CoreCaptureMode
                    || !old.Dependencies.SequenceEqual(node.Dependencies)) throw new InvalidDataException("Existing Artifact identity differs.");
                var isolated = new ArtifactLedgerDocument(current.SchemaVersion, current.Revision, [old with { Dependencies = [] }]);
                try
                {
                    await using (await OpenWithoutGateAsync(isolated, old.ArtifactId, new ActionLease(() => { }), token, NodeBudget())) { }
                    continue;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                { /* A verified transfer may repair missing/corrupt local bytes, retaining the immutable node identity. */ }
            }
            var allocation = await staging.CreateArtifactAsync(node.ArtifactId, token);
            var path = staging.GetAllocationPath(allocation.Staging);
            foreach (var name in new[] { "payload.7z", "envelope.json" })
            {
                var entry = zip.GetEntry(node.ArtifactId.Value.ToString("N") + "/" + name) ?? throw new InvalidDataException("Incomplete closure.");
                if (entry.Length != (name == "payload.7z" ? node.StorageSize : Math.Min(entry.Length, NativeArtifactClosureLimits.MaximumEnvelopeBytes)))
                    throw new InvalidDataException("Invalid envelope size.");
                await using var target = await staging.OpenWriteAsync(allocation.Staging, name, token);
                using var source = entry.Open();
                await source.CopyToAsync(target, token);
            }
            var canonical = node with { ContentRelativePath = "artifacts/" + node.ArtifactId.Value.ToString("N"), TransactionId = transaction };
            // Reuse exactly the same storage and logical verification as restore before publication.
            var temporaryGraph = graph with { Artifacts = closure.Nodes.Select(n => n.ArtifactId == node.ArtifactId
                ? canonical with { ContentRelativePath = Path.GetRelativePath(Root, path).Replace('\\', '/'), Dependencies = [] } : n).ToArray() };
            await using (await OpenWithoutGateAsync(temporaryGraph, node.ArtifactId, new ActionLease(() => { }), token, NodeBudget())) { }
            if (old is not null)
            {
                await RepairAsync(old, path, token);
                continue;
            }
            facts.Add(allocation.Staging, new(node.ArtifactId, allocation.Staging, canonical.ContentRelativePath,
                node.LogicalSha256, node.LogicalSize, node.StorageSha256, node.StorageSize) { LogicalEntryCount = node.LogicalEntryCount });
            additions.Add(canonical);
        }
        if (additions.Count > 0)
        {
            var candidate = current with { Revision = new(Guid.NewGuid().ToString("N")), Artifacts = current.Artifacts.Concat(additions).ToArray() };
            await Ledger.CommitAsync(current, candidate, transaction, staging, facts, token);
        }
        // Every node was bounded and verified before publication; avoid decoding the closure again.
        return PayloadPath((await Ledger.LoadAsync(token)).Artifacts.Single(n => n.ArtifactId == expectedRoot));
    }
}

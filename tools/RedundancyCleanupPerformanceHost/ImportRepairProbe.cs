using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;

// Process-only integration fixture. It references the frozen Runtime DLL and never ships
// in the Host or plugin. The BCL codec isolates the durable object repair protocol.
internal static class ImportRepairProbe
{
    private sealed record Input(ArtifactId Root, string StorageSha256, string ConfigId, Guid FolderId,
        string LedgerRevision, IReadOnlyList<ArtifactId> Artifacts);

    internal static async Task RunAsync(string[] args)
    {
        var root = Path.GetFullPath(args[1]);
        if (args[0] == "--repair-create") { await CreateAsync(root); return; }
        if (!File.Exists(Path.Combine(root, "acceptance-owner"))) throw new IOException("Unowned repair fixture.");
        var input = JsonSerializer.Deserialize<Input>(await File.ReadAllBytesAsync(Path.Combine(root, "input.json")))!;
        var transport = new Files(root);
        if (args[0] == "--repair-crash")
        {
            var point = args[3];
            var store = new NativeArtifactStore(StoreDirectory(root), new Codec(), stage =>
            {
                if (stage != point) return;
                using (var witness = new FileStream(Path.Combine(root, "fault-witness"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    witness.Write(System.Text.Encoding.UTF8.GetBytes(stage)); witness.Flush(true);
                }
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            });
            if (args[2] == "v2") await store.ImportObjectsAsync(Path.Combine(root, "closure.json"), input.Root,
                input.StorageSha256, input.ConfigId, input.FolderId, transport, default);
            else await store.ImportAsync(Path.Combine(root, "closure.zip"), input.Root, input.StorageSha256,
                input.ConfigId, input.FolderId, default);
            throw new IOException("Requested repair fault was not reached.");
        }
        if (args[0] != "--repair-recover") throw new ArgumentException("Unknown fixture mode.");
        var recovered = new NativeArtifactStore(StoreDirectory(root), new Codec());
        await recovered.RecoverAsync((_, _) => Task.FromResult(false), default);
        var ledger = await recovered.Ledger.LoadAsync();
        if (ledger.Revision.Value != input.LedgerRevision || !ledger.Artifacts.Select(a => a.ArtifactId).ToHashSet().SetEquals(input.Artifacts))
            throw new InvalidDataException("Repair changed immutable ledger identity.");
        foreach (var id in input.Artifacts) await using (await recovered.OpenAsync(id, default)) { }
        if (recovered.Pending().Count != 0 || recovered.Transfers().Count != 0
            || Directory.EnumerateFileSystemEntries(Path.Combine(recovered.Root, "repairs")).Any())
            throw new IOException("Recovery retained unfinished repair or transfer roots.");
        await File.WriteAllBytesAsync(Path.Combine(root, "recovered.json"), JsonSerializer.SerializeToUtf8Bytes(new
        { Success = true, ImmutableLedger = true, LogicalAndStorageHashesVerified = true, NoPendingRoots = true }));
    }

    private static async Task CreateAsync(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Repair fixture must be new.");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "acceptance-owner"), "object-import-repair-process");
        var folder = Guid.NewGuid();
        var nodes = new List<ArtifactLedgerEntry>();
        await using (var staging = new ArtifactTransformStagingArea(Path.Combine(root, "fixture")))
        {
            var allocations = new List<ArtifactStagingAllocation>();
            for (var i = 0; i < 2; i++)
            {
                var allocation = await staging.CreateArtifactAsync(default); allocations.Add(allocation);
                await using var stream = await staging.OpenWriteAsync(allocation.Staging, "data.bin", default);
                var bytes = new byte[65536]; new Random(800 + i).NextBytes(bytes); await stream.WriteAsync(bytes);
            }
            var facts = await staging.SealAsync("artifacts");
            foreach (var allocation in allocations)
            {
                var fact = facts[allocation.Staging];
                var payload = Path.Combine(root, allocation.ArtifactId + ".zip");
                ZipFile.CreateFromDirectory(Path.Combine(root, "fixture", allocation.ArtifactId.Value.ToString("N")), payload, CompressionLevel.NoCompression, false);
                nodes.Add(new(allocation.ArtifactId, new(new("com.cleanup.recovery"), "fixture"), 1,
                    new(new("com.cleanup.recovery"), "reader"), "cleanup-recovery", folder, fact.ContentRelativePath,
                    fact.LogicalSha256, fact.LogicalSize, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(payload))), new FileInfo(payload).Length,
                    ArtifactCompleteness.Complete, CoreCaptureMode.Full, [], allocation.ArtifactId.Value.ToString("N"), ArtifactAvailability.Available, ArtifactAvailability.Pending)
                { LogicalEntryCount = fact.LogicalEntryCount });
            }
        }
        nodes[^1] = nodes[^1] with { Dependencies = [nodes[0].ArtifactId] };
        var selected = nodes[^1];
        await File.WriteAllBytesAsync(Path.Combine(root, "closure.json"), JsonSerializer.SerializeToUtf8Bytes(new NativeArtifactObjectClosure(2,
            selected.ArtifactId, nodes.Select(n => new NativeArtifactObject(n, new(1, false))).ToArray())));
        var store = new NativeArtifactStore(StoreDirectory(root), new Codec());
        await store.ImportObjectsAsync(Path.Combine(root, "closure.json"), selected.ArtifactId, selected.StorageSha256,
            selected.ConfigId, folder, new Files(root), default);
        await store.ExportAsync(selected.ArtifactId, Path.Combine(root, "closure.zip"), default);
        var ledger = await store.Ledger.LoadAsync();
        await File.WriteAllBytesAsync(Path.Combine(root, "input.json"), JsonSerializer.SerializeToUtf8Bytes(new Input(selected.ArtifactId,
            selected.StorageSha256, selected.ConfigId, folder, ledger.Revision.Value, nodes.Select(n => n.ArtifactId).ToArray())));
        // A repair must replace corrupted bytes while retaining every committed identity.
        await File.WriteAllTextAsync(store.PayloadPath(ledger.Artifacts.Single(n => n.ArtifactId == selected.ArtifactId)), "damaged");
    }

    private static string StoreDirectory(string root)
    {
        var path = Path.Combine(root, "store");
        while (path.Length < 245) path = Path.Combine(path, new string('p', 24));
        return path;
    }

    private sealed class Files(string root) : INativeArtifactObjectTransport
    {
        public Task<bool> HasVerifiedObjectAsync(string key, long size, string sha, CancellationToken token) => Task.FromResult(false);
        public Task UploadObjectAsync(string key, string path, long size, string sha, ArtifactTransferBudget budget, CancellationToken token)
            => throw new NotSupportedException();
        public async Task DownloadObjectAsync(string key, string path, long size, ArtifactTransferBudget budget, CancellationToken token)
        {
            var descriptor = JsonSerializer.Deserialize<NativeArtifactObjectClosure>(await File.ReadAllBytesAsync(Path.Combine(root, "closure.json"), token))!;
            var node = descriptor.Objects.Single(n => NativeArtifactStore.CloudObjectKey(n.Node) == key).Node;
            await using var source = File.OpenRead(Path.Combine(root, node.ArtifactId + ".zip"));
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await budget.CopyAsync(source, target, size, token);
        }
    }
}

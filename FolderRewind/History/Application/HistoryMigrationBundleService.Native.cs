using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed partial class HistoryMigrationBundleService
{
    public const string NativePayloadType = "native-artifact-objects-v2";
    private static string NativePrefix(HistoryConfigId config) => "native/v2/" + HistoryRepositoryPaths.EncodeConfigPathSegment(config) + "/";
    private static string NativeInstalledRelative(string entry) => ".folderrewind/migration-native-v2/" + Hash(Encoding.UTF8.GetBytes(entry)) + ".payload";
    public static string InstalledNativePath(string backupRoot, string entry)
        => HistoryReplicaRecoveryService.ResolveUnderRoot(backupRoot, NativeInstalledRelative(entry));
    private static ImmutableArray<HistoryTransferNativePayload> NativePayloads(HistoryTransferProjectMigration migration)
        => migration.NativePayloads.IsDefault ? [] : migration.NativePayloads;
    private static ImmutableArray<HistoryTransferNativeObject> NativeObjects(HistoryTransferProjectMigration migration)
        => migration.NativeObjects.IsDefault ? [] : migration.NativeObjects;

    private static IEnumerable<HistoryTransferPayloadFile> InstallationFiles(HistoryTransferProjectMigration migration)
        => migration.PayloadFiles.Concat(NativePayloads(migration).Select(p => new HistoryTransferPayloadFile(
            NativeInstalledRelative(p.EntryName), p.EntryName, p.Size, p.StorageSha256)))
            .Concat(NativeObjects(migration).Select(p => new HistoryTransferPayloadFile(NativeInstalledRelative(p.EntryName), p.EntryName, p.Size, p.StorageSha256)));

    private static void ValidateInstallationPath(HistoryTransferProjectMigration migration, HistoryTransferPayloadFile file)
    {
        if (file.EntryName.StartsWith("native/v2/", StringComparison.Ordinal)
            && file.RelativePath == NativeInstalledRelative(file.EntryName)) return;
        ValidateRelativePath(file.RelativePath);
    }

    private async Task<(ImmutableArray<HistoryTransferNativePayload> Payloads, ImmutableArray<HistoryTransferNativeObject> Objects)> ExportNativeAsync(
        HistoryMigrationExportProject project, IReadOnlyList<VersionRepresentation> representations, IReadOnlyList<SourceVersion> versions,
        ZipArchive zip, string destination, bool embedded, CancellationToken token)
    {
        var selected = representations.Where(NativePluginArtifactBackend.IsSupported).ToArray();
        if (!embedded || selected.Length == 0) return ([], []);
        var store = project.NativeStore ?? throw new InvalidOperationException("Native migration requires an explicit storage/codec context.");
        var consumers = project.Consumers ?? throw new InvalidOperationException("Native migration requires reconstructed source verification.");
        var runtime = project.History.Runtime ?? throw new InvalidOperationException("Native migration requires its History runtime.");
        var ledger = await store.Ledger.LoadAsync(token).ConfigureAwait(false);
        var roots = selected.Select(r => new ArtifactId(Guid.Parse(r.RepresentationSpecificMetadata["artifactRootId"]))).ToArray();
        NativeArtifactClosureLimits.Validate(ledger, roots); // One bounded union; shared objects are stored once.
        var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
        var environment = new RepresentationEnvironment(catalog?.Entries ?? [], [], []);
        var payloads = ImmutableArray.CreateBuilder<HistoryTransferNativePayload>();
        long descriptorBytes = 0;
        var objects = new NativeZipWriter(zip, NativePrefix(project.History.ConfigId), destination);
        var work = store.CreateWorkspace("read");
        try
        {
            foreach (var representation in selected.OrderBy(r => r.RepresentationId.ToString(), StringComparer.Ordinal))
            {
                var assessment = await consumers.AssessVersionAsync(representation.VersionId, [representation], environment,
                    AssessmentDepth.Deep, MaterializationFidelity.Exact, token).ConfigureAwait(false);
                if (assessment.Readiness != HistoryReadiness.Ready) throw new InvalidDataException("Native migration source-tree verification failed.");
                var root = new ArtifactId(Guid.Parse(representation.RepresentationSpecificMetadata["artifactRootId"]));
                var path = Path.Combine(work, representation.RepresentationId + ".json");
                await store.ExportObjectsAsync(root, path, objects, token).ConfigureAwait(false);
                var node = ledger.Artifacts.Single(n => n.ArtifactId == root);
                var source = versions.Single(v => v.VersionId == representation.VersionId).SourceId;
                if (!project.History.ConfigId.Matches(node.ConfigId) || source.Value != node.FolderId)
                    throw new InvalidDataException("Foreign Native migration source binding.");
                var name = NativePrefix(project.History.ConfigId) + "closures/" + representation.RepresentationId + ".json";
                var bytes = ArtifactMetadataFile.ReadBytes(path, 16 * 1024 * 1024);
                if (bytes.Length > MaximumMetadataBytes - descriptorBytes) throw new InvalidDataException("Native migration descriptors exceed their aggregate metadata budget.");
                descriptorBytes += bytes.Length;
                await WriteMetadataAsync(zip, name, bytes, token).ConfigureAwait(false);
                payloads.Add(new(NativePayloadType, representation.RepresentationId, root.Value, source.Value, node.ConfigId,
                    node.StorageSha256, name, bytes.Length, Hash(bytes)));
            }
            return (payloads.ToImmutable(), [.. objects.Objects.Values.OrderBy(o => o.ObjectKey, StringComparer.Ordinal)]);
        }
        finally { HostArtifactWorkspace.DeleteOwnedDirectory(work); }
    }

    private sealed class NativeZipWriter(ZipArchive zip, string prefix, string destination) : INativeArtifactObjectTransport
    {
        public Dictionary<string, HistoryTransferNativeObject> Objects { get; } = new(StringComparer.Ordinal);
        private long _bytes;
        public Task<bool> HasVerifiedObjectAsync(string key, long size, string hash, CancellationToken token)
            => Task.FromResult(Objects.TryGetValue(key, out var previous) && previous.Size == size
                && StringComparer.OrdinalIgnoreCase.Equals(previous.StorageSha256, hash));
        public async Task UploadObjectAsync(string key, string path, long size, string hash, ArtifactTransferBudget budget, CancellationToken token)
        {
            if (Objects.ContainsKey(key) || Objects.Count >= 4096 || size > ArtifactTransformLimits.Default.MaximumBytes - _bytes)
                throw new InvalidDataException("Native migration object union exceeds its bound or conflicts.");
            var name = prefix + "objects/" + key;
            using var space = ArtifactSpaceBudget.Shared.Reserve(new ArtifactSpaceRequest(destination, size));
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            await using var output = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
            using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; long copied = 0; int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                copied = checked(copied + count);
                if (copied > size) throw new InvalidDataException("Native migration object changed during export.");
                contentHash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            if (copied != size || !StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(contentHash.GetHashAndReset()), hash))
                throw new InvalidDataException("Native migration object changed during export.");
            _bytes = checked(_bytes + copied);
            Objects.Add(key, new(key, name, copied, hash.ToLowerInvariant()));
        }
        public Task DownloadObjectAsync(string key, string path, long size, ArtifactTransferBudget budget, CancellationToken token)
            => throw new InvalidOperationException("Migration export cannot download objects.");
    }

    private static async Task ValidateNativeEntriesAsync(ZipArchive zip, HistoryTransferV3Manifest manifest,
        HistoryTransferProject project, HashSet<string> declared, CancellationToken token)
    {
        var migration = project.Migration!; var payloads = NativePayloads(migration); var objects = NativeObjects(migration);
        if (payloads.Any(p => p is null) || objects.Any(p => p is null)) throw new InvalidDataException("Null Native migration payload.");
        if (payloads.Length > 4096 || objects.Length > 4096 || !manifest.IncludesPayloads && (payloads.Length != 0 || objects.Length != 0)
            || manifest.FormatVersion < 4 && (payloads.Length != 0 || objects.Length != 0)
            || payloads.Select(p => p.RepresentationId).Distinct().Count() != payloads.Length
            || objects.Select(p => p.ObjectKey).Distinct(StringComparer.Ordinal).Count() != objects.Length)
            throw new InvalidDataException("Invalid Native migration payload set.");
        var nodes = new Dictionary<ArtifactId, ArtifactLedgerEntry>(); var usedObjects = new HashSet<string>(StringComparer.Ordinal);
        var objectMap = objects.ToDictionary(o => o.ObjectKey, StringComparer.Ordinal);
        foreach (var payload in payloads)
        {
            if (payload.PayloadType != NativePayloadType || payload.ArtifactRootId == Guid.Empty || payload.SourceId == Guid.Empty
                || !project.ConfigId.Matches(payload.ArtifactConfigId) || payload.Size is < 1 or > 16 * 1024 * 1024
                || payload.EntryName != NativePrefix(project.ConfigId) + "closures/" + payload.RepresentationId + ".json")
                throw new InvalidDataException("Invalid Native migration virtual descriptor path or binding.");
            RequireHashAndSize(payload.StorageSha256, payload.Size);
            var descriptorEntry = zip.GetEntry(payload.EntryName) ?? throw new InvalidDataException("Missing Native migration descriptor.");
            if (descriptorEntry.Length != payload.Size) throw new InvalidDataException("Native migration descriptor length differs.");
            await using var descriptorInput = descriptorEntry.Open();
            var bytes = await ArtifactMetadataFile.ReadStreamAsync(descriptorInput, checked((int)payload.Size), token).ConfigureAwait(false);
            if (bytes.Length != payload.Size || Hash(bytes) != payload.StorageSha256 || !declared.Add(payload.EntryName))
                throw new InvalidDataException("Native migration descriptor differs from its manifest.");
            var descriptor = JsonSerializer.Deserialize<NativeArtifactObjectClosure>(bytes)
                ?? throw new InvalidDataException("Empty Native migration descriptor.");
            NativeArtifactStore.ValidateObjectClosure(descriptor, new(payload.ArtifactRootId), payload.RootStorageSha256, payload.ArtifactConfigId, payload.SourceId);
            foreach (var item in descriptor.Objects)
            {
                if (nodes.TryGetValue(item.Node.ArtifactId, out var previous)
                    && !JsonSerializer.SerializeToUtf8Bytes(previous).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(item.Node)))
                    throw new InvalidDataException("Native migration object identity conflicts across roots.");
                nodes[item.Node.ArtifactId] = item.Node;
                var key = NativeArtifactStore.CloudObjectKey(item.Node); usedObjects.Add(key);
                if (!objectMap.TryGetValue(key, out var file) || file.Size != item.Node.StorageSize
                    || !StringComparer.OrdinalIgnoreCase.Equals(file.StorageSha256, item.Node.StorageSha256))
                    throw new InvalidDataException("Native migration closure is incomplete.");
            }
        }
        if (!usedObjects.SetEquals(objectMap.Keys)) throw new InvalidDataException("Native migration contains unrelated objects.");
        if (payloads.Length > 0) NativeArtifactClosureLimits.Validate(new(ArtifactLedgerValidator.CurrentSchemaVersion, new("migration"), nodes.Values.ToArray()),
            payloads.Select(p => new ArtifactId(p.ArtifactRootId)));
        foreach (var file in objects)
        {
            if (file.EntryName != NativePrefix(project.ConfigId) + "objects/" + file.ObjectKey || !declared.Add(file.EntryName))
                throw new InvalidDataException("Invalid Native migration virtual object path.");
            RequireHashAndSize(file.StorageSha256, file.Size);
            var entry = zip.GetEntry(file.EntryName) ?? throw new InvalidDataException("Missing Native migration object.");
            if (entry.Length != file.Size) throw new InvalidDataException("Native migration object length differs.");
            await using var input = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; long size = 0; int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                size = checked(size + count);
                if (size > file.Size) throw new InvalidDataException("Native migration object exceeds its expected length.");
                hash.AppendData(buffer, 0, count);
            }
            if (size != file.Size || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != file.StorageSha256)
                throw new InvalidDataException("Native migration object hash differs.");
        }
    }

    public static INativeArtifactObjectTransport InstalledNativeObjects(HistoryTransferProjectMigration migration, string backupRoot)
        => new InstalledObjects(migration, backupRoot);

    private sealed class InstalledObjects(HistoryTransferProjectMigration migration, string root) : INativeArtifactObjectTransport
    {
        private readonly Dictionary<string, HistoryTransferNativeObject> _objects = NativeObjects(migration).ToDictionary(o => o.ObjectKey, StringComparer.Ordinal);
        public async Task DownloadObjectAsync(string key, string path, long size, ArtifactTransferBudget budget, CancellationToken token)
        {
            if (!_objects.TryGetValue(key, out var file) || file.Size != size) throw new InvalidDataException("Unbound Native migration object.");
            var source = InstalledNativePath(root, file.EntryName);
            HistoryReplicaRecoveryService.RequireRegularPath(root, source);
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            await budget.CopyAsync(input, output, size, token).ConfigureAwait(false);
        }
        public Task<bool> HasVerifiedObjectAsync(string key, long size, string hash, CancellationToken token)
            => throw new InvalidOperationException("Migration recovery is read only.");
        public Task UploadObjectAsync(string key, string path, long size, string hash, ArtifactTransferBudget budget, CancellationToken token)
            => throw new InvalidOperationException("Migration recovery is read only.");
    }
}

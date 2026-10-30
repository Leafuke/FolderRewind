using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using FolderRewind.History.Storage;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Plugin.Runtime.Configuration;
using FolderRewind.Services;
using System;
using System.Buffers.Binary;
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

/// <summary>One portable snapshot format for file and cloud transport. Payloads never enter the metadata buffer.</summary>
public sealed partial class HistoryMigrationBundleService
{
    public const long MaximumMetadataEntryBytes = 64L * 1024 * 1024;
    public const long MaximumMetadataBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly HistoryPackCodec _codec = new();

    public async Task<HistoryTransferV3Manifest> ExportAsync(IReadOnlyList<HistoryMigrationExportProject> projects, string destination,
        string applicationVersion, byte[]? portableConfiguration, bool includePayloads,
        Func<CancellationToken, Task>? validateConfiguration = null, CancellationToken token = default)
    {
        if (projects.Count == 0 || projects.Select(p => p.History.ConfigId).Distinct().Count() != projects.Count)
            throw new ArgumentException("Select distinct projects to export.");
        if (portableConfiguration is not null) ValidateConfiguration(portableConfiguration, projects.Select(p => p.History));
        var output = Path.GetFullPath(destination); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var leases = new List<IAsyncDisposable>();
        try
        {
            foreach (var project in projects.OrderBy(p => p.History.ConfigId.Value, StringComparer.Ordinal))
            {
                leases.Add(await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(project.History.ConfigId, token).ConfigureAwait(false));
                if (project.History.Runtime is { } runtime)
                    leases.Add(await runtime.MutationGate.EnterForRecoveryAsync(token).ConfigureAwait(false));
            }
            var descriptions = ImmutableArray.CreateBuilder<HistoryTransferProject>();
            HistoryTransferV3Manifest manifest;
            long metadataBytes = portableConfiguration?.Length ?? 0;
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                if (portableConfiguration is not null) await WriteMetadataAsync(zip, "configuration.json", portableConfiguration, token).ConfigureAwait(false);
                foreach (var project in projects)
                {
                    var id = project.History.ConfigId; var runtime = project.History.Runtime;
                    if (runtime is not null && runtime.ConfigId != id) throw new HistoryIntegrityConflictException("Export runtime identity differs from the selected project.");
                    var packs = runtime is null ? [] : await runtime.Repository.ReadAllPacksAsync(token).ConfigureAwait(false);
                    new HistoryRepositoryValidator(_codec).Validate(id, packs);
                    var facts = packs.SelectMany(p => p.Pack.Objects).Where(o => o.SchemaVersion == 1 && HistoryObjectKinds.IsKnown(o.Kind))
                        .DistinctBy(o => (o.Kind, o.Id)).Select(_codec.DeserializeKnown).ToArray();
                    var representations = facts.OfType<VersionRepresentation>().ToArray();
                    var versions = facts.OfType<SourceVersion>().ToArray();
                    var root = Path.GetFullPath(project.BackupRoot);
                    var hints = ImmutableArray.CreateBuilder<HistoryReplicaRecoveryHint>();
                    var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var workspace = runtime is null ? null : (await runtime.WorkspaceStore.LoadAsync(token).ConfigureAwait(false)).Value;
                    if (runtime is not null)
                    {
                        var preview = await new HistoryReplicaRecoveryService(runtime).InspectAsync(root, token: token).ConfigureAwait(false);
                        var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
                        foreach (var group in preview.Candidates.GroupBy(c => c.RepresentationId))
                        {
                            var registered = group.Where(c => (catalog?.Entries ?? []).Any(e => e.RepresentationId == c.RepresentationId
                                && e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath && e.Locator.AbsolutePath.Equals(c.Path, StringComparison.OrdinalIgnoreCase))).ToArray();
                            var candidate = registered.Length == 1 ? registered[0] : group.Count() == 1 && group.Single().IdentityVerified ? group.Single() : null;
                            if (candidate is null) continue;
                            var relative = Path.GetRelativePath(root, candidate.Path).Replace('\\', '/');
                            hints.Add(new(candidate.RepresentationId, relative, candidate.Size, candidate.StorageSha256));
                            if (includePayloads) files[relative] = candidate.Path;
                        }
                    }
                    var native = await ExportNativeAsync(project, representations, versions, zip, temporary, includePayloads, token).ConfigureAwait(false);
                    metadataBytes = checked(metadataBytes + native.Payloads.Sum(p => p.Size));
                    if (metadataBytes > MaximumMetadataBytes) throw new InvalidDataException("Migration metadata exceeds its aggregate budget.");
                    ArtifactLedgerDocument? closure = null;
                    var artifactRoots = representations.Where(r => r.Kind == RepresentationKind.PluginArtifact
                        && !NativePluginArtifactBackend.IsSupported(r) && !r.RepresentationSpecificMetadata.ContainsKey("nativeArtifactEnvelope"))
                        .Select(r => r.RepresentationSpecificMetadata.GetValueOrDefault(PluginArtifactRepresentationHandler.ArtifactRootIdMetadataKey))
                        .Where(s => !string.IsNullOrEmpty(s)).Select(s => new ArtifactId(Guid.Parse(s!))).ToArray();
                    if (artifactRoots.Length > 0)
                    {
                        if (!File.Exists(Path.Combine(root, ".folderrewind", "artifact-ledger.v2.json")))
                            throw new IOException("Artifact ledger is required to export these historical representations.");
                        var store = new FileArtifactLedgerStore(root); var ledger = await store.LoadAsync(token).ConfigureAwait(false);
                        var reachable = ArtifactLedgerValidator.BuildClosure(ledger, artifactRoots).Reachable;
                        closure = ledger with { Artifacts = ledger.Artifacts.Where(a => reachable.Contains(a.ArtifactId)).ToArray() };
                        await store.VerifyArtifactsAsync(closure, reachable, token).ConfigureAwait(false);
                        if (includePayloads)
                            foreach (var artifact in closure.Artifacts)
                            {
                                var path = HistoryReplicaRecoveryService.ResolveUnderRoot(root, artifact.ContentRelativePath);
                                if (Directory.Exists(path))
                                    foreach (var directory in Directory.EnumerateDirectories(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }).Prepend(path))
                                    { HistoryReplicaRecoveryService.RequireRegularPath(root, directory); directories.Add(Path.GetRelativePath(root, directory).Replace('\\', '/')); }
                                foreach (var file in PayloadFiles(path))
                                { HistoryReplicaRecoveryService.RequireRegularPath(root, file); files[Path.GetRelativePath(root, file).Replace('\\', '/')] = file; }
                            }
                    }
                    if (includePayloads)
                    {
                        var graph = representations.ToDictionary(r => r.RepresentationId);
                        foreach (var version in versions)
                            if (!representations.Where(r => r.VersionId == version.VersionId && r.Fidelity == MaterializationFidelity.Exact).Any(r =>
                                HistoryChainRewritePlanner.Closure(r.RepresentationId, graph).All(dependency =>
                                    hints.Any(h => h.RepresentationId == dependency) || native.Payloads.Any(p => p.RepresentationId == dependency)
                                    || graph[dependency].Kind == RepresentationKind.PluginArtifact && closure is not null
                                        && !graph[dependency].RepresentationSpecificMetadata.ContainsKey("nativeArtifactEnvelope"))))
                                throw new IOException((representations.Any(r => r.VersionId == version.VersionId && r.Kind == RepresentationKind.PluginArtifact
                                    && !NativePluginArtifactBackend.IsSupported(r) && r.RepresentationSpecificMetadata.ContainsKey("nativeArtifactEnvelope"))
                                    ? "[history.unsupported-experimental-format] " : "")
                                    + $"Complete migration requires the Exact payload closure of Version {version.VersionId}.");
                        if (hints.Count > 0 && project.Archive is null) throw new InvalidOperationException("Archive verification is required for an embedded migration.");
                        foreach (var hint in hints)
                        {
                            var representation = graph[hint.RepresentationId];
                            var verified = await project.Archive!.VerifyAsync(representation, HistoryReplicaRecoveryService.ResolveUnderRoot(root, hint.RelativePath), token).ConfigureAwait(false);
                            if (!verified.Success) throw new InvalidDataException(verified.Diagnostic);
                        }
                    }
                    var payloads = ImmutableArray.CreateBuilder<HistoryTransferPayloadFile>();
                    foreach (var file in files.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        ValidateRelativePath(file.Key);
                        var entry = "payloads/" + HistoryRepositoryPaths.EncodeConfigPathSegment(id) + "/" + Hash(Encoding.UTF8.GetBytes(file.Key));
                        var factsBefore = await HistoryReplicaRecoveryService.ReadFileFactsAsync(file.Value, token).ConfigureAwait(false);
                        if (hints.Any(h => h.RelativePath.Equals(file.Key, StringComparison.OrdinalIgnoreCase)
                            && (h.Size != factsBefore.Size || h.StorageSha256 != factsBefore.Hash))) throw new IOException("Archive changed during migration export verification.");
                        var copied = await WritePayloadAsync(zip, entry, file.Value, token).ConfigureAwait(false);
                        if (copied != factsBefore) throw new IOException("Payload changed during migration export.");
                        payloads.Add(new(file.Key, entry, copied.Size, copied.Hash));
                    }
                    var prefix = "repositories/" + HistoryRepositoryPaths.EncodeConfigPathSegment(id) + "/";
                    var descriptorBytes = HistoryRepositoryDescriptor.Create(id).ToCanonicalBytes(); metadataBytes += descriptorBytes.Length;
                    await WriteMetadataAsync(zip, prefix + "repository.json", descriptorBytes, token).ConfigureAwait(false);
                    foreach (var pack in packs)
                    {
                        metadataBytes += pack.OriginalBytes.Length;
                        if (metadataBytes > MaximumMetadataBytes) throw new InvalidDataException("Migration metadata exceeds its memory budget.");
                        await WriteMetadataAsync(zip, prefix + $"packs/{pack.Pack.PackId}.frpack", pack.OriginalBytes, token).ConfigureAwait(false);
                    }
                    descriptions.Add(new(id, project.History.Name, project.History.Sources, versions.Length,
                        facts.OfType<BranchUpdate>().Select(b => b.BranchId).Distinct().Count(),
                        packs.ToImmutableSortedDictionary(p => p.Pack.PackId.ToString(), p => HistoryPackCodec.ComputeSha256(p.OriginalBytes), StringComparer.Ordinal))
                    { Migration = new(root, hints.ToImmutable(), [.. (workspace?.SourceBaselines ?? []).Where(s => project.History.Sources.Any(h => h.SourceId == s.SourceId))],
                        payloads.ToImmutable(), closure, [.. directories.Order(StringComparer.Ordinal)], native.Payloads, native.Objects) });
                }
                manifest = new HistoryTransferV3Manifest(HistoryTransferManifest.CurrentMagic, descriptions.Any(p => !p.Migration!.NativePayloads.IsDefaultOrEmpty) ? 4 : 3, applicationVersion, includePayloads,
                    descriptions.ToImmutable(), portableConfiguration is null ? null : Hash(portableConfiguration), Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
                var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
                if (metadataBytes + manifestBytes.Length > MaximumMetadataBytes) throw new InvalidDataException("Migration metadata exceeds its memory budget.");
                await WriteMetadataAsync(zip, "manifest.json", manifestBytes, token).ConfigureAwait(false);
                if (validateConfiguration is not null) await validateConfiguration(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested(); File.Move(temporary, output, true);
            return manifest;
        }
        finally
        {
            for (var i = leases.Count - 1; i >= 0; i--) await leases[i].DisposeAsync().ConfigureAwait(false);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<HistoryMigrationBundlePreview> PreviewAsync(string path, CancellationToken token = default)
    {
        var history = await new HistoryRepositoryBatchTransferService().PreviewAsync(path, token).ConfigureAwait(false);
        if (history.FormatVersion < 3) return new(history, false, null, "");
        using var zip = ZipFile.OpenRead(path);
        var manifest = JsonSerializer.Deserialize<HistoryTransferV3Manifest>(await ReadMetadataAsync(zip, "manifest.json", token).ConfigureAwait(false), JsonOptions)!;
        var configuration = manifest.ConfigurationSha256 is null ? null : await ReadMetadataAsync(zip, "configuration.json", token).ConfigureAwait(false);
        await using var input = File.OpenRead(path);
        if (Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)) != history.FileSha256)
            throw new IOException("Migration bundle changed while previewing it.");
        return new(history, manifest.IncludesPayloads, configuration, manifest.TransferId);
    }

    internal static async Task ValidateAdditionalEntriesAsync(ZipArchive zip, HistoryTransferV3Manifest manifest,
        HashSet<string> declared, CancellationToken token)
    {
        long nativeMetadataBytes = 0;
        foreach (var project in manifest.Projects)
            foreach (var payload in NativePayloads(project.Migration ?? throw new InvalidDataException("Missing migration project.")))
            {
                if (payload is null || payload.Size < 0 || payload.Size > MaximumMetadataBytes - nativeMetadataBytes)
                    throw new InvalidDataException("Native migration descriptors exceed their aggregate metadata budget.");
                nativeMetadataBytes += payload.Size;
            }
        var declaredMetadataBytes = (zip.GetEntry("manifest.json")?.Length ?? 0)
            + (manifest.ConfigurationSha256 is null ? 0 : zip.GetEntry("configuration.json")?.Length ?? 0);
        if (nativeMetadataBytes > MaximumMetadataBytes - declaredMetadataBytes)
            throw new InvalidDataException("Migration metadata exceeds its aggregate budget.");
        if (manifest.ConfigurationSha256 is not null)
        {
            var config = await ReadMetadataAsync(zip, "configuration.json", token).ConfigureAwait(false);
            if (Hash(config) != manifest.ConfigurationSha256) throw new InvalidDataException("Migration configuration hash differs from its manifest.");
            ValidateConfiguration(config, manifest.Projects.Select(p => new HistoryTransferExportProject(p.ConfigId, p.Name, p.Sources, null))); declared.Add("configuration.json");
        }
        foreach (var project in manifest.Projects)
        {
            var migration = project.Migration ?? throw new InvalidDataException("Migration project has no recovery information.");
            if (migration.Replicas.IsDefault || migration.Continuation.IsDefault || migration.PayloadFiles.IsDefault
                || migration.Replicas.Select(r => r.RepresentationId).Distinct().Count() != migration.Replicas.Length
                || migration.Continuation.Select(s => s.SourceId).Distinct().Count() != migration.Continuation.Length
                || migration.PayloadFiles.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != migration.PayloadFiles.Length)
                throw new InvalidDataException("Migration recovery information contains invalid or duplicate identities.");
            foreach (var hint in migration.Replicas) { ValidateRelativePath(hint.RelativePath); RequireHashAndSize(hint.StorageSha256, hint.Size); }
            foreach (var directory in migration.PayloadDirectories.IsDefault ? [] : migration.PayloadDirectories)
            {
                ValidateRelativePath(directory);
                if (migration.PayloadFiles.Any(f => f.RelativePath.Equals(directory, StringComparison.OrdinalIgnoreCase)
                    || directory.StartsWith(f.RelativePath + "/", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Migration file and directory paths overlap.");
            }
            if (!manifest.IncludesPayloads && migration.PayloadFiles.Length > 0) throw new InvalidDataException("External migration contains embedded payload declarations.");
            foreach (var file in migration.PayloadFiles)
            {
                ValidateRelativePath(file.RelativePath); RequireHashAndSize(file.StorageSha256, file.Size);
                var expected = "payloads/" + HistoryRepositoryPaths.EncodeConfigPathSegment(project.ConfigId) + "/" + Hash(Encoding.UTF8.GetBytes(file.RelativePath));
                if (file.EntryName != expected || !declared.Add(file.EntryName)) throw new InvalidDataException("Migration payload entry is not canonical or unique.");
                var entry = zip.GetEntry(file.EntryName) ?? throw new InvalidDataException("Migration payload is missing.");
                if (entry.Length != file.Size) throw new InvalidDataException("Migration payload size differs from its manifest.");
                await using var input = entry.Open();
                if (Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)).ToLowerInvariant() != file.StorageSha256)
                    throw new InvalidDataException("Migration payload hash differs from its manifest.");
            }
            await ValidateNativeEntriesAsync(zip, manifest, project, declared, token).ConfigureAwait(false);
            if (migration.ArtifactClosure is not null) ArtifactLedgerValidator.Validate(migration.ArtifactClosure);
            if (manifest.IncludesPayloads && migration.ArtifactClosure is not null) ValidateEmbeddedArtifactClosure(migration);
        }
    }

    internal static void ValidateProject(HistoryTransferProject project, IEnumerable<HistoryPackReadResult> packs, bool embedded)
    {
        var codec = new HistoryPackCodec();
        var representations = packs.SelectMany(p => p.Pack.Objects).Where(o => o.Kind == HistoryObjectKinds.VersionRepresentation && o.SchemaVersion == 1)
            .Select(o => (VersionRepresentation)codec.DeserializeKnown(o)).DistinctBy(r => r.RepresentationId).ToDictionary(r => r.RepresentationId);
        var migration = project.Migration!;
        var historicalSourceIds = packs.SelectMany(p => p.Pack.Objects).Where(o => o.Kind == HistoryObjectKinds.SourceVersion && o.SchemaVersion == 1)
            .Select(o => ((SourceVersion)codec.DeserializeKnown(o)).SourceId).Concat(project.Sources.Select(s => s.SourceId)).ToHashSet();
        foreach (var hint in migration.Replicas)
            if (!representations.ContainsKey(hint.RepresentationId) || embedded && !migration.PayloadFiles.Any(f => f.RelativePath == hint.RelativePath && f.Size == hint.Size && f.StorageSha256 == hint.StorageSha256))
                throw new InvalidDataException("Migration replica does not match its declared history or payload.");
        foreach (var payload in NativePayloads(migration))
        {
            if (!representations.TryGetValue(payload.RepresentationId, out var representation) || !NativePluginArtifactBackend.IsSupported(representation)
                || !representation.RepresentationSpecificMetadata.TryGetValue("artifactRootId", out var root) || !Guid.TryParse(root, out var parsed) || parsed != payload.ArtifactRootId
                || !StringComparer.OrdinalIgnoreCase.Equals(representation.RepresentationSpecificMetadata.GetValueOrDefault("storageSha256"), payload.RootStorageSha256)
                || !packs.SelectMany(p => p.Pack.Objects).Where(o => o.Kind == HistoryObjectKinds.SourceVersion && o.SchemaVersion == 1)
                    .Select(o => (SourceVersion)codec.DeserializeKnown(o)).Any(v => v.VersionId == representation.VersionId && v.SourceId.Value == payload.SourceId))
                throw new InvalidDataException("Native migration descriptor does not match immutable History.");
        }
        foreach (var state in migration.Continuation)
            if (project.Sources.All(s => s.SourceId != state.SourceId)) throw new InvalidDataException("Migration continuation crosses a Source identity.");
        if (migration.ArtifactClosure is not null && migration.ArtifactClosure.Artifacts.Any(a => !new HistoryConfigId(a.ConfigId).Equals(project.ConfigId)
            || historicalSourceIds.All(s => s.Value != a.FolderId))) throw new InvalidDataException("Migration Artifact closure crosses project or Source identities.");
        if (embedded)
        {
            var versions = packs.SelectMany(p => p.Pack.Objects).Where(o => o.Kind == HistoryObjectKinds.SourceVersion && o.SchemaVersion == 1)
                .Select(o => (SourceVersion)codec.DeserializeKnown(o));
            foreach (var version in versions)
                if (!representations.Values.Where(r => r.VersionId == version.VersionId && r.Fidelity == MaterializationFidelity.Exact).Any(r =>
                    HistoryChainRewritePlanner.Closure(r.RepresentationId, representations).All(id => migration.Replicas.Any(h => h.RepresentationId == id)
                        || NativePayloads(migration).Any(p => p.RepresentationId == id)
                        || representations[id].Kind == RepresentationKind.PluginArtifact && migration.ArtifactClosure is not null
                            && !representations[id].RepresentationSpecificMetadata.ContainsKey("nativeArtifactEnvelope"))))
                    throw new InvalidDataException("Embedded migration lacks an Exact payload closure.");
        }
    }

    internal static async Task<byte[]> ReadMetadataAsync(ZipArchive zip, string name, CancellationToken token)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException("Migration metadata is missing.");
        if (entry.Length > MaximumMetadataEntryBytes) throw new InvalidDataException("Migration metadata entry exceeds its memory budget.");
        await using var input = entry.Open(); using var output = new MemoryStream(); var buffer = new byte[65536]; int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        { if (output.Length + count > MaximumMetadataEntryBytes) throw new InvalidDataException("Migration metadata entry exceeds its memory budget."); await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); }
        return output.ToArray();
    }
    private static async Task WriteMetadataAsync(ZipArchive zip, string entry, byte[] bytes, CancellationToken token)
    {
        if (bytes.Length > MaximumMetadataEntryBytes) throw new InvalidDataException("Migration metadata entry exceeds its memory budget.");
        await using var output = zip.CreateEntry(entry, CompressionLevel.Optimal).Open(); await output.WriteAsync(bytes, token).ConfigureAwait(false);
    }
    private static async Task<(long Size, string Hash)> WritePayloadAsync(ZipArchive zip, string entry, string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        await using var output = zip.CreateEntry(entry, CompressionLevel.NoCompression).Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; long size = 0; int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        { await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); hash.AppendData(buffer, 0, count); size += count; }
        return (size, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
    private static IEnumerable<string> PayloadFiles(string path)
    {
        if (File.Exists(path)) return [path];
        if (!Directory.Exists(path)) throw new FileNotFoundException("Artifact payload is missing.", path);
        return Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false });
    }
    internal static void ValidateRelativePath(string path)
    {
        if (!HistoryRepositoryPaths.IsSafeRepositoryRelativePath(path) || path != path.Trim('/') || path.Contains("//", StringComparison.Ordinal)
            || path.Split('/').Any(s => s != s.TrimEnd(' ', '.') || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(s))
            || path.StartsWith(".folderrewind/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsafe migration payload path.");
    }
    private static bool IsDeviceName(string segment)
    {
        var name = segment.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" || name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal)
            || name.StartsWith("LPT", StringComparison.Ordinal)) && name[3] is >= '1' and <= '9';
    }
    private static void RequireHashAndSize(string hash, long size)
    { if (size < 0 || hash is null || hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)) || hash != hash.ToLowerInvariant()) throw new InvalidDataException("Invalid migration payload integrity information."); }
    private static void ValidateConfiguration(byte[] bytes, IEnumerable<HistoryTransferExportProject> projects)
    {
        if (bytes.Length > MaximumMetadataEntryBytes || !new ConfigDocumentGate().Prepare(bytes).IsReady) throw new InvalidDataException("Invalid migration configuration.");
        using var json = JsonDocument.Parse(bytes); var allowed = projects.ToDictionary(p => p.ConfigId);
        var configs = json.RootElement.GetProperty("BackupConfigs").EnumerateArray().ToArray();
        if (configs.Length != allowed.Count || configs.Select(p => new HistoryConfigId(p.GetProperty("Id").GetString()!)).Distinct().Count() != configs.Length)
            throw new InvalidDataException("Migration configuration contains missing or duplicate projects.");
        foreach (var config in configs)
        {
            if (!allowed.TryGetValue(new HistoryConfigId(config.GetProperty("Id").GetString()!), out var selected))
                throw new InvalidDataException("Migration configuration contains an unselected project.");
            var ids = config.GetProperty("SourceFolders").EnumerateArray().Select(s => new SourceId(Guid.Parse(s.GetProperty("Id").GetString()!))).ToArray();
            if (ids.Distinct().Count() != ids.Length || !ids.ToHashSet().SetEquals(selected.Sources.Select(s => s.SourceId)))
                throw new InvalidDataException("Migration configuration and history Source rosters differ.");
        }
    }
    private static void ValidateEmbeddedArtifactClosure(HistoryTransferProjectMigration migration)
    {
        foreach (var artifact in migration.ArtifactClosure!.Artifacts)
        {
            ValidateRelativePath(artifact.ContentRelativePath);
            var file = migration.PayloadFiles.SingleOrDefault(f => f.RelativePath == artifact.ContentRelativePath);
            long size; string hash;
            if (file is not null) { size = file.Size; hash = file.StorageSha256; }
            else
            {
                var files = migration.PayloadFiles.Where(f => f.RelativePath.StartsWith(artifact.ContentRelativePath + "/", StringComparison.Ordinal)).OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray();
                if (files.Length == 0) throw new InvalidDataException("Embedded migration lacks Artifact content.");
                using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); size = 0; var length = new byte[sizeof(long)];
                foreach (var child in files)
                {
                    aggregate.AppendData(Encoding.UTF8.GetBytes(child.RelativePath[(artifact.ContentRelativePath.Length + 1)..])); aggregate.AppendData([0]);
                    BinaryPrimitives.WriteInt64LittleEndian(length, child.Size); aggregate.AppendData(length); aggregate.AppendData(Convert.FromHexString(child.StorageSha256));
                    size = checked(size + child.Size);
                }
                hash = Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
            }
            if (size != artifact.LogicalSize || hash != artifact.LogicalSha256) throw new InvalidDataException("Embedded Artifact closure failed logical integrity verification.");
        }
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

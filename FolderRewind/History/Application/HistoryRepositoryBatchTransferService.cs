using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed record HistoryTransferSource(SourceId SourceId, string Name, string PathHint);
public sealed record HistoryTransferProject(HistoryConfigId ConfigId, string Name,
    ImmutableArray<HistoryTransferSource> Sources, int VersionCount, int BranchCount,
    ImmutableSortedDictionary<string, string> PackSha256);
public sealed record HistoryTransferBatchManifest(string Magic, int FormatVersion, string ApplicationVersion,
    bool IncludesPayloads, ImmutableArray<HistoryTransferProject> Projects);
public sealed record HistoryTransferPreview(int FormatVersion, string ApplicationVersion, ImmutableArray<HistoryTransferProject> Projects, string FileSha256);
public sealed record HistoryTransferExportProject(HistoryConfigId ConfigId, string Name,
    ImmutableArray<HistoryTransferSource> Sources, HistoryRuntime? Runtime);
public enum HistoryTransferCommitStatus { Committed, CommittedRecoveryRequired, Failed, Canceled, NotExecuted }
public enum HistoryTransferErrorKind { None, Data, Format, Permission, Space, Io, Unexpected }
public enum HistoryTransferLocalStateStatus { Ready, InitializationRequired, RecoveryRequired }
public sealed record HistoryTransferProjectResult(HistoryConfigId ConfigId, int InstalledPacks, int DuplicatePacks,
    bool ImportedAsOrphanRepository, HistoryTransferCommitStatus CommitStatus,
    HistoryTransferLocalStateStatus LocalStateStatus, string Diagnostic = "", HistoryTransferErrorKind ErrorKind = HistoryTransferErrorKind.None);
public sealed record HistoryTransferBatchImportResult(ImmutableArray<HistoryTransferProjectResult> Projects);

/// <summary>v2 multi-project transfer, with v1 compatibility and independent repository commits.</summary>
public sealed class HistoryRepositoryBatchTransferService(
    Func<HistoryConfigId, HistoryRuntime?>? runtimeResolver = null, Func<HistoryConfigId, bool>? configExists = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly HistoryPackCodec _codec = new();

    public async Task ExportAsync(IReadOnlyList<HistoryTransferExportProject> projects, string destinationPath,
        string applicationVersion, CancellationToken token = default)
    {
        if (projects.Count == 0 || projects.Select(p => p.ConfigId).Distinct().Count() != projects.Count)
            throw new ArgumentException("Select distinct projects to export.");
        var destination = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("Destination has no parent.");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var manifests = ImmutableArray.CreateBuilder<HistoryTransferProject>();
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var project in projects)
                {
                    if (project.Runtime is { } runtime && runtime.ConfigId != project.ConfigId)
                        throw new HistoryIntegrityConflictException("Export Runtime identity differs from selected project.");
                    IReadOnlyList<HistoryPackReadResult> packs = project.Runtime is null ? []
                        : await project.Runtime.Repository.ReadAllPacksAsync(token).ConfigureAwait(false);
                    new HistoryRepositoryValidator(_codec).Validate(project.ConfigId, packs);
                    var hashes = packs.ToImmutableSortedDictionary(p => p.Pack.PackId.ToString(),
                        p => HistoryPackCodec.ComputeSha256(p.OriginalBytes), StringComparer.Ordinal);
                    manifests.Add(Describe(project.ConfigId, project.Name, project.Sources, hashes, packs));
                    var prefix = Prefix(project.ConfigId);
                    await WriteAsync(archive, prefix + "repository.json", HistoryRepositoryDescriptor.Create(project.ConfigId).ToCanonicalBytes(), token).ConfigureAwait(false);
                    foreach (var pack in packs.OrderBy(p => p.Pack.PackId.ToString(), StringComparer.Ordinal))
                        await WriteAsync(archive, prefix + $"packs/{pack.Pack.PackId}.frpack", pack.OriginalBytes, token).ConfigureAwait(false);
                }
                await WriteAsync(archive, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(new HistoryTransferBatchManifest(
                    HistoryTransferManifest.CurrentMagic, 2, applicationVersion, false, manifests.ToImmutable()), JsonOptions), token).ConfigureAwait(false);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<HistoryTransferPreview> PreviewAsync(string path, CancellationToken token = default)
    {
        var parsed = await Task.Run(() => ReadAsync(path, token), token).ConfigureAwait(false);
        return new(parsed.Format, parsed.Version, parsed.Projects.Select(p => p.Project).ToImmutableArray(), parsed.FileSha256);
    }

    public async Task<HistoryTransferBatchImportResult> ImportAsync(string path, string configDirectory,
        IEnumerable<HistoryConfigId>? selected = null, CancellationToken token = default, string? expectedFileSha256 = null)
    {
        var parsed = await Task.Run(() => ReadAsync(path, token), token).ConfigureAwait(false);
        if (expectedFileSha256 is not null && parsed.FileSha256 != expectedFileSha256)
            throw new InvalidDataException("Transfer file changed after preview. Select it again.");
        var selection = selected?.ToHashSet() ?? parsed.Projects.Select(p => p.Project.ConfigId).ToHashSet();
        if (selection.Count == 0 || selection.Any(id => parsed.Projects.All(p => p.Project.ConfigId != id)))
            throw new ArgumentException("Import selection does not match the transfer.");
        var results = ImmutableArray.CreateBuilder<HistoryTransferProjectResult>();
        var canceled = false;
        foreach (var project in parsed.Projects.Where(p => selection.Contains(p.Project.ConfigId)))
        {
            var id = project.Project.ConfigId;
            var orphan = !(configExists?.Invoke(id) ?? runtimeResolver?.Invoke(id) is not null);
            if (canceled || token.IsCancellationRequested)
            { results.Add(new(id, 0, 0, orphan, HistoryTransferCommitStatus.NotExecuted, HistoryTransferLocalStateStatus.InitializationRequired)); canceled = true; continue; }
            var runtime = runtimeResolver?.Invoke(id);
            var repository = runtime?.Repository ?? new FileHistoryRepository(id, HistoryRepositoryPaths.ForConfigDirectory(configDirectory, id));
            var newCount = 0;
            var duplicates = 0;
            var committed = false;
            try
            {
                await using var operation = await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(id, token).ConfigureAwait(false);
                await repository.InitializeAsync(token).ConfigureAwait(false);
                IAsyncDisposable? mutation = runtime is null ? null : await runtime.MutationGate.EnterAsync(token).ConfigureAwait(false);
                try
                {
                    var existing = await repository.ReadAllPacksAsync(token).ConfigureAwait(false);
                    new HistoryRepositoryValidator(_codec).Validate(id, existing.Concat(project.Packs));
                    var existingById = existing.ToDictionary(p => p.Pack.PackId);
                    foreach (var incoming in project.Packs)
                        if (existingById.TryGetValue(incoming.Pack.PackId, out var previous)
                            && !previous.OriginalBytes.AsSpan().SequenceEqual(incoming.OriginalBytes))
                            throw new HistoryIntegrityConflictException($"PackId {incoming.Pack.PackId} already exists with different bytes.");
                    var existingIds = existingById.Keys.ToHashSet();
                    newCount = project.Packs.Count(p => !existingIds.Contains(p.Pack.PackId));
                    duplicates = project.Packs.Length - newCount;
                    new HistoryMigrationStateStore(id, repository.Paths.LocalStateRoot).MarkImported(
                        await InspectLocalStateAsync(repository).ConfigureAwait(false) != HistoryTransferLocalStateStatus.Ready);
                    await repository.ImportAsync(project.Packs.Select(p => (ReadOnlyMemory<byte>)p.OriginalBytes), token).ConfigureAwait(false);
                    committed = true;
                    if (runtime is not null)
                    {
                        await runtime.EnsureIndexCurrentAsync(CancellationToken.None).ConfigureAwait(false);
                        await runtime.RefreshLocalStateHealthAsync(CancellationToken.None).ConfigureAwait(false);
                        runtime.ChangeFeed.Publish(id, HistoryChangeKind.PacksImported,
                            project.Packs.Where(p => !existingIds.Contains(p.Pack.PackId)).Select(p => p.Pack.PackId.ToString()));
                    }
                    results.Add(new(id, newCount, duplicates, orphan, HistoryTransferCommitStatus.Committed,
                        await InspectLocalStateAsync(repository).ConfigureAwait(false)));
                }
                finally { if (mutation is not null) await mutation.DisposeAsync().ConfigureAwait(false); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested && !committed)
            { results.Add(new(id, 0, duplicates, orphan, HistoryTransferCommitStatus.Canceled, HistoryTransferLocalStateStatus.InitializationRequired)); canceled = true; }
            catch (Exception ex)
            {
                var status = committed ? HistoryTransferCommitStatus.CommittedRecoveryRequired : HistoryTransferCommitStatus.Failed;
                try { repository.Imports.RequireRecovered(); }
                catch (HistoryPackImportRecoveryRequiredException) { status = HistoryTransferCommitStatus.CommittedRecoveryRequired; }
                results.Add(new(id, status == HistoryTransferCommitStatus.CommittedRecoveryRequired ? newCount : 0, duplicates,
                    orphan, status, HistoryTransferLocalStateStatus.RecoveryRequired, ex.ToString(), Classify(ex)));
            }
            finally { if (runtime is null) repository.Dispose(); }
        }
        return new(results.ToImmutable());
    }

    private static HistoryTransferErrorKind Classify(Exception error) => error switch
    {
        HistoryPackCompatibilityException => HistoryTransferErrorKind.Format,
        HistoryRepositoryException or InvalidDataException or JsonException => HistoryTransferErrorKind.Data,
        UnauthorizedAccessException => HistoryTransferErrorKind.Permission,
        IOException io when (io.HResult & 0xFFFF) is 112 or 39 => HistoryTransferErrorKind.Space,
        IOException => HistoryTransferErrorKind.Io,
        _ => HistoryTransferErrorKind.Unexpected
    };

    private static async Task<HistoryTransferLocalStateStatus> InspectLocalStateAsync(FileHistoryRepository repository)
    {
        using var workspaceStore = new HistoryWorkspaceStore(repository.ConfigId, Path.Combine(repository.Paths.LocalStateRoot, "workspace.json"));
        using var catalogStore = new LocalReplicaCatalogStore(repository.ConfigId, Path.Combine(repository.Paths.LocalStateRoot, "replicas.json"));
        var workspace = await workspaceStore.LoadAsync().ConfigureAwait(false);
        var catalog = await catalogStore.LoadAsync().ConfigureAwait(false);
        if (workspace.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible
            || catalog.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible)
            return HistoryTransferLocalStateStatus.RecoveryRequired;
        return workspace.Status == DeviceLocalStateStatus.Valid && catalog.Status == DeviceLocalStateStatus.Valid
            ? HistoryTransferLocalStateStatus.Ready : HistoryTransferLocalStateStatus.InitializationRequired;
    }
    private sealed record ParsedProject(HistoryTransferProject Project, HistoryPackReadResult[] Packs);
    private sealed record ParsedTransfer(int Format, string Version, ParsedProject[] Projects, string FileSha256);
    private async Task<ParsedTransfer> ReadAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.GroupBy(e => e.FullName, StringComparer.Ordinal).Any(g => g.Count() != 1))
            throw new HistoryIntegrityConflictException("Transfer contains duplicate ZIP entries.");
        var bytes = await ReadEntryAsync(archive, "manifest.json", token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(bytes);
        var format = json.RootElement.GetProperty("formatVersion").GetInt32();
        HistoryTransferProject[] projects;
        var version = "unknown";
        if (format == 1)
        {
            var manifest = JsonSerializer.Deserialize<HistoryTransferManifest>(bytes, JsonOptions) ?? throw new InvalidDataException("Empty manifest.");
            ValidateManifest(manifest.Magic, manifest.IncludesPayloads);
            projects = [new(manifest.ConfigId, manifest.ConfigId.Value, [], 0, 0, manifest.PackSha256)];
        }
        else if (format == 2)
        {
            var manifest = JsonSerializer.Deserialize<HistoryTransferBatchManifest>(bytes, JsonOptions) ?? throw new InvalidDataException("Empty manifest.");
            ValidateManifest(manifest.Magic, manifest.IncludesPayloads);
            if (manifest.Projects.IsDefaultOrEmpty) throw new InvalidDataException("Transfer has no projects.");
            projects = manifest.Projects.ToArray(); version = manifest.ApplicationVersion;
        }
        else throw new HistoryPackCompatibilityException($"Unsupported History transfer format {format}.");
        if (projects.Select(p => p.ConfigId).Distinct().Count() != projects.Length)
            throw new HistoryIntegrityConflictException("Transfer contains duplicate project identities.");
        var declared = new HashSet<string>(StringComparer.Ordinal) { "manifest.json" };
        var parsed = new List<ParsedProject>();
        foreach (var project in projects)
        {
            if (project.PackSha256 is null || project.Sources.IsDefault
                || project.Sources.Select(s => s.SourceId).Distinct().Count() != project.Sources.Length)
                throw new InvalidDataException("Invalid project manifest.");
            var prefix = format == 1 ? "" : Prefix(project.ConfigId);
            var descriptorPath = prefix + "repository.json";
            declared.Add(descriptorPath);
            var descriptor = await ReadEntryAsync(archive, descriptorPath, token).ConfigureAwait(false);
            if (HistoryRepositoryDescriptor.Parse(descriptor).ConfigId != project.ConfigId
                || !descriptor.AsSpan().SequenceEqual(HistoryRepositoryDescriptor.Create(project.ConfigId).ToCanonicalBytes()))
                throw new HistoryIntegrityConflictException("Transfer descriptor is not canonical.");
            var packs = new List<HistoryPackReadResult>();
            foreach (var entry in project.PackSha256)
            {
                var id = PackId.Parse(entry.Key);
                if (id.ToString() != entry.Key) throw new HistoryIntegrityConflictException("Pack identity is not canonical.");
                var name = prefix + $"packs/{id}.frpack"; declared.Add(name);
                var packBytes = await ReadEntryAsync(archive, name, token).ConfigureAwait(false);
                if (!StringComparer.Ordinal.Equals(HistoryPackCodec.ComputeSha256(packBytes), entry.Value))
                    throw new HistoryIntegrityConflictException($"Transfer Pack {id} hash differs from manifest.");
                var pack = _codec.Decode(packBytes);
                if (pack.Pack.PackId != id) throw new HistoryIntegrityConflictException("Transfer path differs from Pack identity.");
                packs.Add(pack);
            }
            new HistoryRepositoryValidator(_codec).Validate(project.ConfigId, packs);
            parsed.Add(new(Describe(project.ConfigId, project.Name, project.Sources, project.PackSha256, packs), packs.ToArray()));
        }
        if (archive.Entries.Any(e => !declared.Contains(e.FullName))) throw new HistoryIntegrityConflictException("Transfer contains undeclared entries.");
        return new(format, version, parsed.ToArray(), hash);
    }
    private HistoryTransferProject Describe(HistoryConfigId id, string name, ImmutableArray<HistoryTransferSource> sources,
        ImmutableSortedDictionary<string, string> hashes, IEnumerable<HistoryPackReadResult> packs)
    {
        var facts = packs.SelectMany(p => p.Pack.Objects).Where(o => o.SchemaVersion == 1 && HistoryObjectKinds.IsKnown(o.Kind))
            .DistinctBy(o => (o.Kind, o.Id)).Select(_codec.DeserializeKnown).ToArray();
        var versions = facts.OfType<SourceVersion>().ToArray();
        if (sources.IsDefaultOrEmpty) sources = versions.GroupBy(v => v.SourceId)
            .Select(g => g.OrderByDescending(v => v.CreatedAtUtc).First()).Select(v => new HistoryTransferSource(v.SourceId,
                v.SourceDescriptorSnapshot.DisplayName, v.SourceDescriptorSnapshot.PathHint)).ToImmutableArray();
        return new(id, name, sources, versions.Length, facts.OfType<BranchUpdate>().Select(b => b.BranchId).Distinct().Count(), hashes);
    }
    private static string Prefix(HistoryConfigId id) => "repositories/" + HistoryRepositoryPaths.EncodeConfigPathSegment(id) + "/";
    private static void ValidateManifest(string magic, bool payloads)
    { if (magic != HistoryTransferManifest.CurrentMagic || payloads) throw new HistoryPackCompatibilityException("Unsupported History transfer manifest."); }
    private static async Task WriteAsync(ZipArchive archive, string name, byte[] bytes, CancellationToken token)
    { await using var output = archive.CreateEntry(name, CompressionLevel.Optimal).Open(); await output.WriteAsync(bytes, token).ConfigureAwait(false); }
    private static async Task<byte[]> ReadEntryAsync(ZipArchive archive, string name, CancellationToken token)
    {
        await using var input = (archive.GetEntry(name) ?? throw new InvalidDataException($"Transfer entry '{name}' is missing.")).Open();
        using var output = new MemoryStream(); await input.CopyToAsync(output, token).ConfigureAwait(false); return output.ToArray();
    }
}

using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
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

public sealed record HistoryTransferManifest(
    string Magic,
    int FormatVersion,
    HistoryConfigId ConfigId,
    bool IncludesPayloads,
    ImmutableSortedDictionary<string, string> PackSha256)
{
    public const string CurrentMagic = "FolderRewindHistoryTransfer";
    public const int CurrentFormatVersion = 1;
}

public sealed record HistoryTransferImportResult(
    HistoryConfigId ConfigId,
    int InstalledPacks,
    int DuplicatePacks,
    bool ImportedAsOrphanRepository);

public sealed class HistoryRepositoryTransferService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly HistoryPackCodec _codec = new();
    private readonly Func<HistoryConfigId, HistoryRuntime?>? _runtimeResolver;

    public HistoryRepositoryTransferService(Func<HistoryConfigId, HistoryRuntime?>? runtimeResolver = null)
        => _runtimeResolver = runtimeResolver;

    public async Task ExportAsync(
        HistoryRuntime runtime,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var destination = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("Transfer destination has no parent.");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var packs = await runtime.Repository.ReadAllPacksAsync(cancellationToken).ConfigureAwait(false);
            var hashes = packs.ToImmutableSortedDictionary(
                item => item.Pack.PackId.ToString(),
                item => HistoryPackCodec.ComputeSha256(item.OriginalBytes),
                StringComparer.Ordinal);
            var manifest = new HistoryTransferManifest(
                HistoryTransferManifest.CurrentMagic,
                HistoryTransferManifest.CurrentFormatVersion,
                runtime.ConfigId,
                IncludesPayloads: false,
                hashes);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteEntryAsync(archive, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions), cancellationToken)
                    .ConfigureAwait(false);
                await WriteEntryAsync(
                    archive,
                    "repository.json",
                    HistoryRepositoryDescriptor.Create(runtime.ConfigId).ToCanonicalBytes(),
                    cancellationToken).ConfigureAwait(false);
                foreach (var pack in packs.OrderBy(item => item.Pack.PackId.ToString(), StringComparer.Ordinal))
                    await WriteEntryAsync(archive, $"packs/{pack.Pack.PackId}.frpack", pack.OriginalBytes, cancellationToken)
                        .ConfigureAwait(false);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public async Task<HistoryTransferImportResult> ImportAsync(
        string transferPath,
        string configDirectory,
        CancellationToken cancellationToken = default)
    {
        var bytes = new List<byte[]>();
        HistoryTransferManifest manifest;
        HistoryRepositoryDescriptor descriptor;
        await using (var stream = File.OpenRead(Path.GetFullPath(transferPath)))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            manifest = JsonSerializer.Deserialize<HistoryTransferManifest>(
                await ReadEntryAsync(archive, "manifest.json", cancellationToken).ConfigureAwait(false), JsonOptions)
                ?? throw new InvalidDataException("Transfer manifest is empty.");
            if (manifest.Magic != HistoryTransferManifest.CurrentMagic
                || manifest.FormatVersion != HistoryTransferManifest.CurrentFormatVersion
                || manifest.IncludesPayloads
                || manifest.PackSha256 is null)
                throw new HistoryPackCompatibilityException("Unsupported History transfer manifest.");
            var descriptorBytes = await ReadEntryAsync(archive, "repository.json", cancellationToken).ConfigureAwait(false);
            descriptor = HistoryRepositoryDescriptor.Parse(descriptorBytes);
            if (descriptor.ConfigId != manifest.ConfigId
                || !descriptorBytes.AsSpan().SequenceEqual(HistoryRepositoryDescriptor.Create(manifest.ConfigId).ToCanonicalBytes()))
                throw new HistoryIntegrityConflictException("Transfer repository descriptor is not canonical.");
            foreach (var pair in manifest.PackSha256)
            {
                var packBytes = await ReadEntryAsync(archive, $"packs/{pair.Key}.frpack", cancellationToken).ConfigureAwait(false);
                if (!StringComparer.Ordinal.Equals(HistoryPackCodec.ComputeSha256(packBytes), pair.Value))
                    throw new HistoryIntegrityConflictException($"Transfer Pack {pair.Key} hash differs from manifest.");
                var decoded = _codec.Decode(packBytes);
                if (!StringComparer.Ordinal.Equals(decoded.Pack.PackId.ToString(), pair.Key))
                    throw new HistoryIntegrityConflictException("Transfer Pack path differs from envelope identity.");
                bytes.Add(packBytes);
            }
            if (archive.Entries.Count(entry => entry.FullName.StartsWith("packs/", StringComparison.Ordinal)) != manifest.PackSha256.Count)
                throw new HistoryIntegrityConflictException("Transfer contains undeclared Pack entries.");
        }

        var runtime = _runtimeResolver?.Invoke(manifest.ConfigId);
        bool orphan = runtime is null;
        var repository = runtime?.Repository ?? new FileHistoryRepository(
            manifest.ConfigId,
            HistoryRepositoryPaths.ForConfigDirectory(configDirectory, manifest.ConfigId));
        IAsyncDisposable? mutationLease = null;
        try
        {
            if (runtime is not null)
            {
                mutationLease = await runtime.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }

            var existing = await repository.ReadAllPacksAsync(cancellationToken).ConfigureAwait(false);
            var decodedIncoming = bytes.Select(item => _codec.Decode(item)).ToArray();
            new HistoryRepositoryValidator(_codec).Validate(manifest.ConfigId, existing.Concat(decodedIncoming));
            var results = await repository.ImportAsync(bytes.Select(item => (ReadOnlyMemory<byte>)item), cancellationToken)
                .ConfigureAwait(false);
            var installed = results
                .Where(item => item.Disposition == HistoryPackInstallDisposition.Installed)
                .ToArray();
            if (runtime is not null && installed.Length > 0)
            {
                await runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
                runtime.ChangeFeed.Publish(
                    runtime.ConfigId,
                    HistoryChangeKind.PacksImported,
                    installed.Select(item => item.PackId.ToString()));
            }

            return new(
                manifest.ConfigId,
                installed.Length,
                results.Count(item => item.Disposition == HistoryPackInstallDisposition.Duplicate),
                orphan);
        }
        finally
        {
            if (mutationLease is not null)
            {
                await mutationLease.DisposeAsync().ConfigureAwait(false);
            }
            if (orphan)
            {
                repository.Dispose();
            }
        }
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, byte[] bytes, CancellationToken token)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var output = entry.Open();
        await output.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchive archive, string name, CancellationToken token)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"Transfer entry '{name}' is missing.");
        await using var input = entry.Open();
        using var output = new MemoryStream();
        await input.CopyToAsync(output, token).ConfigureAwait(false);
        return output.ToArray();
    }
}

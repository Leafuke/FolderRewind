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
        var service = new HistoryRepositoryBatchTransferService(_runtimeResolver);
        if ((await service.PreviewAsync(transferPath, cancellationToken).ConfigureAwait(false)).Projects.Length != 1)
            throw new InvalidOperationException("Use the batch transfer service for multiple projects.");
        var batch = await service.ImportAsync(
            transferPath, configDirectory, token: cancellationToken).ConfigureAwait(false);
        if (batch.Projects.Length != 1) throw new InvalidOperationException("Use the batch transfer service for multiple projects.");
        var result = batch.Projects[0];
        if (result.CommitStatus != HistoryTransferCommitStatus.Committed)
            throw new InvalidOperationException(result.Diagnostic);
        return new(result.ConfigId, result.InstalledPacks, result.DuplicatePacks, result.ImportedAsOrphanRepository);
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

using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Discovery;

public sealed class LudusaviManifestCacheService
{
    public const long MaximumManifestBytes = 64L * 1024 * 1024;
    private const string ManifestFileName = "manifest.yaml";
    private const string SecondaryManifestFileName = ".ludusavi.yaml";
    private const string IndexFileName = "index.v1.json.gz";
    private const string MetadataFileName = "metadata.json";
    private const string PointerFileName = "current.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _cacheRoot;
    private readonly string _generationsRoot;
    private readonly LudusaviManifestCompiler _compiler;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LudusaviManifestCacheService(string cacheRoot, LudusaviManifestCompiler? compiler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _generationsRoot = Path.Combine(_cacheRoot, "generations");
        _compiler = compiler ?? new LudusaviManifestCompiler();
    }

    public async Task<LudusaviManifestUpdateResult> DownloadAndCompileAsync(
        HttpClient httpClient,
        Uri manifestUri,
        string? secondaryManifestPath,
        string? overridePath,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(manifestUri);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentMetadata = await TryLoadCurrentMetadataAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
            if (!string.IsNullOrWhiteSpace(currentMetadata?.ETag)
                && EntityTagHeaderValue.TryParse(currentMetadata.ETag, out var etag))
            {
                request.Headers.IfNoneMatch.Add(etag);
            }

            progress?.Report(new DiscoveryProgress
            {
                ProviderId = "ludusavi",
                Phase = "download",
                Message = manifestUri.ToString()
            });
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                var current = await LoadCurrentAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The server returned 304, but no valid local manifest generation exists.");
                return new LudusaviManifestUpdateResult
                {
                    Status = LudusaviManifestUpdateStatus.NotModified,
                    Metadata = current.Metadata,
                    Index = current.Index
                };
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumManifestBytes)
            {
                throw new InvalidDataException("The Ludusavi manifest exceeds the 64 MiB safety limit.");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var primaryBytes = await ReadBoundedAsync(responseStream, cancellationToken).ConfigureAwait(false);
            var result = await CompileAndStoreAsync(
                primaryBytes,
                secondaryManifestPath,
                overridePath,
                sourceKind: "upstream",
                sourceUri: manifestUri.ToString(),
                etag: response.Headers.ETag?.ToString() ?? string.Empty,
                progress,
                cancellationToken).ConfigureAwait(false);
            return new LudusaviManifestUpdateResult
            {
                Status = LudusaviManifestUpdateStatus.Updated,
                Metadata = result.Metadata,
                Index = result.Index
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LudusaviManifestUpdateResult> ImportAndCompileAsync(
        string manifestPath,
        string? secondaryManifestPath,
        string? overridePath,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var primaryBytes = await ReadBoundedFileAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            var result = await CompileAndStoreAsync(
                primaryBytes,
                secondaryManifestPath,
                overridePath,
                sourceKind: "local",
                sourceUri: Path.GetFullPath(manifestPath),
                etag: string.Empty,
                progress,
                cancellationToken).ConfigureAwait(false);
            return new LudusaviManifestUpdateResult
            {
                Status = LudusaviManifestUpdateStatus.Updated,
                Metadata = result.Metadata,
                Index = result.Index
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(LudusaviManifestCacheMetadata Metadata, LudusaviCompiledIndex Index)?> LoadCurrentAsync(
        CancellationToken cancellationToken)
    {
        var pointerPath = Path.Combine(_cacheRoot, PointerFileName);
        if (!File.Exists(pointerPath))
        {
            return null;
        }

        await using var pointerStream = new FileStream(pointerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var pointer = await JsonSerializer.DeserializeAsync<LudusaviManifestPointer>(
            pointerStream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (pointer == null || !IsSafeGenerationId(pointer.GenerationId))
        {
            return null;
        }

        var generationRoot = GetGenerationRoot(pointer.GenerationId);
        var metadataPath = Path.Combine(generationRoot, MetadataFileName);
        var indexPath = Path.Combine(generationRoot, IndexFileName);
        if (!File.Exists(metadataPath) || !File.Exists(indexPath))
        {
            return null;
        }

        await using var metadataStream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var metadata = await JsonSerializer.DeserializeAsync<LudusaviManifestCacheMetadata>(
            metadataStream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (metadata == null || !string.Equals(metadata.GenerationId, pointer.GenerationId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        await using var indexFile = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var gzip = new GZipStream(indexFile, CompressionMode.Decompress, leaveOpen: false);
        var index = await JsonSerializer.DeserializeAsync<LudusaviCompiledIndex>(
            gzip,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (index == null
            || index.SchemaVersion != LudusaviCompiledIndex.CurrentSchemaVersion
            || !string.Equals(index.SourceSha256, metadata.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return (metadata, index);
    }

    private async Task<(LudusaviManifestCacheMetadata Metadata, LudusaviCompiledIndex Index)> CompileAndStoreAsync(
        byte[] primaryBytes,
        string? secondaryManifestPath,
        string? overridePath,
        string sourceKind,
        string sourceUri,
        string etag,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        var secondaryBytes = string.IsNullOrWhiteSpace(secondaryManifestPath)
            ? Array.Empty<byte>()
            : await ReadBoundedFileAsync(secondaryManifestPath, cancellationToken).ConfigureAwait(false);
        var overrideBytes = string.IsNullOrWhiteSpace(overridePath)
            ? Array.Empty<byte>()
            : await ReadBoundedFileAsync(overridePath, cancellationToken).ConfigureAwait(false);
        var overrides = overrideBytes.Length == 0
            ? new FolderRewindGameOverrideDocument()
            : JsonSerializer.Deserialize<FolderRewindGameOverrideDocument>(overrideBytes, JsonOptions)
              ?? throw new InvalidDataException("The FolderRewind override document is empty.");

        var sourceHash = ComputeSourceHash(primaryBytes, secondaryBytes, overrideBytes);
        var generationId = sourceHash.ToLowerInvariant();
        progress?.Report(new DiscoveryProgress
        {
            ProviderId = "ludusavi",
            Phase = "compile",
            Message = generationId
        });

        using var primaryStream = new MemoryStream(primaryBytes, writable: false);
        using var secondaryStream = secondaryBytes.Length == 0
            ? null
            : new MemoryStream(secondaryBytes, writable: false);
        var index = _compiler.Compile(
            primaryStream,
            secondaryStream,
            overrides,
            sourceHash,
            cancellationToken);
        var metadata = new LudusaviManifestCacheMetadata
        {
            GenerationId = generationId,
            SourceSha256 = sourceHash,
            SourceKind = sourceKind,
            SourceUri = sourceUri,
            ETag = etag,
            UpdatedAtUtc = DateTime.UtcNow
        };

        var previous = await TryLoadPointerAsync(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(_generationsRoot);
        var finalGenerationRoot = GetGenerationRoot(generationId);
        if (!Directory.Exists(finalGenerationRoot))
        {
            var temporaryRoot = Path.Combine(_generationsRoot, $".{generationId}.{Guid.NewGuid():N}.tmp");
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                await File.WriteAllBytesAsync(Path.Combine(temporaryRoot, ManifestFileName), primaryBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (secondaryBytes.Length > 0)
                {
                    await File.WriteAllBytesAsync(Path.Combine(temporaryRoot, SecondaryManifestFileName), secondaryBytes, cancellationToken)
                        .ConfigureAwait(false);
                }

                await WriteJsonAsync(Path.Combine(temporaryRoot, MetadataFileName), metadata, cancellationToken)
                    .ConfigureAwait(false);
                await WriteCompressedIndexAsync(Path.Combine(temporaryRoot, IndexFileName), index, cancellationToken)
                    .ConfigureAwait(false);
                Directory.Move(temporaryRoot, finalGenerationRoot);
            }
            finally
            {
                if (Directory.Exists(temporaryRoot))
                {
                    Directory.Delete(temporaryRoot, recursive: true);
                }
            }
        }

        AtomicFileService.Write(
            Path.Combine(_cacheRoot, PointerFileName),
            stream => JsonSerializer.Serialize(stream, new LudusaviManifestPointer
            {
                GenerationId = generationId
            }, JsonOptions));
        PruneGenerations(generationId, previous?.GenerationId);
        return (metadata, index);
    }

    private async Task<LudusaviManifestCacheMetadata?> TryLoadCurrentMetadataAsync(
        CancellationToken cancellationToken)
    {
        var current = await LoadCurrentAsync(cancellationToken).ConfigureAwait(false);
        return current?.Metadata;
    }

    private async Task<LudusaviManifestPointer?> TryLoadPointerAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_cacheRoot, PointerFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<LudusaviManifestPointer>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Manifest input was not found.", path);
        }
        if (info.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("Manifest input exceeds the 64 MiB safety limit.");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > MaximumManifestBytes)
            {
                throw new InvalidDataException("Manifest input exceeds the 64 MiB safety limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string ComputeSourceHash(params byte[][] inputs)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var input in inputs)
        {
            var length = BitConverter.GetBytes(input.LongLength);
            hash.AppendData(length);
            hash.AppendData(input);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteCompressedIndexAsync(
        string path,
        LudusaviCompiledIndex index,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var gzip = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: false);
        await JsonSerializer.SerializeAsync(gzip, index, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private string GetGenerationRoot(string generationId)
    {
        if (!IsSafeGenerationId(generationId))
        {
            throw new InvalidDataException("Invalid manifest generation ID.");
        }

        var path = Path.GetFullPath(Path.Combine(_generationsRoot, generationId));
        var rootWithSeparator = _generationsRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Manifest generation path escapes the cache root.");
        }

        return path;
    }

    private static bool IsSafeGenerationId(string? generationId) =>
        generationId is { Length: 64 }
        && generationId.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private void PruneGenerations(string currentGenerationId, string? previousGenerationId)
    {
        if (!Directory.Exists(_generationsRoot))
        {
            return;
        }

        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            currentGenerationId
        };
        if (IsSafeGenerationId(previousGenerationId))
        {
            retained.Add(previousGenerationId!);
        }

        foreach (var directory in Directory.EnumerateDirectories(_generationsRoot))
        {
            var name = Path.GetFileName(directory);
            if (!IsSafeGenerationId(name) || retained.Contains(name))
            {
                continue;
            }

            Directory.Delete(directory, recursive: true);
        }
    }
}

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
    public const string CompilerVersion = "ludusavi-compiler-v3.1-case-sensitive-identities";
    private const string ManifestFileName = "manifest.yaml";
    private const string SecondaryManifestFileName = ".ludusavi.yaml";
    private const string OverrideFileName = "folderrewind.override.json";
    private const string IndexFileName = "index.v3.json.gz";
    private const string MetadataFileName = "metadata.json";
    private const string PointerFileName = "current.json";

    private static readonly byte[] EmptyInput = Array.Empty<byte>();
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
            var pointer = await TryLoadPointerAsync(cancellationToken).ConfigureAwait(false);
            var current = await LoadCurrentInternalAsync(pointer, repairPointer: true, cancellationToken).ConfigureAwait(false);
            var seed = current == null
                ? await TryLoadRebuildSeedAsync(pointer, cancellationToken).ConfigureAwait(false)
                : await ReadSeedAsync(current.Value.Metadata.GenerationId, current.Value.Metadata, cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
            if (!string.IsNullOrWhiteSpace(seed?.Metadata.ETag)
                && EntityTagHeaderValue.TryParse(seed.Metadata.ETag, out var etag))
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

            byte[] primaryBytes;
            string etagValue;
            var status = LudusaviManifestUpdateStatus.Updated;
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (seed == null)
                {
                    throw new InvalidDataException("The server returned 304, but no cached primary manifest is available.");
                }
                primaryBytes = seed.PrimaryBytes;
                etagValue = seed.Metadata.ETag;
                status = LudusaviManifestUpdateStatus.NotModified;
            }
            else
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaximumManifestBytes)
                {
                    throw new InvalidDataException("The Ludusavi manifest exceeds the 64 MiB safety limit.");
                }
                await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                primaryBytes = await ReadBoundedAsync(responseStream, cancellationToken).ConfigureAwait(false);
                etagValue = response.Headers.ETag?.ToString() ?? string.Empty;
            }

            var warnings = new List<string>();
            var secondaryBytes = await ReadOptionalInputAsync(secondaryManifestPath, "secondary manifest", warnings, cancellationToken)
                .ConfigureAwait(false);
            var overrideBytes = await ReadOptionalInputAsync(overridePath, "FolderRewind override", warnings, cancellationToken)
                .ConfigureAwait(false);
            var generationId = ComputeGenerationId(primaryBytes, secondaryBytes, overrideBytes);
            if (status == LudusaviManifestUpdateStatus.NotModified
                && current != null
                && string.Equals(current.Value.Metadata.GenerationId, generationId, StringComparison.OrdinalIgnoreCase))
            {
                return new LudusaviManifestUpdateResult
                {
                    Status = status,
                    Metadata = WithWarnings(current.Value.Metadata, warnings),
                    Index = current.Value.Index
                };
            }

            var compiled = await CompileAndStoreAsync(
                primaryBytes,
                secondaryBytes,
                overrideBytes,
                "upstream",
                manifestUri.ToString(),
                etagValue,
                warnings,
                progress,
                cancellationToken).ConfigureAwait(false);
            return new LudusaviManifestUpdateResult
            {
                Status = LudusaviManifestUpdateStatus.Updated,
                Metadata = compiled.Metadata,
                Index = compiled.Index
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
            var warnings = new List<string>();
            var primaryBytes = await ReadBoundedFileAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            var secondaryBytes = await ReadOptionalInputAsync(secondaryManifestPath, "secondary manifest", warnings, cancellationToken)
                .ConfigureAwait(false);
            var overrideBytes = await ReadOptionalInputAsync(overridePath, "FolderRewind override", warnings, cancellationToken)
                .ConfigureAwait(false);
            var result = await CompileAndStoreAsync(
                primaryBytes,
                secondaryBytes,
                overrideBytes,
                "local",
                Path.GetFullPath(manifestPath),
                string.Empty,
                warnings,
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pointer = await TryLoadPointerAsync(cancellationToken).ConfigureAwait(false);
            return await LoadCurrentInternalAsync(pointer, repairPointer: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(LudusaviManifestCacheMetadata Metadata, LudusaviCompiledIndex Index)?> EnsureCurrentAsync(
        string? secondaryManifestPath,
        string? overridePath,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pointer = await TryLoadPointerAsync(cancellationToken).ConfigureAwait(false);
            var current = await LoadCurrentInternalAsync(pointer, repairPointer: true, cancellationToken).ConfigureAwait(false);
            pointer = await TryLoadPointerAsync(cancellationToken).ConfigureAwait(false);
            var seed = current == null
                ? await TryLoadRebuildSeedAsync(pointer, cancellationToken).ConfigureAwait(false)
                : await ReadSeedAsync(current.Value.Metadata.GenerationId, current.Value.Metadata, cancellationToken).ConfigureAwait(false);
            if (seed == null)
            {
                return null;
            }

            var warnings = new List<string>();
            byte[] primaryBytes;
            if (string.Equals(seed.Metadata.SourceKind, "local", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(seed.Metadata.SourceUri)
                && File.Exists(seed.Metadata.SourceUri))
            {
                primaryBytes = await ReadBoundedFileAsync(seed.Metadata.SourceUri, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                primaryBytes = seed.PrimaryBytes;
                if (string.Equals(seed.Metadata.SourceKind, "local", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add("The imported primary manifest is no longer available; the cached copy is being used.");
                }
            }

            var secondaryBytes = await ReadOptionalInputAsync(secondaryManifestPath, "secondary manifest", warnings, cancellationToken)
                .ConfigureAwait(false);
            var overrideBytes = await ReadOptionalInputAsync(overridePath, "FolderRewind override", warnings, cancellationToken)
                .ConfigureAwait(false);
            var generationId = ComputeGenerationId(primaryBytes, secondaryBytes, overrideBytes);
            if (current != null
                && string.Equals(current.Value.Metadata.GenerationId, generationId, StringComparison.OrdinalIgnoreCase))
            {
                return (WithWarnings(current.Value.Metadata, warnings), current.Value.Index);
            }

            progress?.Report(new DiscoveryProgress
            {
                ProviderId = "ludusavi",
                Phase = "compile",
                Message = "Rebuilding the Ludusavi index because a manifest input or compiler version changed"
            });
            return await CompileAndStoreAsync(
                primaryBytes,
                secondaryBytes,
                overrideBytes,
                seed.Metadata.SourceKind,
                seed.Metadata.SourceUri,
                seed.Metadata.ETag,
                warnings,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(LudusaviManifestCacheMetadata Metadata, LudusaviCompiledIndex Index)?> LoadCurrentInternalAsync(
        LudusaviManifestPointer? pointer,
        bool repairPointer,
        CancellationToken cancellationToken)
    {
        if (pointer == null)
        {
            return null;
        }

        var current = await LoadGenerationAsync(pointer.CurrentGenerationId, cancellationToken).ConfigureAwait(false);
        if (current != null)
        {
            return current;
        }

        var previous = await LoadGenerationAsync(pointer.PreviousGenerationId, cancellationToken).ConfigureAwait(false);
        if (previous == null)
        {
            return null;
        }

        if (repairPointer)
        {
            WritePointer(new LudusaviManifestPointer
            {
                CurrentGenerationId = pointer.PreviousGenerationId,
                PreviousGenerationId = string.Empty
            });
        }
        return (WithWarnings(previous.Value.Metadata, new[]
        {
            "The current Ludusavi cache generation was invalid; the previous generation was restored."
        }), previous.Value.Index);
    }

    private async Task<(LudusaviManifestCacheMetadata Metadata, LudusaviCompiledIndex Index)?> LoadGenerationAsync(
        string? generationId,
        CancellationToken cancellationToken)
    {
        if (!IsSafeGenerationId(generationId))
        {
            return null;
        }
        try
        {
            return await LoadGenerationAtRootAsync(GetGenerationRoot(generationId!), generationId!, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<(LudusaviManifestCacheMetadata Metadata, LudusaviCompiledIndex Index)?> LoadGenerationAtRootAsync(
        string generationRoot,
        string generationId,
        CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(generationRoot, MetadataFileName);
        var indexPath = Path.Combine(generationRoot, IndexFileName);
        var manifestPath = Path.Combine(generationRoot, ManifestFileName);
        if (!File.Exists(metadataPath) || !File.Exists(indexPath) || !File.Exists(manifestPath))
        {
            return null;
        }

        await using var metadataStream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var metadata = await JsonSerializer.DeserializeAsync<LudusaviManifestCacheMetadata>(
            metadataStream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (metadata == null
            || !string.Equals(metadata.GenerationId, generationId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(metadata.CompilerVersion, CompilerVersion, StringComparison.Ordinal))
        {
            return null;
        }

        var primaryBytes = await ReadBoundedFileAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var secondaryPath = Path.Combine(generationRoot, SecondaryManifestFileName);
        var overridePath = Path.Combine(generationRoot, OverrideFileName);
        var secondaryBytes = File.Exists(secondaryPath)
            ? await ReadBoundedFileAsync(secondaryPath, cancellationToken).ConfigureAwait(false)
            : EmptyInput;
        var overrideBytes = File.Exists(overridePath)
            ? await ReadBoundedFileAsync(overridePath, cancellationToken).ConfigureAwait(false)
            : EmptyInput;
        if (!MetadataMatchesInputs(metadata, primaryBytes, secondaryBytes, overrideBytes)
            || !string.Equals(ComputeGenerationId(primaryBytes, secondaryBytes, overrideBytes), generationId, StringComparison.OrdinalIgnoreCase))
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
        byte[] secondaryBytes,
        byte[] overrideBytes,
        string sourceKind,
        string sourceUri,
        string etag,
        IReadOnlyList<string> warnings,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sourceHash = ComputeSourceHash(primaryBytes, secondaryBytes, overrideBytes);
        var generationId = ComputeGenerationId(primaryBytes, secondaryBytes, overrideBytes);
        progress?.Report(new DiscoveryProgress
        {
            ProviderId = "ludusavi",
            Phase = "compile",
            Message = generationId
        });

        var overrides = overrideBytes.Length == 0
            ? new FolderRewindGameOverrideDocument()
            : JsonSerializer.Deserialize<FolderRewindGameOverrideDocument>(overrideBytes, JsonOptions)
              ?? throw new InvalidDataException("The FolderRewind override document is empty.");
        using var primaryStream = new MemoryStream(primaryBytes, writable: false);
        using var secondaryStream = secondaryBytes.Length == 0 ? null : new MemoryStream(secondaryBytes, writable: false);
        var index = _compiler.Compile(primaryStream, secondaryStream, overrides, sourceHash, cancellationToken);
        var metadata = new LudusaviManifestCacheMetadata
        {
            GenerationId = generationId,
            SourceSha256 = sourceHash,
            PrimarySha256 = ComputeHash(primaryBytes),
            SecondarySha256 = ComputeHash(secondaryBytes),
            OverrideSha256 = ComputeHash(overrideBytes),
            CompilerVersion = CompilerVersion,
            SourceKind = sourceKind,
            SourceUri = sourceUri,
            ETag = etag,
            UpdatedAtUtc = DateTime.UtcNow,
            Warnings = warnings.ToList()
        };

        var oldPointer = await TryLoadPointerAsync(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(_generationsRoot);
        var finalRoot = GetGenerationRoot(generationId);
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
            if (overrideBytes.Length > 0)
            {
                await File.WriteAllBytesAsync(Path.Combine(temporaryRoot, OverrideFileName), overrideBytes, cancellationToken)
                    .ConfigureAwait(false);
            }
            await WriteJsonAsync(Path.Combine(temporaryRoot, MetadataFileName), metadata, cancellationToken).ConfigureAwait(false);
            await WriteCompressedIndexAsync(Path.Combine(temporaryRoot, IndexFileName), index, cancellationToken).ConfigureAwait(false);
            if (await LoadGenerationAtRootAsync(temporaryRoot, generationId, cancellationToken).ConfigureAwait(false) == null)
            {
                throw new InvalidDataException("The newly compiled Ludusavi generation failed validation.");
            }

            ReplaceGenerationDirectory(temporaryRoot, finalRoot);
        }
        finally
        {
            TryDeleteDirectory(temporaryRoot);
        }

        var previousId = oldPointer?.CurrentGenerationId;
        if (string.Equals(previousId, generationId, StringComparison.OrdinalIgnoreCase))
        {
            previousId = oldPointer?.PreviousGenerationId;
        }
        var pointer = new LudusaviManifestPointer
        {
            CurrentGenerationId = generationId,
            PreviousGenerationId = IsSafeGenerationId(previousId) ? previousId! : string.Empty
        };
        WritePointer(pointer);
        PruneGenerations(pointer.CurrentGenerationId, pointer.PreviousGenerationId);
        return (metadata, index);
    }

    private async Task<GenerationSeed?> TryLoadRebuildSeedAsync(
        LudusaviManifestPointer? pointer,
        CancellationToken cancellationToken)
    {
        if (pointer == null)
        {
            return null;
        }
        foreach (var generationId in new[] { pointer.CurrentGenerationId, pointer.PreviousGenerationId })
        {
            if (!IsSafeGenerationId(generationId))
            {
                continue;
            }
            try
            {
                var metadataPath = Path.Combine(GetGenerationRoot(generationId), MetadataFileName);
                if (!File.Exists(metadataPath))
                {
                    continue;
                }
                await using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var metadata = await JsonSerializer.DeserializeAsync<LudusaviManifestCacheMetadata>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                if (metadata == null)
                {
                    continue;
                }
                var seed = await ReadSeedAsync(generationId, metadata, cancellationToken).ConfigureAwait(false);
                if (seed != null)
                {
                    return seed;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    private async Task<GenerationSeed?> ReadSeedAsync(
        string generationId,
        LudusaviManifestCacheMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (!IsSafeGenerationId(generationId))
        {
            return null;
        }
        var path = Path.Combine(GetGenerationRoot(generationId), ManifestFileName);
        return File.Exists(path)
            ? new GenerationSeed(metadata, await ReadBoundedFileAsync(path, cancellationToken).ConfigureAwait(false))
            : null;
    }

    private static async Task<byte[]> ReadOptionalInputAsync(
        string? path,
        string description,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return EmptyInput;
        }
        if (!File.Exists(path))
        {
            warnings.Add($"The configured {description} no longer exists and was omitted: {path}");
            return EmptyInput;
        }
        return await ReadBoundedFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LudusaviManifestPointer?> TryLoadPointerAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_cacheRoot, PointerFileName);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<LudusaviManifestPointer>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WritePointer(LudusaviManifestPointer pointer)
    {
        Directory.CreateDirectory(_cacheRoot);
        AtomicFileService.Write(
            Path.Combine(_cacheRoot, PointerFileName),
            stream => JsonSerializer.Serialize(stream, pointer, JsonOptions));
    }

    private static LudusaviManifestCacheMetadata WithWarnings(
        LudusaviManifestCacheMetadata metadata,
        IEnumerable<string> warnings) => new()
    {
        GenerationId = metadata.GenerationId,
        SourceSha256 = metadata.SourceSha256,
        PrimarySha256 = metadata.PrimarySha256,
        SecondarySha256 = metadata.SecondarySha256,
        OverrideSha256 = metadata.OverrideSha256,
        CompilerVersion = metadata.CompilerVersion,
        SourceKind = metadata.SourceKind,
        SourceUri = metadata.SourceUri,
        ETag = metadata.ETag,
        UpdatedAtUtc = metadata.UpdatedAtUtc,
        Warnings = metadata.Warnings.Concat(warnings).Distinct(StringComparer.Ordinal).ToList()
    };

    private static bool MetadataMatchesInputs(
        LudusaviManifestCacheMetadata metadata,
        byte[] primary,
        byte[] secondary,
        byte[] overrides) =>
        string.Equals(metadata.PrimarySha256, ComputeHash(primary), StringComparison.OrdinalIgnoreCase)
        && string.Equals(metadata.SecondarySha256, ComputeHash(secondary), StringComparison.OrdinalIgnoreCase)
        && string.Equals(metadata.OverrideSha256, ComputeHash(overrides), StringComparison.OrdinalIgnoreCase)
        && string.Equals(metadata.SourceSha256, ComputeSourceHash(primary, secondary, overrides), StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]> ReadBoundedFileAsync(string path, CancellationToken cancellationToken)
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

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
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

    private static string ComputeHash(byte[] input) =>
        Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();

    private static string ComputeSourceHash(params byte[][] inputs)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var input in inputs)
        {
            hash.AppendData(BitConverter.GetBytes(input.LongLength));
            hash.AppendData(input);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputeGenerationId(byte[] primary, byte[] secondary, byte[] overrides)
    {
        var value = string.Join(':',
            CompilerVersion,
            LudusaviCompiledIndex.CurrentSchemaVersion,
            ComputeHash(primary),
            ComputeHash(secondary),
            ComputeHash(overrides));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
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

    private static void ReplaceGenerationDirectory(string temporaryRoot, string finalRoot)
    {
        if (!Directory.Exists(finalRoot))
        {
            Directory.Move(temporaryRoot, finalRoot);
            return;
        }

        var displaced = finalRoot + $".replaced.{Guid.NewGuid():N}";
        Directory.Move(finalRoot, displaced);
        try
        {
            Directory.Move(temporaryRoot, finalRoot);
            TryDeleteDirectory(displaced);
        }
        catch
        {
            if (!Directory.Exists(finalRoot) && Directory.Exists(displaced))
            {
                Directory.Move(displaced, finalRoot);
            }
            throw;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
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
        && generationId.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private void PruneGenerations(string currentGenerationId, string? previousGenerationId)
    {
        try
        {
            if (!Directory.Exists(_generationsRoot))
            {
                return;
            }
            var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { currentGenerationId };
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
                TryDeleteDirectory(directory);
            }
        }
        catch
        {
            // Generation cleanup is opportunistic and must not invalidate an already committed pointer.
        }
    }

    private sealed record GenerationSeed(LudusaviManifestCacheMetadata Metadata, byte[] PrimaryBytes);
}

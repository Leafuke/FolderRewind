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
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Discovery;

public sealed record LudusaviGenerationUpdate(LudusaviManifestUpdateStatus Status, LudusaviGeneration Generation);

public sealed partial class LudusaviManifestCacheService
{
    public async Task<LudusaviGeneration?> PrepareStoredGenerationAsync(IProgress<DiscoveryProgress>? progress, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        var transferred = false;
        try
        {
            var seed = await FindDiskSeedAsync(token).ConfigureAwait(false);
            if (seed is null) return null;
            var root = GetGenerationRoot(seed.Value.Metadata.GenerationId);
            var secondary = Path.Combine(root, SecondaryManifestFileName);
            var overrides = Path.Combine(root, OverrideFileName);
            var generation = await BuildDiskGenerationAsync(seed.Value.PrimaryPath, null,
                File.Exists(secondary) ? secondary : null, File.Exists(overrides) ? overrides : null,
                seed.Value.Metadata.SourceKind, seed.Value.Metadata.SourceUri, seed.Value.Metadata.ETag,
                progress, token, seed.Value.IndexValidated ? seed.Value.Metadata.GenerationId : null).ConfigureAwait(false);
            transferred = true;
            return generation;
        }
        finally { if (!transferred) _gate.Release(); }
    }

    public async Task<LudusaviGenerationUpdate> DownloadGenerationAsync(HttpClient client, Uri uri,
        string? secondaryPath, string? overridePath, IProgress<DiscoveryProgress>? progress, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        var transferred = false;
        try
        {
            var seed = await FindDiskSeedAsync(token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (EntityTagHeaderValue.TryParse(seed?.Metadata.ETag, out var etag)) request.Headers.IfNoneMatch.Add(etag);
            progress?.Report(new DiscoveryProgress { ProviderId = "ludusavi", Phase = "download", Message = uri.ToString() });
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            LudusaviGeneration generation;
            var status = LudusaviManifestUpdateStatus.Updated;
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (seed is null) throw new InvalidDataException("The server returned 304 without a cached manifest.");
                generation = await BuildDiskGenerationAsync(seed.Value.PrimaryPath, null, secondaryPath, overridePath,
                    "upstream", uri.ToString(), seed.Value.Metadata.ETag, progress, token, seed.Value.IndexValidated ? seed.Value.Metadata.GenerationId : null).ConfigureAwait(false);
                if (generation.Metadata.GenerationId == seed.Value.Metadata.GenerationId) status = LudusaviManifestUpdateStatus.NotModified;
            }
            else
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaximumManifestBytes)
                    throw new InvalidDataException("The manifest exceeds the 64 MiB input limit.");
                await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                generation = await BuildDiskGenerationAsync(null, source, secondaryPath, overridePath, "upstream",
                    uri.ToString(), response.Headers.ETag?.ToString() ?? string.Empty, progress, token).ConfigureAwait(false);
            }
            transferred = true;
            return new(status, generation);
        }
        finally { if (!transferred) _gate.Release(); }
    }

    public async Task<LudusaviGenerationUpdate> ImportGenerationAsync(string path, string? secondaryPath,
        string? overridePath, IProgress<DiscoveryProgress>? progress, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        var transferred = false;
        try
        {
            var generation = await BuildDiskGenerationAsync(Path.GetFullPath(path), null, secondaryPath, overridePath,
                "local", Path.GetFullPath(path), string.Empty, progress, token).ConfigureAwait(false);
            transferred = true;
            return new(LudusaviManifestUpdateStatus.Updated, generation);
        }
        finally { if (!transferred) _gate.Release(); }
    }

    private Task<(LudusaviManifestCacheMetadata Metadata, string PrimaryPath, bool IndexValidated)?> FindDiskSeedAsync(CancellationToken token)
        => Task.Run(() => FindDiskSeedCoreAsync(token), token);

    private async Task<(LudusaviManifestCacheMetadata Metadata, string PrimaryPath, bool IndexValidated)?> FindDiskSeedCoreAsync(CancellationToken token)
    {
        var pointer = await TryLoadPointerAsync(token).ConfigureAwait(false);
        if (pointer is null) return null;
        (LudusaviManifestCacheMetadata Metadata, string PrimaryPath, bool IndexValidated)? fallback = null;
        foreach (var id in new[] { pointer.CurrentGenerationId, pointer.PreviousGenerationId })
        {
            if (!IsSafeGenerationId(id)) continue;
            var root = GetGenerationRoot(id);
            var metadataPath = Path.Combine(root, MetadataFileName);
            var primary = Path.Combine(root, ManifestFileName);
            if (!File.Exists(metadataPath) || !File.Exists(primary)) continue;
            try
            {
                await using var file = File.OpenRead(metadataPath);
                var metadata = await JsonSerializer.DeserializeAsync<LudusaviManifestCacheMetadata>(file, JsonOptions, token).ConfigureAwait(false);
                if (metadata is null || metadata.GenerationId != id) continue;
                if (!string.IsNullOrEmpty(metadata.PrimarySha256)
                    && !string.Equals(await HashFileAsync(primary, token).ConfigureAwait(false), metadata.PrimarySha256, StringComparison.OrdinalIgnoreCase)) continue;
                fallback ??= (metadata, primary, false);
                if (metadata.CompilerVersion == CompilerVersion && await ValidateDiskIndexAsync(
                    Path.Combine(root, IndexFileName), metadata.SourceSha256, token).ConfigureAwait(false))
                {
                    if (id != pointer.CurrentGenerationId)
                    {
                        WritePointer(new LudusaviManifestPointer { CurrentGenerationId = id, PreviousGenerationId = string.Empty });
                        metadata = WithWarnings(metadata, ["The current cache was invalid; the previous generation was restored."]);
                    }
                    return (metadata, primary, true);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return fallback;
    }

    private Task<LudusaviGeneration> BuildDiskGenerationAsync(string? primaryPath, Stream? download,
        string? secondaryPath, string? overridePath, string sourceKind, string sourceUri, string etag,
        IProgress<DiscoveryProgress>? progress, CancellationToken token, string? validatedGenerationId = null)
        => Task.Run(() => BuildDiskGenerationCoreAsync(primaryPath, download, secondaryPath, overridePath,
            sourceKind, sourceUri, etag, progress, token, validatedGenerationId), token);

    private async Task<LudusaviGeneration> BuildDiskGenerationCoreAsync(string? primaryPath, Stream? download,
        string? secondaryPath, string? overridePath, string sourceKind, string sourceUri, string etag,
        IProgress<DiscoveryProgress>? progress, CancellationToken token, string? validatedGenerationId = null)
    {
        Directory.CreateDirectory(_generationsRoot);
        var staging = Path.Combine(_generationsRoot, "." + Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(staging);
        try
        {
            var warnings = new List<string>();
            if (sourceKind == "local")
            {
                if (File.Exists(sourceUri)) primaryPath = sourceUri;
                else warnings.Add("The imported primary manifest is no longer available; the cached copy is being used.");
            }
            var primary = Path.Combine(staging, ManifestFileName);
            string primaryHash;
            if (download is not null) primaryHash = await CopyBoundedAsync(download, primary, token).ConfigureAwait(false);
            else
            {
                await using var input = File.OpenRead(primaryPath ?? throw new FileNotFoundException("No primary manifest is available."));
                primaryHash = await CopyBoundedAsync(input, primary, token).ConfigureAwait(false);
            }
            var stagedSecondary = await StageOptionalAsync(secondaryPath, Path.Combine(staging, SecondaryManifestFileName),
                "secondary manifest", warnings, token).ConfigureAwait(false);
            var stagedOverrides = await StageOptionalAsync(overridePath, Path.Combine(staging, OverrideFileName),
                "FolderRewind override", warnings, token).ConfigureAwait(false);
            var secondary = stagedSecondary.Path;
            var overrides = stagedOverrides.Path;
            var hashes = new[] { primaryHash, stagedSecondary.Hash, stagedOverrides.Hash };
            var sourceHash = await HashInputsAsync(new[] { primary, secondary, overrides }, token).ConfigureAwait(false);
            var generationId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(':',
                CompilerVersion, LudusaviCompiledIndex.CurrentSchemaVersion, hashes[0], hashes[1], hashes[2])))).ToLowerInvariant();
            var oldPointer = await TryLoadPointerAsync(token).ConfigureAwait(false);
            var existing = await TryReadDiskMetadataAsync(generationId, token).ConfigureAwait(false);
            if (existing is not null && existing.SourceSha256 == sourceHash
                && (validatedGenerationId == generationId || await ValidateDiskIndexAsync(Path.Combine(GetGenerationRoot(generationId), IndexFileName),
                sourceHash, token).ConfigureAwait(false)))
            {
                if (existing.SourceKind != sourceKind || existing.SourceUri != sourceUri || existing.ETag != etag)
                {
                    existing = new LudusaviManifestCacheMetadata
                    {
                        GenerationId = existing.GenerationId, CompilerVersion = existing.CompilerVersion,
                        SourceSha256 = existing.SourceSha256, PrimarySha256 = existing.PrimarySha256,
                        SecondarySha256 = existing.SecondarySha256, OverrideSha256 = existing.OverrideSha256,
                        SourceKind = sourceKind, SourceUri = sourceUri, ETag = etag,
                        UpdatedAtUtc = DateTime.UtcNow, Warnings = warnings
                    };
                    AtomicFileService.Write(Path.Combine(GetGenerationRoot(generationId), MetadataFileName),
                        stream => JsonSerializer.Serialize(stream, existing, JsonOptions));
                }
                var previous = oldPointer?.PreviousGenerationId == generationId ? null
                    : oldPointer?.CurrentGenerationId == generationId ? oldPointer.PreviousGenerationId : oldPointer?.CurrentGenerationId;
                if (oldPointer?.CurrentGenerationId != generationId)
                    WritePointer(new LudusaviManifestPointer { CurrentGenerationId = generationId,
                        PreviousGenerationId = IsSafeGenerationId(previous) ? previous! : string.Empty });
                return new LudusaviGeneration(WithWarnings(existing, warnings), Path.Combine(GetGenerationRoot(generationId), IndexFileName), _gate);
            }
            progress?.Report(new DiscoveryProgress { ProviderId = "ludusavi", Phase = "compile", Message = I18n.GetString("GameDiscovery_Status_CompilingIndex") });
            await Task.Run(() => _compiler.CompileToIndex(primary, secondary, overrides,
                Path.Combine(staging, IndexFileName), sourceHash, staging, token), token).ConfigureAwait(false);
            var metadata = new LudusaviManifestCacheMetadata { GenerationId = generationId, CompilerVersion = CompilerVersion,
                SourceSha256 = sourceHash, PrimarySha256 = hashes[0], SecondarySha256 = hashes[1], OverrideSha256 = hashes[2],
                SourceKind = sourceKind, SourceUri = sourceUri, ETag = etag, UpdatedAtUtc = DateTime.UtcNow, Warnings = warnings };
            await WriteJsonAsync(Path.Combine(staging, MetadataFileName), metadata, token).ConfigureAwait(false);
            if (!await ValidateDiskIndexAsync(Path.Combine(staging, IndexFileName), sourceHash, token).ConfigureAwait(false))
                throw new InvalidDataException("The compiled generation failed validation.");
            token.ThrowIfCancellationRequested();
            var finalRoot = GetGenerationRoot(generationId);
            ReplaceGenerationDirectory(staging, finalRoot);
            var previousId = oldPointer?.CurrentGenerationId == generationId ? oldPointer.PreviousGenerationId : oldPointer?.CurrentGenerationId;
            WritePointer(new LudusaviManifestPointer { CurrentGenerationId = generationId,
                PreviousGenerationId = IsSafeGenerationId(previousId) ? previousId! : string.Empty });
            PruneGenerations(generationId, previousId);
            return new LudusaviGeneration(metadata, Path.Combine(finalRoot, IndexFileName), _gate);
        }
        finally { TryDeleteDirectory(staging); }
    }

    private async Task<LudusaviManifestCacheMetadata?> TryReadDiskMetadataAsync(string id, CancellationToken token)
    {
        try
        {
            var metadata = await ReadMetadataAsync(id, token).ConfigureAwait(false);
            return metadata?.CompilerVersion == CompilerVersion && metadata.GenerationId == id ? metadata : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static async Task<bool> ValidateDiskIndexAsync(string path, string hash, CancellationToken token)
    {
        try
        {
            await using var file = File.OpenRead(path);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var reader = new LudusaviIndexReader(hash, new());
            await foreach (var _ in reader.ReadAsync(gzip, token).ConfigureAwait(false)) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    private sealed record StagedInput(string? Path, string Hash);

    private static async Task<StagedInput> StageOptionalAsync(string? source, string destination, string description,
        List<string> warnings, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(source)) return new(null, ComputeHash(EmptyInput));
        if (!File.Exists(source)) { warnings.Add($"The configured {description} no longer exists and was omitted: {source}"); return new(null, ComputeHash(EmptyInput)); }
        await using var file = File.OpenRead(source);
        var hash = await CopyBoundedAsync(file, destination, token).ConfigureAwait(false);
        return new(destination, hash);
    }

    private static async Task<string> CopyBoundedAsync(Stream source, string destination, CancellationToken token)
    {
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[64 * 1024];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > MaximumManifestBytes) throw new InvalidDataException("Manifest input exceeds the 64 MiB input limit.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<string> HashFileAsync(string? path, CancellationToken token)
    {
        if (path is null) return Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();
        await using var file = File.OpenRead(path);
        if (file.Length > MaximumManifestBytes) throw new InvalidDataException("Manifest input exceeds the 64 MiB input limit.");
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static async Task<string> HashInputsAsync(IEnumerable<string?> paths, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        foreach (var path in paths)
        {
            var length = path is null ? 0L : new FileInfo(path).Length;
            hash.AppendData(BitConverter.GetBytes(length));
            if (path is null) continue;
            await using var file = File.OpenRead(path);
            int read;
            while ((read = await file.ReadAsync(buffer, token).ConfigureAwait(false)) > 0) hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

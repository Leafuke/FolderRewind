using FolderRewind.Models;
using FolderRewind.Services.Discovery;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderRewind.Tests;

// Tests consume the same disk-generation compiler, reader and cache entry points as the UI.
internal static class LudusaviStreamingFixture
{
    private static readonly JsonSerializerOptions Json = new()
    { Converters = { new JsonStringEnumConverter() } };

    internal sealed record Update(LudusaviManifestUpdateStatus Status,
        LudusaviManifestCacheMetadata Metadata, LudusaviCompiledIndex Index);

    internal static async Task<Update> ImportSnapshotAsync(this LudusaviManifestCacheService cache,
        string path, string? secondary, string? overrides, IProgress<DiscoveryProgress>? progress, CancellationToken token)
    {
        var update = await cache.ImportGenerationAsync(path, secondary, overrides, progress, token);
        await using var generation = update.Generation;
        return new(update.Status, generation.Metadata, await ReadAsync(generation, token));
    }

    internal static async Task<Update> DownloadSnapshotAsync(this LudusaviManifestCacheService cache,
        HttpClient client, Uri uri, string? secondary, string? overrides,
        IProgress<DiscoveryProgress>? progress, CancellationToken token)
    {
        var update = await cache.DownloadGenerationAsync(client, uri, secondary, overrides, progress, token);
        await using var generation = update.Generation;
        return new(update.Status, generation.Metadata, await ReadAsync(generation, token));
    }

    internal static async Task<(LudusaviManifestCacheMetadata Metadata, LudusaviCompiledIndex Index)?> ReadSnapshotAsync(
        this LudusaviManifestCacheService cache, CancellationToken token)
    {
        await using var generation = await cache.PrepareStoredGenerationAsync(null, token);
        return generation is null ? null : (generation.Metadata, await ReadAsync(generation, token));
    }

    internal static async Task<(LudusaviManifestCacheMetadata Metadata, LudusaviCompiledIndex Index)?> EnsureSnapshotAsync(
        this LudusaviManifestCacheService cache, string? secondary, string? overrides,
        IProgress<DiscoveryProgress>? progress, CancellationToken token)
    {
        await using var generation = await cache.PrepareGenerationAsync(secondary, overrides, progress, token);
        return generation is null ? null : (generation.Metadata, await ReadAsync(generation, token));
    }

    internal static async Task<LudusaviCompiledIndex> CompileAsync(Stream primary, Stream? secondary,
        FolderRewindGameOverrideDocument? overrides, string sourceHash, CancellationToken token)
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindStreamingCompiler", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var manifest = Path.Combine(root, "manifest.yaml");
            await using (var file = File.Create(manifest)) await primary.CopyToAsync(file, token);
            string? overlay = null, overrideFile = null;
            if (secondary is not null)
            {
                overlay = Path.Combine(root, "secondary.yaml");
                await using var file = File.Create(overlay);
                await secondary.CopyToAsync(file, token);
            }
            if (overrides is not null)
            {
                overrideFile = Path.Combine(root, "overrides.json");
                await File.WriteAllTextAsync(overrideFile, JsonSerializer.Serialize(overrides, Json), token);
            }
            var index = Path.Combine(root, "index.json.gz");
            new LudusaviManifestCompiler().CompileToIndex(manifest, overlay, overrideFile, index, sourceHash, root, token);
            await using var generation = new LudusaviGeneration(new() { SourceSha256 = sourceHash }, index, new(0, 1));
            return await ReadAsync(generation, token);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal static async Task<LudusaviCompiledIndex> ReadAsync(LudusaviGeneration generation, CancellationToken token)
    {
        var games = new List<LudusaviCompiledGame>();
        await foreach (var game in generation.ReadGamesAsync(token)) games.Add(game);
        return new LudusaviCompiledIndex
        {
            SourceSha256 = generation.Metadata.SourceSha256,
            Games = games, Diagnostics = generation.Diagnostics.ToArray()
        };
    }

    // Provider matching fixtures seed a valid disk index; compiler behavior is tested above.
    internal static LudusaviManifestCacheService SeedCache(string root, IReadOnlyList<LudusaviCompiledGame> games)
    {
        var primary = Encoding.UTF8.GetBytes("{}\n");
        static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var bytes in new[] { primary, Array.Empty<byte>(), Array.Empty<byte>() })
        { digest.AppendData(BitConverter.GetBytes(bytes.LongLength)); digest.AppendData(bytes); }
        var sourceHash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        var id = Hash(Encoding.UTF8.GetBytes(string.Join(':', LudusaviManifestCacheService.CompilerVersion,
            LudusaviCompiledIndex.CurrentSchemaVersion, Hash(primary), Hash([]), Hash([]))));
        var directory = Path.Combine(root, "generations", id);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "manifest.yaml"), primary);
        var metadata = new LudusaviManifestCacheMetadata
        {
            GenerationId = id, SourceSha256 = sourceHash, PrimarySha256 = Hash(primary),
            SecondarySha256 = Hash([]), OverrideSha256 = Hash([]),
            CompilerVersion = LudusaviManifestCacheService.CompilerVersion,
            SourceKind = "upstream", SourceUri = "https://example.test/fixture.yaml"
        };
        File.WriteAllText(Path.Combine(directory, "metadata.json"), JsonSerializer.Serialize(metadata, Json));
        using (var file = File.Create(Path.Combine(directory, "index.v3.json.gz")))
        using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
            JsonSerializer.Serialize(gzip, new LudusaviCompiledIndex { SourceSha256 = sourceHash, Games = games }, Json);
        File.WriteAllText(Path.Combine(root, "current.json"),
            JsonSerializer.Serialize(new LudusaviManifestPointer { CurrentGenerationId = id }, Json));
        return new(root);
    }
}

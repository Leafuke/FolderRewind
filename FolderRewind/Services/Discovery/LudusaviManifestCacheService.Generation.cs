using System;
using FolderRewind.Models;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Discovery;

public sealed partial class LudusaviManifestCacheService
{
    public async Task<LudusaviManifestCacheMetadata?> ReadStatusAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var pointer = await TryLoadPointerAsync(token).ConfigureAwait(false);
            if (pointer is null || !IsSafeGenerationId(pointer.CurrentGenerationId)) return null;
            return await ReadMetadataAsync(pointer.CurrentGenerationId, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<LudusaviManifestCacheMetadata?> ReadMetadataAsync(string id, CancellationToken token)
    {
        if (!IsSafeGenerationId(id)) return null;
        var root = GetGenerationRoot(id);
        if (!File.Exists(Path.Combine(root, ManifestFileName)) || !File.Exists(Path.Combine(root, IndexFileName))
            || !File.Exists(Path.Combine(root, MetadataFileName))) return null;
        await using var file = File.OpenRead(Path.Combine(root, MetadataFileName));
        return await JsonSerializer.DeserializeAsync<LudusaviManifestCacheMetadata>(file, JsonOptions, token).ConfigureAwait(false);
    }

    public async Task<LudusaviGeneration?> PrepareGenerationAsync(string? secondaryPath, string? overridePath,
        IProgress<DiscoveryProgress>? progress, CancellationToken token)
    {
        var current = await EnsureCurrentAsync(secondaryPath, overridePath, progress, token).ConfigureAwait(false);
        if (current is null) return null;
        return await AcquireGenerationAsync(current.Value.Metadata, token).ConfigureAwait(false);
    }

    internal async Task<LudusaviGeneration> AcquireGenerationAsync(LudusaviManifestCacheMetadata metadata, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        return new LudusaviGeneration(metadata, Path.Combine(GetGenerationRoot(metadata.GenerationId), IndexFileName), _gate);
    }
}

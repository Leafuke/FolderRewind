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

public sealed partial class LudusaviManifestCacheService
{
    public const long MaximumManifestBytes = 64L * 1024 * 1024;
    public const string CompilerVersion = "ludusavi-compiler-v3.2-streaming";
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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate;

    public LudusaviManifestCacheService(string cacheRoot, LudusaviManifestCompiler? compiler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _gate = Gates.GetOrAdd(_cacheRoot, _ => new SemaphoreSlim(1, 1));
        _generationsRoot = Path.Combine(_cacheRoot, "generations");
        _compiler = compiler ?? new LudusaviManifestCompiler();
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



    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
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

}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FolderRewind.Plugin.Runtime.Artifacts;

public sealed partial class NativeArtifactStore
{
    private static void ValidateTransfer(NativeArtifactTransfer value)
    {
        if (value.Root.Value == Guid.Empty || !Guid.TryParse(value.RepresentationId, out var representation) || representation == Guid.Empty
            || !Guid.TryParseExact(value.ReplicaId, "N", out var replica) || replica == Guid.Empty)
            throw new InvalidDataException("Invalid Native transfer identity.");
        if (value.JobId is null)
        {
            if (value.TargetFingerprint is not null || value.Id != "download-" + value.ReplicaId)
                throw new InvalidDataException("Native uploads require a Cloud Job binding.");
        }
        else if (!Guid.TryParseExact(value.JobId, "N", out var job) || job == Guid.Empty
            || value.TargetFingerprint is not { Length: 64 } fingerprint || !fingerprint.All(Uri.IsHexDigit)
            || value.Id != "job-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.JobId))).ToLowerInvariant() + "-" + value.ReplicaId)
            throw new InvalidDataException("Invalid Native Cloud Job binding.");
    }

    public IReadOnlyList<NativeArtifactTransfer> Transfers()
    {
        var root = Path.Combine(Root, "transfers");
        if (!Directory.Exists(root)) return [];
        HostArtifactReadService.EnsureNoReparsePoints(Root, root);
        var paths = Directory.EnumerateFiles(root, "*.json").Take(10001).ToArray();
        if (paths.Length > 10000 || paths.Sum(p => new FileInfo(p).Length) > 16 * 1024 * 1024)
            throw new InvalidDataException("Native transfer metadata quota exceeded.");
        return paths.Select(path =>
        {
            HostArtifactReadService.EnsureNoReparsePoints(Root, path);
            var value = ArtifactMetadataFile.ReadJson<NativeArtifactTransfer>(path, 1024 * 1024)
                ?? throw new InvalidDataException("Empty Native transfer intent.");
            ValidateTransfer(value);
            if (value.Id != Path.GetFileNameWithoutExtension(path)) throw new InvalidDataException("Native transfer path binding differs.");
            return value;
        }).ToArray();
    }

    // Caller owns the store gate, shared with GC and recovery.
    public async Task SaveTransferAsync(NativeArtifactTransfer value, CancellationToken token)
    {
        ValidateTransfer(value);
        var current = Transfers();
        var previous = current.SingleOrDefault(t => t.Id == value.Id);
        if (previous is not null && previous with { Complete = value.Complete } != value)
            throw new InvalidDataException("Native transfer cannot change its frozen binding.");
        var root = Path.Combine(Root, "transfers"); var path = Path.Combine(root, value.Id + ".json");
        if (previous is null && current.Count >= 10000 || JsonSerializer.SerializeToUtf8Bytes(value).Length
            + (Directory.Exists(root) ? Directory.EnumerateFiles(root, "*.json").Where(p => p != path).Sum(p => new FileInfo(p).Length) : 0) > 16 * 1024 * 1024)
            throw new InvalidDataException("Native transfer metadata quota exceeded.");
        await WriteAsync(path, value, token);
    }
}

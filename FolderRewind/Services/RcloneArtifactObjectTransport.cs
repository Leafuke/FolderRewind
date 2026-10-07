using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Services;

/// <summary>Receipts attest an earlier verified upload, not a fresh deep read of an unchanged blob.</summary>
internal sealed class RcloneArtifactObjectTransport(
    Func<string, string> remotePath,
    Func<string, string, bool, CancellationToken, Task> copy,
    Func<string, CancellationToken, Task<byte[]>> readBytes,
    Func<string, CancellationToken, Task<long?>> stat) : INativeArtifactObjectTransport
{
    private sealed record Receipt(int Version, long Size, string Sha256);
    private string Remote(string key) => remotePath(ArtifactPathRules.NormalizeRelativePath(key));
    public async Task<bool> HasVerifiedObjectAsync(string key, long size, string sha256, CancellationToken token)
    {
        var remote = Remote(key);
        if (await stat(remote + ".verified.json", token) is null) return false;
        var receipt = JsonSerializer.Deserialize<Receipt>(await readBytes(remote + ".verified.json", token));
        return receipt is { Version: 1 } && receipt.Size == size
            && StringComparer.OrdinalIgnoreCase.Equals(receipt.Sha256, sha256) && await stat(remote, token) == size;
    }
    public async Task UploadObjectAsync(string key, string path, long size, string sha256, CancellationToken token)
    {
        var remote = Remote(key); var verify = Temporary(); var receiptPath = Temporary();
        try
        {
            await copy(path, remote, true, token);
            await copy(remote, verify, false, token);
            await using (var stream = File.OpenRead(verify))
                if (stream.Length != size || !StringComparer.OrdinalIgnoreCase.Equals(
                    Convert.ToHexString(await SHA256.HashDataAsync(stream, token)), sha256)) throw new InvalidDataException("Uploaded Artifact object failed verification.");
            await File.WriteAllBytesAsync(receiptPath, JsonSerializer.SerializeToUtf8Bytes(new Receipt(1, size, sha256.ToLowerInvariant())), token);
            await copy(receiptPath, remote + ".verified.json", true, token);
        }
        finally { if (File.Exists(verify)) File.Delete(verify); if (File.Exists(receiptPath)) File.Delete(receiptPath); }
    }
    public Task DownloadObjectAsync(string key, string path, CancellationToken token) => copy(Remote(key), path, false, token);
    private static string Temporary() => Path.Combine(Path.GetTempPath(), "FolderRewind-object-" + Guid.NewGuid().ToString("N"));
}

using FolderRewind.History.Cloud;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>Checks remote metadata without downloading the payload.</summary>
internal sealed class RcloneRemotePayloadVerifier(
    Func<IReadOnlyList<string>, CancellationToken, Task<string>> run, bool verifyAfterUpload)
{
    public async Task<HistoryReplicaCheck> VerifyAsync(string remote, long expectedSize, string expectedSha256,
        string? localPath, CancellationToken token)
    {
        var arguments = new List<string> { "lsjson", remote, "--stat", "--no-modtime", "--no-mimetype" };
        if (verifyAfterUpload)
            arguments.AddRange(["--hash-type", "SHA-256", "--hash-type", "SHA-1", "--hash-type", "MD5"]);
        string output;
        try { output = await run(arguments, token).ConfigureAwait(false); }
        catch (RcloneProcessException ex) when (ex.ExitCode is 3 or 4)
        { return new(false, HistoryReplicaCheckKind.Existence, "Remote payload is missing."); }

        using var document = JsonDocument.Parse(output);
        var info = document.RootElement;
        if (info.GetProperty("IsDir").GetBoolean())
            return new(false, HistoryReplicaCheckKind.Existence, "Remote payload is a directory.");
        if (!verifyAfterUpload) return new(true, HistoryReplicaCheckKind.Existence, "");
        if (info.GetProperty("Size").GetInt64() != expectedSize)
            return new(false, HistoryReplicaCheckKind.Size, "Remote payload size differs from the expected size.");

        if (info.TryGetProperty("Hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Object)
        {
            foreach (var algorithm in new[] { HashAlgorithmName.SHA256, HashAlgorithmName.SHA1, HashAlgorithmName.MD5 })
            {
                var remoteHash = FindHash(hashes, algorithm);
                if (string.IsNullOrEmpty(remoteHash)) continue;
                // SHA-256 is already part of the committed facts; other hashes need the local bytes.
                if (algorithm != HashAlgorithmName.SHA256 && (localPath is null || !File.Exists(localPath))) continue;
                var length = algorithm == HashAlgorithmName.SHA256 ? 64 : algorithm == HashAlgorithmName.SHA1 ? 40 : 32;
                if (remoteHash.Length != length || !IsHex(remoteHash))
                    throw new InvalidDataException("[cloud.payload-hash-invalid] Invalid remote payload hash.");
                var localHash = algorithm == HashAlgorithmName.SHA256 ? expectedSha256
                    : await HashLocalAsync(localPath!, expectedSize, expectedSha256, algorithm, token).ConfigureAwait(false);
                var matches = StringComparer.OrdinalIgnoreCase.Equals(remoteHash, localHash);
                return new(matches, HistoryReplicaCheckKind.Hash, matches ? "" : "Remote payload hash differs from the local payload.");
            }
        }
        return new(true, HistoryReplicaCheckKind.Size, "");
    }

    private static string? FindHash(JsonElement hashes, HashAlgorithmName algorithm)
    {
        foreach (var property in hashes.EnumerateObject())
            if (string.Equals(property.Name.Replace("-", ""), algorithm.Name, StringComparison.OrdinalIgnoreCase))
                return property.Value.GetString();
        return null;
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value) if (!Uri.IsHexDigit(character)) return false;
        return true;
    }

    private static async Task<string> HashLocalAsync(string path, long expectedSize, string expectedSha256,
        HashAlgorithmName algorithm, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (input.Length != expectedSize) throw new InvalidDataException("[cloud.payload-changed] Local payload size changed.");
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var selected = IncrementalHash.CreateHash(algorithm);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
            {
                sha256.AppendData(buffer, 0, read);
                selected.AppendData(buffer, 0, read);
            }
            if (!StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(sha256.GetHashAndReset()), expectedSha256))
                throw new InvalidDataException("[cloud.payload-changed] Local payload hash changed.");
            return Convert.ToHexString(selected.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public sealed record MigrationCloudSnapshot(string FileName, long Size, string Sha256, DateTimeOffset CreatedAtUtc, int FormatVersion = 1);

/// <summary>A verified payload is published by its small manifest, independently of project synchronization.</summary>
public sealed class RcloneMigrationSnapshotTransport(RcloneExecutionContext context, int timeoutSeconds = 600)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 10, 86400));
    private string Remote(string name) => CloudRemotePath.Append(context.RemoteRoot, "_folderrewind/migration-exports/" + name);

    public async Task<MigrationCloudSnapshot> UploadAsync(string path, CancellationToken token = default)
    {
        var suffix = Path.GetExtension(path).ToLowerInvariant();
        if (suffix is not (".frhistory" or ".json")) throw new ArgumentException("Unsupported migration snapshot file.");
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var now = DateTimeOffset.UtcNow;
        var name = "migration-" + now.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + suffix;
        var snapshot = new MigrationCloudSnapshot(name, source.Length, Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false)).ToLowerInvariant(), now);
        var verification = Path.Combine(Path.GetTempPath(), "FolderRewind-migration-verify-" + Guid.NewGuid().ToString("N"));
        var manifest = verification + ".json";
        try
        {
            await CopyAsync(path, Remote(name), token, immutable: true).ConfigureAwait(false);
            await CopyAsync(Remote(name), verification, token).ConfigureAwait(false);
            await VerifyAsync(verification, snapshot, token).ConfigureAwait(false);
            await File.WriteAllBytesAsync(manifest, JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions), token).ConfigureAwait(false);
            await CopyAsync(manifest, Remote(name + ".manifest.json"), token, immutable: true).ConfigureAwait(false);
            return snapshot;
        }
        finally { if (File.Exists(verification)) File.Delete(verification); if (File.Exists(manifest)) File.Delete(manifest); }
    }

    public async Task<IReadOnlyList<MigrationCloudSnapshot>> ListAsync(CancellationToken token = default)
    {
        var names = new List<string>();
        try
        {
            await RcloneConnectionService.RunLinesAsync(context.CreateStartInfo(["lsf", Remote(""), "--files-only"]), _timeout, token, name =>
            {
                if (name.EndsWith(".manifest.json", StringComparison.Ordinal))
                {
                    ValidateName(name[..^14]); names.Add(name);
                    if (names.Count > 10000) throw new IOException("Migration snapshot list exceeds its limit.");
                }
            }).ConfigureAwait(false);
        }
        catch (RcloneProcessException error) when (error.ExitCode is 3 or 4) { return []; }
        var result = new List<MigrationCloudSnapshot>();
        foreach (var name in names.OrderDescending(StringComparer.Ordinal).Take(200))
        {
            var bytes = await RcloneConnectionService.RunBytesAsync(context.CreateStartInfo(["cat", Remote(name)]), _timeout, token, 64 * 1024).ConfigureAwait(false);
            var snapshot = JsonSerializer.Deserialize<MigrationCloudSnapshot>(bytes, JsonOptions) ?? throw new InvalidDataException("Migration snapshot manifest is empty.");
            Validate(snapshot);
            if (snapshot.FileName + ".manifest.json" != name) throw new InvalidDataException("Migration snapshot manifest name differs from its identity.");
            result.Add(snapshot);
        }
        return result;
    }
    public async Task DownloadAsync(MigrationCloudSnapshot snapshot, string destination, CancellationToken token = default)
    {
        Validate(snapshot); var output = Path.GetFullPath(destination); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await CopyAsync(Remote(snapshot.FileName), temporary, token).ConfigureAwait(false);
            await VerifyAsync(temporary, snapshot, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); File.Move(temporary, output, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private Task<string> CopyAsync(string source, string target, CancellationToken token, bool immutable = false)
        => RcloneConnectionService.RunAsync(context.CreateStartInfo(immutable ? ["copyto", source, target, "--immutable"] : ["copyto", source, target]), _timeout, token);
    private static async Task VerifyAsync(string path, MigrationCloudSnapshot snapshot, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        if (input.Length != snapshot.Size || Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)).ToLowerInvariant() != snapshot.Sha256)
            throw new InvalidDataException("Downloaded migration snapshot failed integrity verification.");
    }
    private static void Validate(MigrationCloudSnapshot snapshot)
    {
        ValidateName(snapshot.FileName);
        if (snapshot.FormatVersion != 1 || snapshot.Size < 0 || snapshot.Sha256 is null || snapshot.Sha256.Length != 64 || snapshot.Sha256.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("Migration snapshot manifest is invalid.");
    }
    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || !name.StartsWith("migration-", StringComparison.Ordinal)
            || !(name.EndsWith(".frhistory", StringComparison.Ordinal) || name.EndsWith(".json", StringComparison.Ordinal))
            || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '.'))
            throw new InvalidDataException("Migration snapshot name is invalid.");
    }
}

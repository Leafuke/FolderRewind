using FolderRewind.History.Cloud;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

// 既有 Native History 服务的 rclone 适配器；共用一个不可变任务上下文。
public sealed class RcloneNativeHistoryTransport(RcloneExecutionContext context, int timeoutSeconds = 600)
    : IHistoryMetadataTransport, IHistoryReplicaTransport
{
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 10, 86400));
    private string Append(params string[] parts) => string.Join('/', new[] { context.RemoteRoot.TrimEnd('/') }.Concat(parts));
    private string HistoryRoot(HistoryConfigId id) => Append("_folderrewind", "history", HistoryRepositoryPaths.EncodeConfigPathSegment(id));
    private string PackPath(HistoryConfigId id, PackId pack) => HistoryRoot(id) + "/packs/" + pack.ToString()[..2] + "/" + pack + ".frpack";
    private string ReplicaRoot(ReplicaId id) => Append("_folderrewind", "replicas", id.ToString());
    public async Task<byte[]?> ReadDescriptorAsync(HistoryConfigId id, CancellationToken token)
    {
        try { return await ReadBytesAsync(HistoryRoot(id) + "/repository.json", token).ConfigureAwait(false); }
        catch (RcloneProcessException ex) when (ex.ExitCode is 3 or 4) { return null; }
    }
    public Task CreateDescriptorOnceAsync(HistoryConfigId id, byte[] bytes, CancellationToken token) => WriteOnceAsync(HistoryRoot(id) + "/repository.json", bytes, token);
    public async Task<IReadOnlyList<PackId>> ListPacksAsync(HistoryConfigId id, CancellationToken token)
    {
        var output = await RunAsync(["lsf", HistoryRoot(id), "--files-only", "--recursive"], token).ConfigureAwait(false);
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > OnboardingOperationBudgets.RemoteInteraction) throw new IOException(I18n.GetString("CloudSetup_OutputTruncated"));
        return lines.Where(line => line.EndsWith(".frpack", StringComparison.OrdinalIgnoreCase))
            .Select(line => PackId.Parse(Path.GetFileNameWithoutExtension(line.Replace('/', Path.DirectorySeparatorChar))))
            .Distinct().ToArray();
    }
    public Task<byte[]> DownloadPackAsync(HistoryConfigId id, PackId pack, CancellationToken token) => ReadBytesAsync(PackPath(id, pack), token);
    public Task UploadPackOnceAsync(HistoryConfigId id, PackId pack, byte[] bytes, CancellationToken token) => WriteOnceAsync(PackPath(id, pack), bytes, token);
    public Task<bool> LegacyHistoryExistsAsync(HistoryConfigId id, CancellationToken token) => Task.FromResult(false);
    public Task UploadAsync(ReplicaId id, string localPath, CancellationToken token) => CopyAsync(localPath, ReplicaRoot(id) + "/payload", true, token);
    public async Task<HistoryReplicaVerification> VerifyRemoteAsync(ReplicaId id, CancellationToken token)
    {
        var temporary = Temporary();
        try
        {
            await CopyAsync(ReplicaRoot(id) + "/payload", temporary, false, token).ConfigureAwait(false);
            await using var file = File.OpenRead(temporary);
            return new(true, file.Length, Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)).ToLowerInvariant(), "");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(false, 0, "", CloudCommandSecurity.Redact(ex.Message)); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public Task CommitManifestOnceAsync(HistoryReplicaManifest manifest, CancellationToken token)
        => WriteOnceAsync(ReplicaRoot(manifest.ReplicaId) + "/manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, RcloneTransportJsonContext.Default.HistoryReplicaManifest), token);
    public Task DownloadAsync(HistoryReplicaManifest manifest, string stagingPath, CancellationToken token)
        => CopyAsync(ReplicaRoot(manifest.ReplicaId) + "/payload", stagingPath, false, token);
    public async Task DeletePhysicalAsync(HistoryReplicaManifest manifest, CancellationToken token)
    {
        await RunAsync(["deletefile", ReplicaRoot(manifest.ReplicaId) + "/payload"], token).ConfigureAwait(false);
        await RunAsync(["deletefile", ReplicaRoot(manifest.ReplicaId) + "/manifest.json"], token).ConfigureAwait(false);
    }
    private Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
        => RcloneConnectionService.RunAsync(context.CreateStartInfo(arguments), _timeout, token);
    private async Task CopyAsync(string source, string destination, bool immutable, CancellationToken token)
        => _ = await RunAsync(immutable ? ["copyto", source, destination, "--immutable"] : ["copyto", source, destination], token).ConfigureAwait(false);
    private async Task<byte[]> ReadBytesAsync(string remote, CancellationToken token)
        => await RcloneConnectionService.RunBytesAsync(context.CreateStartInfo(["cat", remote]), _timeout, token, 64 * 1024 * 1024).ConfigureAwait(false);
    private async Task WriteOnceAsync(string remote, byte[] bytes, CancellationToken token)
    {
        var temporary = Temporary();
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            try { await CopyAsync(temporary, remote, true, token).ConfigureAwait(false); }
            catch (RcloneProcessException)
            {
                var existing = await ReadBytesAsync(remote, token).ConfigureAwait(false);
                if (!existing.AsSpan().SequenceEqual(bytes)) throw new IOException("Remote immutable history object differs.");
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Temporary() => Path.Combine(Path.GetTempPath(), "FolderRewind-native-" + Guid.NewGuid().ToString("N") + ".tmp");
}

[JsonSerializable(typeof(HistoryReplicaManifest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class RcloneTransportJsonContext : JsonSerializerContext;

public sealed class RcloneProcessException(int exitCode, string message) : IOException(message)
{
    public int ExitCode { get; } = exitCode;
}

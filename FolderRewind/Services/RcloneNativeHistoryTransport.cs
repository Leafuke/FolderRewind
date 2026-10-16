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
    : IHistoryMetadataTransport, IHistoryReplicaTransport, FolderRewind.Plugin.Runtime.Artifacts.INativeArtifactObjectTransport
{
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 10, 86400));
    internal Func<CloudJobPhase, CancellationToken, Task>? ObserveJobPhase { get; set; }
    private string Append(params string[] parts) => CloudRemotePath.Append(context.RemoteRoot, parts);
    private RcloneArtifactObjectTransport Objects => new(key => Append("_folderrewind", "artifact-objects", key), CopyAsync, (path, maximum, token) => ReadBytesAsync(path, token, maximum), ObjectSizeAsync, DownloadBoundedAsync);
    public Task<bool> HasVerifiedObjectAsync(string key, long size, string sha256, CancellationToken token) => Objects.HasVerifiedObjectAsync(key, size, sha256, token);
    public Task UploadObjectAsync(string key, string path, long size, string sha256, FolderRewind.Plugin.Runtime.Artifacts.ArtifactTransferBudget budget, CancellationToken token) => Objects.UploadObjectAsync(key, path, size, sha256, budget, token);
    public Task DownloadObjectAsync(string key, string path, long expectedLength, FolderRewind.Plugin.Runtime.Artifacts.ArtifactTransferBudget budget, CancellationToken token) => Objects.DownloadObjectAsync(key, path, expectedLength, budget, token);
    private async Task<long?> ObjectSizeAsync(string remote, CancellationToken token)
    {
        try { using var doc = JsonDocument.Parse(await RunAsync(["lsjson", remote, "--stat"], token)); return doc.RootElement.GetProperty("Size").GetInt64(); }
        catch (RcloneProcessException ex) when (ex.ExitCode is 3 or 4) { return null; }
    }
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
        var packs = new HashSet<PackId>();
        // 完整仓库枚举独立于交互式浏览预算；有界流式读取，达到容量即失败，绝不以截断集合做 union。
        await RcloneConnectionService.RunLinesAsync(context.CreateStartInfo(["lsf", HistoryRoot(id), "--files-only", "--recursive"]), _timeout, token, line =>
        {
            if (!line.EndsWith(".frpack", StringComparison.OrdinalIgnoreCase)) return;
            var name = line.Split('/').Last();
            var pack = PackId.Parse(name[..^7]);
            if (line != "packs/" + pack.ToString()[..2] + "/" + pack + ".frpack")
                throw new InvalidDataException("Commit Pack path does not match its identity.");
            packs.Add(pack);
            if (packs.Count > 200000) throw new IOException(I18n.GetString("CloudSetup_OutputTruncated"));
        }).ConfigureAwait(false);
        return packs.OrderBy(p => p.ToString(), StringComparer.Ordinal).ToArray();
    }
    public Task<byte[]> DownloadPackAsync(HistoryConfigId id, PackId pack, CancellationToken token) => ReadBytesAsync(PackPath(id, pack), token);
    public Task UploadPackOnceAsync(HistoryConfigId id, PackId pack, byte[] bytes, CancellationToken token) => WriteOnceAsync(PackPath(id, pack), bytes, token);
    public Task<bool> LegacyHistoryExistsAsync(HistoryConfigId id, CancellationToken token) => Task.FromResult(false);
    public async Task UploadAsync(ReplicaId id, string localPath, CancellationToken token)
    {
        if (ObserveJobPhase is not null) await ObserveJobPhase(CloudJobPhase.Uploading, token).ConfigureAwait(false);
        await CopyAsync(localPath, ReplicaRoot(id) + "/payload", true, token).ConfigureAwait(false);
    }
    public async Task<HistoryReplicaVerification> VerifyRemoteAsync(ReplicaId id, CancellationToken token)
    {
        if (ObserveJobPhase is not null) await ObserveJobPhase(CloudJobPhase.Verifying, token).ConfigureAwait(false);
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
    public async Task<HistoryReplicaManifest?> ReadManifestAsync(ReplicaId replicaId, CancellationToken token)
    {
        try
        {
            var bytes = await RcloneConnectionService.RunBytesAsync(context.CreateStartInfo(["cat", ReplicaRoot(replicaId) + "/manifest.json"]), _timeout, token, 64 * 1024).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Any(p =>
                p.Name is not ("representationId" or "replicaId" or "artifactRootId" or "objectKey" or "size" or "storageSha256")))
                throw new InvalidDataException("[cloud.manifest-invalid] Unsupported replica manifest format.");
            return JsonSerializer.Deserialize(bytes, RcloneTransportJsonContext.Default.HistoryReplicaManifest)
                ?? throw new InvalidDataException("[cloud.manifest-invalid] Empty replica manifest.");
        }
        catch (RcloneProcessException ex) when (ex.ExitCode is 3 or 4) { return null; }
        catch (JsonException ex) { throw new InvalidDataException("[cloud.manifest-invalid] " + CloudCommandSecurity.Redact(ex.Message), ex); }
    }
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
    private async Task<byte[]> ReadBytesAsync(string remote, CancellationToken token, int maximumBytes = 64 * 1024 * 1024)
        => await RcloneConnectionService.RunBytesAsync(context.CreateStartInfo(["cat", remote]), _timeout, token, maximumBytes).ConfigureAwait(false);
    private Task DownloadBoundedAsync(string remote, string path, long expectedLength,
        FolderRewind.Plugin.Runtime.Artifacts.ArtifactTransferBudget budget, CancellationToken token)
        => RcloneConnectionService.RunToFileAsync(context.CreateStartInfo(["cat", remote]), _timeout, token, path, expectedLength, budget);
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

using FolderRewind.History.Cloud;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Plugin.Runtime.Artifacts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

// 既有 Native History 服务的 rclone 适配器；共用一个不可变任务上下文。
public sealed class RcloneNativeHistoryTransport(RcloneExecutionContext context, int timeoutSeconds = 600, bool verifyAfterUpload = false)
    : IHistoryMetadataTransport, IHistoryReplicaTransport, FolderRewind.Plugin.Runtime.Artifacts.INativeArtifactObjectTransport
{
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 10, 86400));
    internal Func<CloudJobPhase, CancellationToken, Task>? ObserveJobPhase { get; set; }
    private string Append(params string[] parts) => CloudRemotePath.Append(context.RemoteRoot, parts);
    private RcloneArtifactObjectTransport Objects => new(key => Append("_folderrewind", "artifact-objects", key),
        CopyPayloadAsync, CheckPayloadAsync, DownloadBoundedAsync);
    public Task<bool> HasVerifiedObjectAsync(string key, long size, string sha256, CancellationToken token) => Objects.HasVerifiedObjectAsync(key, size, sha256, token);
    public Task<bool> HasVerifiedObjectAsync(string key, long size, string sha256, string? localPath, CancellationToken token) => Objects.HasVerifiedObjectAsync(key, size, sha256, localPath, token);
    public Task UploadObjectAsync(string key, string path, long size, string sha256, FolderRewind.Plugin.Runtime.Artifacts.ArtifactTransferBudget budget, CancellationToken token) => Objects.UploadObjectAsync(key, Path.GetFullPath(path), size, sha256, budget, token);
    public Task DownloadObjectAsync(string key, string path, long expectedLength, FolderRewind.Plugin.Runtime.Artifacts.ArtifactTransferBudget budget, CancellationToken token) => Objects.DownloadObjectAsync(key, path, expectedLength, budget, token);
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
    public Task UploadAsync(ReplicaId id, string localPath, CancellationToken token)
        => CopyPayloadAsync(Path.GetFullPath(localPath), ReplicaRoot(id) + "/payload", true, token);
    private async Task CopyPayloadAsync(string source, string destination, bool immutable, CancellationToken token)
    {
        if (ObserveJobPhase is not null) await ObserveJobPhase(CloudJobPhase.Uploading, token).ConfigureAwait(false);
        await CopyFileAsync(source, destination, immutable, token, payload: true).ConfigureAwait(false);
    }
    public Task<HistoryReplicaCheck> VerifyRemoteAsync(HistoryReplicaManifest expected, string? localPath, CancellationToken token)
        => CheckPayloadAsync(ReplicaRoot(expected.ReplicaId) + "/payload", expected.Size, expected.StorageSha256, localPath, token);
    private async Task<HistoryReplicaCheck> CheckPayloadAsync(string remote, long size, string sha256, string? localPath, CancellationToken token)
    {
        if (ObserveJobPhase is not null)
            await ObserveJobPhase(verifyAfterUpload ? CloudJobPhase.Verifying : CloudJobPhase.Checking, token).ConfigureAwait(false);
        return await new RcloneRemotePayloadVerifier(RunAsync, verifyAfterUpload)
            .VerifyAsync(remote, size, sha256, localPath, token).ConfigureAwait(false);
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
    {
        if (manifest.ArtifactRootId is null) return CopyFileAsync(ReplicaRoot(manifest.ReplicaId) + "/payload", stagingPath, false, token);
        if (manifest.Size is < 1 or > 16 * 1024 * 1024) throw new InvalidDataException("Native descriptor exceeds its metadata budget.");
        return DownloadBoundedAsync(ReplicaRoot(manifest.ReplicaId) + "/payload", stagingPath, manifest.Size,
            new FolderRewind.Plugin.Runtime.Artifacts.ArtifactTransferBudget(16 * 1024 * 1024), token);
    }
    public async Task DeletePhysicalAsync(HistoryReplicaManifest manifest, CancellationToken token)
    {
        await RunAsync(["deletefile", ReplicaRoot(manifest.ReplicaId) + "/payload"], token).ConfigureAwait(false);
        await RunAsync(["deletefile", ReplicaRoot(manifest.ReplicaId) + "/manifest.json"], token).ConfigureAwait(false);
    }
    private Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
        => RcloneConnectionService.RunAsync(context.CreateStartInfo(arguments), _timeout, token);
    private async Task CopyFileAsync(string source, string destination, bool immutable, CancellationToken token, bool payload = false)
    {
        token.ThrowIfCancellationRequested();
        if (Path.IsPathFullyQualified(source))
        {
            if (PreviewStoragePaths.IsReserved(source))
                throw new IOException(I18n.GetString("Preview_CloudWorkspaceRejected"));
            if ((File.GetAttributes(source) & FileAttributes.Directory) != 0)
                throw new InvalidDataException(I18n.GetString("Cloud_SourceFileRequired"));
        }
        // 历史载荷、对象与元数据均是单文件；目录过滤器会使 rclone 拒绝单文件 copyto。
        // 预览目录由 Host 在入口拒绝，目录同步的排除规则仍由对应的目录传输路径负责。
        var arguments = new List<string> { "copyto", source, destination };
        // 载荷的上传后检查由项目策略统一执行，避免 rclone 另做一次哈希或大小校验。
        if (payload) { arguments.Add("--ignore-checksum"); arguments.Add("--ignore-size"); }
        // copyto 的单文件路径不能仅依赖 --immutable；跳过已存在目标后仍按项目策略检查。
        if (immutable) { arguments.Add("--immutable"); arguments.Add("--ignore-existing"); }
        _ = await RunAsync(arguments, token).ConfigureAwait(false);
    }
    private async Task<byte[]> ReadBytesAsync(string remote, CancellationToken token, int maximumBytes = 64 * 1024 * 1024)
        => await RcloneConnectionService.RunBytesAsync(context.CreateStartInfo(["cat", remote]), _timeout, token, maximumBytes).ConfigureAwait(false);
    private Task DownloadBoundedAsync(string remote, string path, long expectedLength,
        FolderRewind.Plugin.Runtime.Artifacts.ArtifactTransferBudget budget, CancellationToken token)
        => RcloneConnectionService.RunToFileAsync(context.CreateStartInfo(["cat", remote]), _timeout, token, path, expectedLength, budget);
    private async Task WriteOnceAsync(string remote, byte[] bytes, CancellationToken token)
    {
        await using var work = await OwnedArtifactTemporaryWorkspace.CreateAsync(TemporaryRoot, token);
        var temporary = Path.Combine(work.Root, "payload");
        using var space = ArtifactSpaceBudget.Shared.Reserve(new ArtifactSpaceRequest(temporary, bytes.LongLength));
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            try { await CopyFileAsync(temporary, remote, true, token).ConfigureAwait(false); }
            catch (RcloneProcessException) { /* 已发布的同内容事实允许幂等重试，以下回读决定结果。 */ }
            var existing = await ReadBytesAsync(remote, token, bytes.Length).ConfigureAwait(false);
            if (!existing.AsSpan().SequenceEqual(bytes)) throw new IOException("Remote immutable history object differs.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private string TemporaryRoot => Path.Combine(context.TaskRoot, "native-transfers");
}

[JsonSerializable(typeof(HistoryReplicaManifest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class RcloneTransportJsonContext : JsonSerializerContext;

public sealed class RcloneProcessException(int exitCode, string message) : IOException(message)
{
    public int ExitCode { get; } = exitCode;
}

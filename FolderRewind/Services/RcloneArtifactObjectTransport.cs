using FolderRewind.History.Cloud;
using FolderRewind.Plugin.Runtime.Artifacts;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>Object availability follows the project's upload verification policy.</summary>
internal sealed class RcloneArtifactObjectTransport(
    Func<string, string> remotePath,
    Func<string, string, bool, CancellationToken, Task> copy,
    Func<string, long, string, string?, CancellationToken, Task<HistoryReplicaCheck>> check,
    Func<string, string, long, ArtifactTransferBudget, CancellationToken, Task> download) : INativeArtifactObjectTransport
{
    private string Remote(string key) => remotePath(ArtifactPathRules.NormalizeRelativePath(key));
    public Task<bool> HasVerifiedObjectAsync(string key, long size, string sha256, CancellationToken token)
        => HasVerifiedObjectAsync(key, size, sha256, null, token);
    public async Task<bool> HasVerifiedObjectAsync(string key, long size, string sha256, string? localPath, CancellationToken token)
        => (await check(Remote(key), size, sha256, localPath, token).ConfigureAwait(false)).Success;
    public async Task UploadObjectAsync(string key, string path, long size, string sha256, ArtifactTransferBudget budget, CancellationToken token)
    {
        var remote = Remote(key);
        Observe("CloudObject:before");
        await copy(path, remote, true, token).ConfigureAwait(false);
        Observe("CloudObject:after");
        var result = await check(remote, size, sha256, path, token).ConfigureAwait(false);
        if (!result.Success) throw new InvalidDataException("[cloud.payload-check-failed] " + result.Diagnostic);
    }
    public Task DownloadObjectAsync(string key, string path, long expectedLength, ArtifactTransferBudget budget, CancellationToken token)
        => download(Remote(key), path, expectedLength, budget, token);
    private static void Observe(string stage)
    {
#if NATIVE_ARTIFACT_ACCEPTANCE
        Development.NativeArtifactAcceptance.Observe(stage);
#endif
    }
}

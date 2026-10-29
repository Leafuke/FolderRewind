using FolderRewind.History.Domain;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Cloud;

/// <summary>Representation payload preparation and reuse evidence; scheduling belongs to Cloud Jobs.</summary>
public class HistoryReplicaPayloadProtocol(Func<RepresentationId, CancellationToken, Task<string?>> localPath)
{
    public virtual async Task<PreparedHistoryReplicaPayload?> PrepareAsync(VersionRepresentation representation, ReplicaId replicaId, CancellationToken token)
    {
        if (representation.Kind == RepresentationKind.PluginArtifact)
            throw new InvalidOperationException("Native payload protocol is required.");
        var path = await localPath(representation.RepresentationId, token).ConfigureAwait(false);
        return path is not null && File.Exists(path) ? new(path) : null;
    }

    public virtual async Task<bool> CanReuseAsync(VersionRepresentation representation, HistoryReplicaManifest manifest,
        IHistoryReplicaTransport transport, CancellationToken token)
    {
        if (representation.Kind == RepresentationKind.PluginArtifact) return false;
        var path = await localPath(representation.RepresentationId, token).ConfigureAwait(false);
        var verified = await transport.VerifyRemoteAsync(manifest, path, token).ConfigureAwait(false);
        return verified.Success;
    }
}

public sealed class PreparedHistoryReplicaPayload(string path, Func<CancellationToken, Task>? publish = null,
    Func<CancellationToken, Task>? complete = null, Action? dispose = null) : IDisposable
{
    public string Path { get; } = path;
    public Task PublishAsync(CancellationToken token) => publish?.Invoke(token) ?? Task.CompletedTask;
    public Task CompleteAsync(CancellationToken token) => complete?.Invoke(token) ?? Task.CompletedTask;
    public void Dispose() => dispose?.Invoke();
}

using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Artifacts;

public sealed partial class NativeArtifactStore
{
    private string CorruptionPath(ArtifactLedgerEntry node) => Path.Combine(Root, "integrity", node.ArtifactId.Value.ToString("N") + ".json");
    private Task RecordCorruptionAsync(ArtifactLedgerEntry node, CancellationToken token)
        => WriteAsync(CorruptionPath(node), new { node.ArtifactId, node.StorageSha256 }, token);
    /// <summary>Pins the graph without claiming fresh byte verification of unread nodes.</summary>
    public async Task<NativeArtifactReadLease> OpenLazyAsync(ArtifactId root, CancellationToken token)
    {
        var held = await EnterAsync(token);
        try { return OpenLazyWithoutGate(await Ledger.LoadAsync(token), root, held); }
        catch { held.Dispose(); throw; }
    }

    internal NativeArtifactReadLease OpenLazyWithoutGate(ArtifactLedgerDocument ledger, ArtifactId root, IDisposable held,
        IReadOnlyDictionary<ArtifactId, string>? verifiedDirectories = null, NativeArtifactReadLease? baseline = null)
    {
        var closure = NativeArtifactClosureLimits.Validate(ledger, root);
        var map = ledger.Artifacts.ToDictionary(a => a.ArtifactId);
        var nodes = closure.TopologicallySorted.Select(id => map[id]).ToArray();
        var handles = new Dictionary<ArtifactContentHandle, ArtifactLedgerEntry>();
        var snapshots = new List<BackupArtifactSnapshot>();
        foreach (var node in nodes)
        {
            var payload = PayloadPath(node);
            if (File.Exists(CorruptionPath(node))) throw new InvalidDataException("Artifact has an observed integrity failure; deep verification or replica repair is required.");
            if (node.LocalAvailability != ArtifactAvailability.Available || !File.Exists(payload) || new FileInfo(payload).Length != node.StorageSize)
                throw new InvalidDataException("Artifact dependency is missing or its size changed.");
            var envelope = Path.Combine(Path.GetDirectoryName(payload)!, "envelope.json");
            if (!File.Exists(envelope) || new FileInfo(envelope).Length > 4096)
                throw new InvalidDataException("Artifact envelope metadata is missing or invalid.");
            var header = ArtifactMetadataFile.ReadJson<NativeEnvelope>(envelope, 4096) ?? throw new InvalidDataException("Empty Native envelope.");
            if (header.Version != 1) throw new InvalidDataException("[history.unsupported-experimental-format] Unsupported Native envelope version.");
            var handle = new ArtifactContentHandle(Guid.NewGuid().ToString("N"));
            handles.Add(handle, node);
            snapshots.Add(new(node.ArtifactId, node.Format, node.FormatVersion, node.RestoreStrategyId, handle,
                node.LogicalSha256, node.LogicalSize, node.Completeness, node.CoreCaptureMode,
                node.ConfigId, node.FolderId, node.Dependencies));
        }
        return new(snapshots.AsReadOnly(), new LazyRead(this, ledger, handles, verifiedDirectories, baseline), held, string.Empty, nodes);
    }

    private sealed class LazyRead(NativeArtifactStore store, ArtifactLedgerDocument ledger,
        IReadOnlyDictionary<ArtifactContentHandle, ArtifactLedgerEntry> handles,
        IReadOnlyDictionary<ArtifactId, string>? verifiedDirectories, NativeArtifactReadLease? baseline) : IArtifactReadService, IAsyncDisposable
    {
        private readonly Dictionary<ArtifactContentHandle, NativeArtifactReadLease> _opened = [];
        private readonly ArtifactExtractionBudget _decoded = new();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _disposed;
        private async Task<NativeArtifactReadLease> Open(ArtifactContentHandle handle, CancellationToken token)
        {
            await _gate.WaitAsync(token);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_opened.TryGetValue(handle, out var old)) return old;
                if (!handles.TryGetValue(handle, out var node)) throw new InvalidDataException("Handle is outside the pinned closure.");
                var snapshot = new BackupArtifactSnapshot(node.ArtifactId, node.Format, node.FormatVersion, node.RestoreStrategyId, handle,
                    node.LogicalSha256, node.LogicalSize, node.Completeness, node.CoreCaptureMode, node.ConfigId, node.FolderId, node.Dependencies);
                if (verifiedDirectories?.TryGetValue(node.ArtifactId, out var verified) == true)
                {
                    var reused = new NativeArtifactReadLease([snapshot], new HostArtifactReadService(new Dictionary<ArtifactContentHandle, string>
                        { [handle] = verified }), new ActionLease(() => { }), string.Empty, [node]);
                    _opened.Add(handle, reused); return reused;
                }
                var prior = baseline?.Artifacts.SingleOrDefault(a => a.ArtifactId == node.ArtifactId);
                if (prior is not null)
                {
                    var reused = new NativeArtifactReadLease([snapshot], new ReadAlias(baseline!.Read, prior.Content),
                        new ActionLease(() => { }), string.Empty, [node]);
                    _opened.Add(handle, reused); return reused;
                }
                var isolated = ledger with { Artifacts = [node with { Dependencies = [] }] };
                var opened = await store.OpenWithoutGateAsync(isolated, node.ArtifactId, new ActionLease(() => { }), token, _decoded);
                _opened.Add(handle, opened);
                return opened;
            }
            finally { _gate.Release(); }
        }
        public async ValueTask<IReadOnlyList<ArtifactFileEntry>> ListFilesAsync(ArtifactContentHandle content, CancellationToken token)
        { var lease = await Open(content, token); return await lease.Read.ListFilesAsync(lease.Artifacts[0].Content, token); }
        public async ValueTask<Stream> OpenReadAsync(ArtifactContentHandle content, string path, CancellationToken token)
        { var lease = await Open(content, token); return await lease.Read.OpenReadAsync(lease.Artifacts[0].Content, path, token); }
        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync();
            try
            {
                if (_disposed) return;
                _disposed = true;
                List<Exception>? errors = null;
                foreach (var lease in _opened.Values)
                {
                    try { await lease.DisposeAsync(); }
                    catch (Exception error) { (errors ??= []).Add(error); }
                }
                _opened.Clear();
                if (errors is not null) throw new AggregateException("Artifact read cleanup failed.", errors);
            }
            finally { _gate.Release(); }
        }
    }

    // Non-owning alias: the prepared operation owns the baseline lease until candidate views close.
    private sealed class ReadAlias(IArtifactReadService read, ArtifactContentHandle original) : IArtifactReadService
    {
        public ValueTask<IReadOnlyList<ArtifactFileEntry>> ListFilesAsync(ArtifactContentHandle handle, CancellationToken token)
            => read.ListFilesAsync(original, token);
        public ValueTask<Stream> OpenReadAsync(ArtifactContentHandle handle, string path, CancellationToken token)
            => read.OpenReadAsync(original, path, token);
    }
}

internal sealed class CombinedArtifactReadService(ArtifactContentHandle primaryHandle,
    IArtifactReadService primary, IArtifactReadService? baseline) : IArtifactReadService
{
    private IArtifactReadService Resolve(ArtifactContentHandle handle)
        => handle == primaryHandle ? primary : baseline ?? throw new InvalidDataException("Unrecognized Artifact handle.");
    public ValueTask<IReadOnlyList<ArtifactFileEntry>> ListFilesAsync(ArtifactContentHandle handle, CancellationToken token)
        => Resolve(handle).ListFilesAsync(handle, token);
    public ValueTask<Stream> OpenReadAsync(ArtifactContentHandle handle, string path, CancellationToken token)
        => Resolve(handle).OpenReadAsync(handle, path, token);
}

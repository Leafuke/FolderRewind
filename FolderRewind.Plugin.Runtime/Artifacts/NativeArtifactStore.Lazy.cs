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

    internal NativeArtifactReadLease OpenLazyWithoutGate(ArtifactLedgerDocument ledger, ArtifactId root, IDisposable held)
    {
        var closure = ArtifactLedgerValidator.BuildClosure(ledger, [root]);
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
            if (!File.Exists(envelope) || new FileInfo(envelope).Length > 4096
                || System.Text.Json.JsonSerializer.Deserialize<NativeEnvelope>(File.ReadAllBytes(envelope))?.Version != 1)
                throw new InvalidDataException("Artifact envelope metadata is missing or invalid.");
            var handle = new ArtifactContentHandle(Guid.NewGuid().ToString("N"));
            handles.Add(handle, node);
            snapshots.Add(new(node.ArtifactId, node.Format, node.FormatVersion, node.RestoreStrategyId, handle,
                node.LogicalSha256, node.LogicalSize, node.Completeness, node.CoreCaptureMode,
                node.ConfigId, node.FolderId, node.Dependencies));
        }
        return new(snapshots.AsReadOnly(), new LazyRead(this, ledger, handles), held, string.Empty, nodes);
    }

    private sealed class LazyRead(NativeArtifactStore store, ArtifactLedgerDocument ledger,
        IReadOnlyDictionary<ArtifactContentHandle, ArtifactLedgerEntry> handles) : IArtifactReadService, IAsyncDisposable
    {
        private readonly Dictionary<ArtifactContentHandle, NativeArtifactReadLease> _opened = [];
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
                var isolated = ledger with { Artifacts = [node with { Dependencies = [] }] };
                var opened = await store.OpenWithoutGateAsync(isolated, node.ArtifactId, new ActionLease(() => { }), token);
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
            try { if (_disposed) return; _disposed = true; foreach (var lease in _opened.Values) await lease.DisposeAsync(); _opened.Clear(); }
            finally { _gate.Release(); }
        }
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

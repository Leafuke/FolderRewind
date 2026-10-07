using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;

namespace FolderRewind.Plugin.Runtime.Artifacts;

/// <summary>Host implementation owns compression, passwords and extraction validation.</summary>
public interface IArtifactEnvelopeCodec
{
    Task PackAsync(string logicalDirectory, string payload, bool encrypted, CancellationToken token);
    Task UnpackAsync(string payload, string logicalDirectory, bool encrypted, CancellationToken token);
}

public sealed record NativeEnvelope(int Version, bool Encrypted);
public sealed record NativeArtifactPending(string Id, string VersionId, string RepresentationId, ArtifactId Root,
    string Phase, string? HistoryPackId = null, string? HistoryTransactionId = null)
{
    public string? CorePayloadPath { get; init; }
    public string? CoreRepresentationId { get; init; }
}
public sealed record NativeArtifactTransfer(string Id, ArtifactId Root, string RepresentationId, string ReplicaId, bool Complete);

/// <summary>Config-isolated payload storage. The artifact DAG stays in the existing ledger.</summary>
public sealed partial class NativeArtifactStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Active = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate;
    private readonly IArtifactEnvelopeCodec _codec;
    private readonly Action<string>? _observe;
    public string Root { get; }
    public FileArtifactLedgerStore Ledger { get; }
    public static string ForConfig(string destination, string configId)
        => Path.Combine(Path.GetFullPath(destination), ".folderrewind", "native-artifacts",
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(configId))).ToLowerInvariant());

    public NativeArtifactStore(string root, IArtifactEnvelopeCodec codec, Action<string>? observe = null)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); _codec = codec; _observe = observe;
        _gate = Gates.GetOrAdd(Root, _ => new(1, 1));
        Ledger = new FileArtifactLedgerStore(Root);
        Directory.CreateDirectory(Path.Combine(Root, "pending"));
    }
    public async ValueTask<IDisposable> EnterAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var file = new FileStream(Path.Combine(Root, ".mutation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return new ActionLease(() => { file.Dispose(); _gate.Release(); });
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { await Task.Delay(50, token); }
            }
        }
        catch { _gate.Release(); throw; }
    }

    public string PayloadPath(ArtifactLedgerEntry node)
        => Path.Combine(ArtifactPathRules.ResolveUnderRoot(Root, node.ContentRelativePath), "payload.7z");

    public async Task SavePendingAsync(NativeArtifactPending intent, CancellationToken token)
    {
        _observe?.Invoke(intent.Phase + ":before");
        await WriteAsync(Path.Combine(Root, "pending", RequireId(intent.Id) + ".json"), intent, token);
        _observe?.Invoke(intent.Phase + ":after");
    }
    public IReadOnlyList<NativeArtifactPending> Pending()
        => Directory.EnumerateFiles(Path.Combine(Root, "pending"), "*.json").Select(path =>
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Oversized native artifact intent.");
            var value = JsonSerializer.Deserialize<NativeArtifactPending>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty native artifact intent.");
            if (value.Root.Value == Guid.Empty || !Guid.TryParse(value.VersionId, out _) || !Guid.TryParse(value.RepresentationId, out _)
                || value.Id != Path.GetFileNameWithoutExtension(path)
                || !new[] { "Prepared", "PayloadsSealed", "ArtifactGraphCommitted", "HistoryPackCommitted", "LocalStateApplied", "Complete" }.Contains(value.Phase)
                || (value.HistoryPackId is null) != (value.HistoryTransactionId is null)
                || (value.CorePayloadPath is null) != (value.CoreRepresentationId is null)
                || value.CorePayloadPath is not null && (!Path.IsPathFullyQualified(value.CorePayloadPath) || !Guid.TryParse(value.CoreRepresentationId, out _))
                || value.HistoryPackId is not null && (!Guid.TryParse(value.HistoryPackId, out _) || !Guid.TryParse(value.HistoryTransactionId, out _)))
                throw new InvalidDataException("Invalid native artifact intent.");
            return value;
        }).ToArray();
    public void CompletePending(string id)
    {
        File.Delete(Path.Combine(Root, "pending", RequireId(id) + ".json"));
        Active.TryRemove(Path.Combine(Root, id), out _);
    }

    // Called after the History journal has recovered. The callback must inspect immutable packs,
    // not a potentially stale derived index. No plugin invocation is replayed here.
    public async Task RecoverAsync(Func<NativeArtifactPending, CancellationToken, Task<bool>> isCommitted,
        CancellationToken token)
    {
        using var held = await EnterAsync(token);
        var pending = Pending(); // validate all intents before any cleanup
        await Ledger.RecoverAsync(token);
        foreach (var intent in pending)
        {
            if (Active.ContainsKey(Path.Combine(Root, intent.Id))) continue;
            var committed = await isCommitted(intent, token); // inconsistent/corrupt pack must throw and retain roots
            if (!committed && intent.CorePayloadPath is not null)
                await WriteAsync(Path.Combine(Root, "orphans", intent.Id + ".json"), intent, token);
            // Committed roots are protected by History; orphan roots become eligible for normal GC.
            CompletePending(intent.Id);
        }
        if (!Active.Keys.Any(p => Path.GetDirectoryName(p) == Root))
        {
            foreach (var name in new[] { ".read", ".capture", ".materialize" })
                DeleteDirectory(Path.Combine(Root, name));
            var transactions = Path.Combine(Root, ".folderrewind", "transactions");
            foreach (var directory in Directory.EnumerateDirectories(transactions))
                DeleteDirectory(Path.Combine(directory, "staging"));
        }
    }

    public async Task<PreparedNativeArtifact> PrepareAsync(PluginRuntimeManager runtime, ConfigSnapshot config,
        FolderSnapshot folder, string versionId, string representationId, string primaryDirectory,
        ArtifactTransformPolicy policy, bool encrypted, CancellationToken token, string? corePayloadPath = null, string? coreRepresentationId = null)
    {
        var held = await EnterAsync(token);
        PluginCapabilityLease<IBackupArtifactTransformerCapability>? lease = null;
        ArtifactTransformStagingArea? staging = null;
        try
        {
            var manifest = runtime.GetActiveManifest(policy.TransformerId.PluginId) ?? throw new InvalidOperationException("Active declared transformer required.");
            var declaration = manifest.ArtifactTransformers.SingleOrDefault(d => d.TransformerId == policy.TransformerId)
                ?? throw new InvalidOperationException("Transformer is undeclared.");
            if (!declaration.CompatibleConfigKinds.Contains(config.Kind) || !declaration.CompatibleCoreModes.Contains(CoreCaptureMode.Full)
                || !declaration.CompatibleCompleteness.Contains(ArtifactCompleteness.Complete)
                || !declaration.SupportedFailureBehaviors.Contains(policy.FailureBehavior)) throw new InvalidOperationException("Transformer policy is incompatible.");
            lease = runtime.TryAcquire<IBackupArtifactTransformerCapability>(policy.TransformerId.PluginId,
                c => c.TransformerId == policy.TransformerId, token) ?? throw new InvalidOperationException("Transformer unavailable.");
            var current = await Ledger.LoadAsync(token);
            var transaction = Guid.NewGuid().ToString("N");
            staging = Ledger.CreateStagingArea(transaction, ArtifactTransformLimits.Default.MaximumFiles, ArtifactTransformLimits.Default.MaximumBytes);
            var handle = new ArtifactContentHandle(Guid.NewGuid().ToString("N"));
            var primaryFacts = await HostArtifactReadService.ComputeLogicalFactsAsync(primaryDirectory, token);
            var primary = new BackupArtifactSnapshot(new(Guid.NewGuid()), new(new OwnerId("folderrewind.core"), "archive-set"), 1,
                new(new PluginId("folderrewind.core"), "archive-materializer"), handle, primaryFacts.Sha256, primaryFacts.Size,
                ArtifactCompleteness.Complete, CoreCaptureMode.Full, config.ConfigId, folder.FolderId, []);
            var request = new ArtifactTransformRequest(config, folder, versionId, current.Revision, primary, [], policy.Parameters,
                new HostArtifactReadService(new Dictionary<ArtifactContentHandle, string> { [handle] = primaryDirectory }), staging, NullPluginOperationProgress.Instance);
            var result = await lease.Capability.TransformAsync(request, lease.Context);
            lease.Context.OperationCancellation.ThrowIfCancellationRequested();
            if (result.Outcome is not (OperationOutcome.Success or OperationOutcome.SuccessWithWarnings) || result.Patch is null
                || result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException("Transformer did not produce a successful patch.");
            foreach (var node in result.Patch.AddedArtifacts)
            {
                if (!manifest.ArtifactFormats.Any(f => f.Format == node.Format && f.MinimumVersion <= node.FormatVersion && f.MaximumVersion >= node.FormatVersion)
                    || !manifest.RestoreStrategies.Any(s => s.RestoreStrategyId == node.RestoreStrategyId
                        && s.SupportedCompleteness.Contains(node.Completeness) && s.SupportedRestoreModes.Contains(RestoreMode.Clean)
                        && s.SupportedFormats.Any(f => f.Format == node.Format && f.MinimumVersion <= node.FormatVersion && f.MaximumVersion >= node.FormatVersion)))
                    throw new InvalidDataException("Undeclared artifact format or strategy.");
                using var materializer = runtime.TryAcquire<IRestoreMaterializerCapability>(node.RestoreStrategyId.PluginId,
                    c => c.RestoreStrategyId == node.RestoreStrategyId, token);
                if (materializer is null) throw new InvalidDataException("Declared materializer is unavailable.");
            }
            var facts = await staging.SealAsync("artifacts", token);
            var candidate = ArtifactGraphPatchApplier.Apply(current, result.Patch,
                new(policy.TransformerId.PluginId, config.ConfigId, folder.FolderId, new HashSet<ArtifactId>(), transaction,
                    new(Guid.NewGuid().ToString("N"))), facts);
            var closure = ArtifactLedgerValidator.BuildClosure(candidate, [result.Patch.ResultRootArtifactId]);
            var resultNode = candidate.Artifacts.Single(a => a.ArtifactId == result.Patch.ResultRootArtifactId);
            if (resultNode.Completeness != ArtifactCompleteness.Complete || resultNode.CoreCaptureMode != CoreCaptureMode.Full)
                throw new InvalidDataException("Native Full root must preserve complete Full capture semantics.");
            if (closure.Reachable.Count != result.Patch.AddedArtifacts.Count)
                throw new InvalidDataException("Native Full artifacts must be a self-contained new closure.");
            var pending = new NativeArtifactPending(transaction, versionId, representationId, result.Patch.ResultRootArtifactId, "Prepared")
                { CorePayloadPath = corePayloadPath, CoreRepresentationId = coreRepresentationId };
            await SavePendingAsync(pending, token);
            Active.TryAdd(Path.Combine(Root, transaction), 0);
            return new PreparedNativeArtifact(this, runtime, config, folder, versionId, current, candidate,
                staging, facts, pending, lease, held, result.Diagnostics, encrypted);
        }
        catch
        {
            if (staging is not null) await staging.DisposeAsync();
            lease?.Dispose(); held.Dispose(); throw;
        }
    }

    internal async Task<IReadOnlyDictionary<ArtifactStagingHandle, StagedArtifactFacts>> SealEnvelopesAsync(
        ArtifactTransformStagingArea staging, IReadOnlyDictionary<ArtifactStagingHandle, StagedArtifactFacts> facts,
        bool encrypted, CancellationToken token)
    {
        var result = new Dictionary<ArtifactStagingHandle, StagedArtifactFacts>();
        foreach (var (handle, fact) in facts)
        {
            var source = staging.GetAllocationPath(handle); var sealedRoot = source + "-envelope";
            Directory.CreateDirectory(sealedRoot);
            var payload = Path.Combine(sealedRoot, "payload.7z");
            await _codec.PackAsync(source, payload, encrypted, token);
            await WriteAsync(Path.Combine(sealedRoot, "envelope.json"), new NativeEnvelope(1, encrypted), token);
            var verify = source + "-verify";
            try
            {
                await _codec.UnpackAsync(payload, verify, encrypted, token);
                var logical = await HostArtifactReadService.ComputeLogicalFactsAsync(verify, token);
                if (logical.Sha256 != fact.LogicalSha256 || logical.Size != fact.LogicalSize) throw new InvalidDataException("Envelope roundtrip mismatch.");
            }
            finally { DeleteDirectory(verify); }
            using (var input = File.OpenRead(payload))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token)).ToLowerInvariant();
                result.Add(handle, fact with { StorageSha256 = hash, StorageSize = input.Length });
            }
            DeleteDirectory(source); Directory.Move(sealedRoot, source);
        }
        return result;
    }

    public async Task<NativeArtifactReadLease> OpenAsync(ArtifactId root, CancellationToken token)
    {
        var held = await EnterAsync(token);
        try { return await OpenWithoutGateAsync(await Ledger.LoadAsync(token), root, held, token); }
        catch { held.Dispose(); throw; }
    }
    internal async Task<NativeArtifactReadLease> OpenWithoutGateAsync(ArtifactLedgerDocument ledger, ArtifactId root, IDisposable held, CancellationToken token)
    {
        var closure = ArtifactLedgerValidator.BuildClosure(ledger, [root]);
        var rows = ledger.Artifacts.ToDictionary(a => a.ArtifactId);
        var roots = new Dictionary<ArtifactContentHandle, string>(); var snapshots = new List<BackupArtifactSnapshot>();
        var temporary = Path.Combine(Root, ".read", Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var id in closure.TopologicallySorted)
            {
                var node = rows[id]; var directory = ArtifactPathRules.ResolveUnderRoot(Root, node.ContentRelativePath);
                var envelopePath = Path.Combine(directory, "envelope.json");
                var logicalDirectory = directory;
                if (File.Exists(envelopePath))
                {
                    var envelope = JsonSerializer.Deserialize<NativeEnvelope>(await File.ReadAllTextAsync(envelopePath, token)) ?? throw new InvalidDataException();
                    if (envelope.Version != 1) throw new InvalidDataException("Unknown envelope version.");
                    using (var blob = File.OpenRead(PayloadPath(node)))
                        if (blob.Length != node.StorageSize || !StringComparer.OrdinalIgnoreCase.Equals(
                            Convert.ToHexString(await SHA256.HashDataAsync(blob, token)), node.StorageSha256)) throw new InvalidDataException("Artifact ciphertext/storage hash mismatch.");
                    logicalDirectory = Path.Combine(temporary, id.Value.ToString("N"));
                    await _codec.UnpackAsync(PayloadPath(node), logicalDirectory, envelope.Encrypted, token);
                }
                var logical = await HostArtifactReadService.ComputeLogicalFactsAsync(logicalDirectory, token);
                if (logical.Size != node.LogicalSize || !StringComparer.OrdinalIgnoreCase.Equals(logical.Sha256, node.LogicalSha256))
                    throw new InvalidDataException("Artifact logical tree mismatch.");
                var handle = new ArtifactContentHandle(Guid.NewGuid().ToString("N")); roots.Add(handle, logicalDirectory);
                snapshots.Add(new(node.ArtifactId, node.Format, node.FormatVersion, node.RestoreStrategyId, handle,
                    node.LogicalSha256, node.LogicalSize, node.Completeness, node.CoreCaptureMode, node.ConfigId, node.FolderId, node.Dependencies));
            }
            return new(snapshots, new HostArtifactReadService(roots), held, temporary, closure.TopologicallySorted.Select(id => rows[id]).ToArray());
        }
        catch { DeleteDirectory(temporary); throw; }
    }

    public async Task<IReadOnlyList<ArtifactId>> CollectAsync(IEnumerable<ArtifactId> roots, CancellationToken token)
    {
        using var held = await EnterAsync(token);
        var ledger = await Ledger.LoadAsync(token);
        var ids = ledger.Artifacts.Select(a => a.ArtifactId).ToHashSet();
        var pending = Pending(); // Corrupt intent must fail closed before GC.
        var transfers = Path.Combine(Root, "transfers");
        var transferRoots = Directory.Exists(transfers) ? Directory.EnumerateFiles(transfers, "*.json").Select(path =>
        {
            var transfer = JsonSerializer.Deserialize<NativeArtifactTransfer>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid transfer intent.");
            if (transfer.Root.Value == Guid.Empty || transfer.Id != Path.GetFileNameWithoutExtension(path)) throw new InvalidDataException("Invalid transfer identity.");
            return transfer;
        }).Where(t => !t.Complete).Select(t => t.Root).ToArray() : [];
        return await Ledger.GarbageCollectUnreachableAsync(roots.Concat(pending.Select(p => p.Root)).Concat(transferRoots).Where(ids.Contains), cancellationToken: token);
    }
    public static async Task WriteAsync<T>(string path, T value, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await JsonSerializer.SerializeAsync(output, value, cancellationToken: token); output.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static void DeleteDirectory(string path)
    { if (Directory.Exists(path)) Directory.Delete(path, true); }
    private static string RequireId(string id) => Guid.TryParseExact(id, "N", out _) ? id : throw new InvalidDataException("Invalid intent identity.");
    internal sealed class ActionLease(Action release) : IDisposable
    { private Action? _release = release; public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke(); }
}

public sealed class NativeArtifactReadLease(IReadOnlyList<BackupArtifactSnapshot> artifacts, IArtifactReadService read, IDisposable held, string temporary,
    IReadOnlyList<ArtifactLedgerEntry> nodes) : IAsyncDisposable
{
    public IReadOnlyList<ArtifactLedgerEntry> Nodes { get; } = nodes;
    public IReadOnlyList<BackupArtifactSnapshot> Artifacts { get; } = artifacts;
    public IArtifactReadService Read { get; } = read;
    public ValueTask DisposeAsync()
    { try { NativeArtifactStore.DeleteDirectory(temporary); } finally { held.Dispose(); } return ValueTask.CompletedTask; }
}

public sealed class PreparedNativeArtifact : IAsyncDisposable
{
    private readonly NativeArtifactStore _store;
    private readonly ArtifactTransformStagingArea _staging;
    private IReadOnlyDictionary<ArtifactStagingHandle, StagedArtifactFacts> _facts;
    private readonly PluginCapabilityLease<IBackupArtifactTransformerCapability> _lease;
    private readonly IDisposable _held;
    private readonly ArtifactLedgerDocument _before;
    private readonly bool _encrypted;
    public ArtifactLedgerDocument Ledger { get; private set; }
    public NativeArtifactPending Intent { get; private set; }
    public IReadOnlyList<PluginDiagnostic> Diagnostics { get; }
    internal PreparedNativeArtifact(NativeArtifactStore store, PluginRuntimeManager runtime, ConfigSnapshot config,
        FolderSnapshot folder, string version, ArtifactLedgerDocument before, ArtifactLedgerDocument after,
        ArtifactTransformStagingArea staging, IReadOnlyDictionary<ArtifactStagingHandle, StagedArtifactFacts> facts,
        NativeArtifactPending intent, PluginCapabilityLease<IBackupArtifactTransformerCapability> lease, IDisposable held,
        IReadOnlyList<PluginDiagnostic> diagnostics, bool encrypted)
    { _store = store; _before = before; Ledger = after; _staging = staging; _facts = facts; Intent = intent;
        _lease = lease; _held = held; Diagnostics = diagnostics; _encrypted = encrypted; }
    public async Task SealAndCommitGraphAsync(CancellationToken token)
    {
        _lease.Context.OperationCancellation.ThrowIfCancellationRequested();
        _facts = await _store.SealEnvelopesAsync(_staging, _facts, _encrypted, token);
        var byId = _facts.Values.ToDictionary(f => f.ArtifactId);
        Ledger = Ledger with { Artifacts = Ledger.Artifacts.Select(a => byId.TryGetValue(a.ArtifactId, out var f)
            ? a with { StorageSha256 = f.StorageSha256, StorageSize = f.StorageSize } : a).ToArray() };
        await _store.SavePendingAsync(Intent = Intent with { Phase = "PayloadsSealed" }, token);
        await _store.Ledger.CommitAsync(_before, Ledger, Intent.Id, _staging, _facts, token);
        await _store.SavePendingAsync(Intent = Intent with { Phase = "ArtifactGraphCommitted" }, token);
    }
    public Task<NativeArtifactReadLease> OpenAsync(CancellationToken token)
        => _store.OpenWithoutGateAsync(Ledger, Intent.Root, new NativeArtifactStore.ActionLease(() => { }), token);
    public async ValueTask DisposeAsync()
    { try { await _staging.DisposeAsync(); } finally { _lease.Dispose(); _held.Dispose(); } }
}

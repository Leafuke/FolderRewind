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

/// <summary>Host codec opt-in for independent envelopes; implementations bound their worker resources.</summary>
public interface IConcurrentArtifactEnvelopeCodec : IArtifactEnvelopeCodec
{
    int MaximumConcurrency { get; }
}

public sealed record NativeEnvelope(int Version, bool Encrypted);
public sealed record NativeArtifactPending(string Id, string VersionId, string RepresentationId, ArtifactId Root,
    string Phase, string? HistoryPackId = null, string? HistoryTransactionId = null)
{
    public string? CorePayloadPath { get; init; }
    public string? CoreRepresentationId { get; init; }
    public bool TemporaryCoreInput { get; init; }
    public string? CorePayloadSha256 { get; init; }
}
public sealed record NativeArtifactTransfer(string Id, ArtifactId Root, string RepresentationId, string ReplicaId, bool Complete);
/// <summary>Host-owned, operation-scoped input; never persisted as a Representation.</summary>
public sealed record NativeArtifactPrimaryInput(ArtifactContentHandle Content, HostArtifactReadService Read);

/// <summary>Config-isolated payload storage. The artifact DAG stays in the existing ledger.</summary>
public sealed partial class NativeArtifactStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Active = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate;
    private readonly IArtifactEnvelopeCodec _codec;
    private readonly Action<string>? _observe;
    private int EnvelopeConcurrency => _codec is IConcurrentArtifactEnvelopeCodec concurrent
        ? Math.Clamp(concurrent.MaximumConcurrency, 1, 4) : 1;
    public string Root { get; }
    public FileArtifactLedgerStore Ledger { get; }
    public static string ForConfig(string destination, string configId)
        => Path.Combine(Path.GetFullPath(destination), ".folderrewind", "native-artifacts-v2",
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
        ReleasePendingOperation(id);
    }
    public void ReleasePendingOperation(string id) => Active.TryRemove(Path.Combine(Root, RequireId(id)), out _);

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
            if (committed && intent.TemporaryCoreInput) await CleanupPrimaryAsync(intent, token);
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
        ArtifactTransformPolicy policy, bool encrypted, CancellationToken token, string? corePayloadPath = null, string? coreRepresentationId = null,
        ArtifactIncrementalInput? incrementalInput = null, Func<bool, byte[]>? hostCaptureEvidence = null,
        NativeArtifactPrimaryInput? primaryInput = null, PluginOperationLease? operation = null)
    {
        var held = await EnterAsync(token);
        PluginOperationLease? lease = null;
        ArtifactTransformStagingArea? staging = null;
        NativeArtifactReadLease? baselineRead = null;
        try
        {
            lease = operation ?? runtime.TryAcquireOperation(policy.TransformerId.PluginId, token)
                ?? throw new InvalidOperationException("Transformer session unavailable.");
            var manifest = lease.Manifest ?? throw new InvalidOperationException("Active declared transformer required.");
            var declaration = manifest.ArtifactTransformers.SingleOrDefault(d => d.TransformerId == policy.TransformerId)
                ?? throw new InvalidOperationException("Transformer is undeclared.");
            var mode = incrementalInput?.Kind == ArtifactCaptureInputKind.ChangesOnly ? CoreCaptureMode.Smart : CoreCaptureMode.Full;
            var completeness = mode == CoreCaptureMode.Smart ? ArtifactCompleteness.Partial : ArtifactCompleteness.Complete;
            if (incrementalInput is not null && !declaration.SupportsIncrementalInput)
                throw new InvalidOperationException("Transformer does not declare incremental input support.");
            if (!declaration.CompatibleConfigKinds.Contains(config.Kind) || !declaration.CompatibleCoreModes.Contains(mode)
                || !declaration.CompatibleCompleteness.Contains(completeness)
                || !declaration.SupportedFailureBehaviors.Contains(policy.FailureBehavior)) throw new InvalidOperationException("Transformer policy is incompatible.");
            var transformer = lease.Find<IBackupArtifactTransformerCapability>(c => c.TransformerId == policy.TransformerId)
                ?? throw new InvalidOperationException("Transformer unavailable.");
            var current = await Ledger.LoadAsync(token);
            if (incrementalInput?.BaseRootArtifactId is { } baseRoot)
            {
                baselineRead = incrementalInput.Checkpoint
                    ? await OpenWithoutGateAsync(current, baseRoot, new ActionLease(() => { }), token)
                    : OpenLazyWithoutGate(current, baseRoot, new ActionLease(() => { }));
                if (baselineRead.Artifacts.Any(a => a.ConfigId != config.ConfigId || a.FolderId != folder.FolderId))
                    throw new InvalidDataException("Incremental baseline belongs to another source.");
            }
            if (mode == CoreCaptureMode.Smart && baselineRead is null)
                throw new InvalidDataException("Changes-only input requires an explicit readable baseline.");
            var transaction = Guid.NewGuid().ToString("N");
            staging = Ledger.CreateStagingArea(transaction, ArtifactTransformLimits.Default.MaximumFiles, ArtifactTransformLimits.Default.MaximumBytes);
            var handle = primaryInput?.Content ?? new ArtifactContentHandle(Guid.NewGuid().ToString("N"));
            var primaryRead = primaryInput?.Read ?? new HostArtifactReadService(new Dictionary<ArtifactContentHandle, string> { [handle] = primaryDirectory });
            var primaryFiles = await primaryRead.ListFilesAsync(handle, token);
            if (incrementalInput is null && primaryFiles.Count == 0) throw new InvalidDataException("Full primary cannot be empty.");
            var primaryFacts = HostArtifactReadService.ComputeInventoryFacts(primaryFiles);
            var primary = new BackupArtifactSnapshot(new(Guid.NewGuid()), new(new OwnerId("folderrewind.core"), "archive-set"), 1,
                new(new PluginId("folderrewind.core"), "archive-materializer"), handle, primaryFacts.Sha256, primaryFacts.Size,
                completeness, mode, config.ConfigId, folder.FolderId, []);
            var request = new ArtifactTransformRequest(config, folder, versionId, current.Revision, primary,
                baselineRead?.Artifacts.Where(a => a.ArtifactId == incrementalInput!.BaseRootArtifactId).ToArray() ?? [], policy.Parameters,
                new CombinedArtifactReadService(handle, primaryRead, baselineRead?.Read), staging, NullPluginOperationProgress.Instance)
            { IncrementalInput = incrementalInput, ReadableArtifacts = baselineRead?.Artifacts ?? [] };
            ArtifactTransformResult result;
            using (ArtifactDiagnostics.Source.StartActivity("transform"))
                result = await transformer.TransformAsync(request, lease.Context);
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
                var materializer = lease.Find<IRestoreMaterializerCapability>(c => c.RestoreStrategyId == node.RestoreStrategyId);
                if (materializer is null) throw new InvalidDataException("Declared materializer is unavailable.");
            }
            if (hostCaptureEvidence is not null)
            {
                var evidence = hostCaptureEvidence(result.RequiresFullVerification);
                if (incrementalInput is null || evidence.Length > 64 * 1024 * 1024)
                    throw new InvalidDataException("Invalid Host capture evidence.");
                var rootStaging = result.Patch.AddedArtifacts.Single(n => n.ArtifactId == result.Patch.ResultRootArtifactId).Staging;
                await using var evidenceStream = await staging.OpenWriteAsync(rootStaging, "_host/capture-evidence.json", token);
                await evidenceStream.WriteAsync(evidence, token);
            }
            IReadOnlyDictionary<ArtifactStagingHandle, StagedArtifactFacts> facts;
            using (ArtifactDiagnostics.Source.StartActivity("staging.seal"))
                facts = await staging.SealAsync("artifacts", token, result.Patch.AddedArtifacts.Select(n => n.Staging).ToHashSet());
            var candidate = ArtifactGraphPatchApplier.Apply(current, result.Patch,
                new(policy.TransformerId.PluginId, config.ConfigId, folder.FolderId, baselineRead?.Artifacts.Select(a => a.ArtifactId).ToHashSet() ?? [], transaction,
                    new(Guid.NewGuid().ToString("N"))), facts);
            var closure = ArtifactLedgerValidator.BuildClosure(candidate, [result.Patch.ResultRootArtifactId]);
            if (result.Patch.AddedArtifacts.Any(node => !closure.Reachable.Contains(node.ArtifactId)))
                throw new InvalidDataException("Transform patch contains an unreachable new Artifact.");
            var resultNode = candidate.Artifacts.Single(a => a.ArtifactId == result.Patch.ResultRootArtifactId);
            if (resultNode.Completeness != ArtifactCompleteness.Complete || resultNode.CoreCaptureMode != mode)
                throw new InvalidDataException("Native Full root must preserve complete Full capture semantics.");
            if (incrementalInput is null && closure.Reachable.Count != result.Patch.AddedArtifacts.Count)
                throw new InvalidDataException("Native Full artifacts must be a self-contained new closure.");
            if (result.RequiresFullVerification && !closure.Reachable.All(id => result.Patch.AddedArtifacts.Any(a => a.ArtifactId == id)))
                throw new InvalidDataException("Compaction must produce a self-contained new closure.");
            var pending = new NativeArtifactPending(transaction, versionId, representationId, result.Patch.ResultRootArtifactId, "Prepared")
                { CorePayloadPath = corePayloadPath, CoreRepresentationId = coreRepresentationId, TemporaryCoreInput = incrementalInput is not null,
                    CorePayloadSha256 = incrementalInput is not null && corePayloadPath is not null
                        ? Convert.ToHexString(await HashFileAsync(corePayloadPath, token)) : null };
            await SavePendingAsync(pending, token);
            Active.TryAdd(Path.Combine(Root, transaction), 0);
            return new PreparedNativeArtifact(this, runtime, config, folder, versionId, current, candidate,
                staging, facts, pending, lease, held, result.Diagnostics, encrypted, baselineRead, result.RequiresFullVerification);
        }
        catch
        {
            if (staging is not null) await staging.DisposeAsync();
            if (baselineRead is not null) await baselineRead.DisposeAsync();
            lease?.Dispose(); held.Dispose(); throw;
        }
    }

    internal async Task<IReadOnlyDictionary<ArtifactStagingHandle, StagedArtifactFacts>> SealEnvelopesAsync(
        ArtifactTransformStagingArea staging, IReadOnlyDictionary<ArtifactStagingHandle, StagedArtifactFacts> facts,
        bool encrypted, CancellationToken token, IDictionary<ArtifactId, string>? verifiedDirectories = null)
    {
        using var phase = ArtifactDiagnostics.Source.StartActivity("envelopes.seal.wall");
        var result = new ConcurrentDictionary<ArtifactStagingHandle, StagedArtifactFacts>();
        await Parallel.ForEachAsync(facts, new ParallelOptions { MaxDegreeOfParallelism = EnvelopeConcurrency, CancellationToken = token }, async (pair, workerToken) =>
        {
            var (handle, fact) = pair;
            var token = workerToken;
            using var stage = ArtifactDiagnostics.Source.StartActivity("envelope.seal");
            stage?.SetTag("logical.bytes", fact.LogicalSize);
            var source = staging.GetAllocationPath(handle); var sealedRoot = source + "-envelope";
            Directory.CreateDirectory(sealedRoot);
            var payload = Path.Combine(sealedRoot, "payload.7z");
            using (ArtifactDiagnostics.Source.StartActivity("envelope.pack"))
                await _codec.PackAsync(source, payload, encrypted, token);
            await WriteAsync(Path.Combine(sealedRoot, "envelope.json"), new NativeEnvelope(1, encrypted), token);
            var verify = source + "-verify";
            var verified = false;
            try
            {
                using (ArtifactDiagnostics.Source.StartActivity("envelope.roundtrip.unpack"))
                    await _codec.UnpackAsync(payload, verify, encrypted, token);
                var logical = await HostArtifactReadService.ComputeLogicalFactsAsync(verify, token);
                if (logical.Sha256 != fact.LogicalSha256 || logical.Size != fact.LogicalSize) throw new InvalidDataException("Envelope roundtrip mismatch.");
                verified = true;
                if (verifiedDirectories is not null)
                    lock (verifiedDirectories) verifiedDirectories.Add(fact.ArtifactId, verify);
            }
            finally { if (!verified || verifiedDirectories is null) DeleteDirectory(verify); }
            using (var input = File.OpenRead(payload))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token)).ToLowerInvariant();
                if (!result.TryAdd(handle, fact with { StorageSha256 = hash, StorageSize = input.Length }))
                    throw new InvalidDataException("Duplicate sealed Artifact handle.");
            }
            DeleteDirectory(source); Directory.Move(sealedRoot, source);
        });
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
        using var phase = ArtifactDiagnostics.Source.StartActivity("closure.open.wall");
        var closure = ArtifactLedgerValidator.BuildClosure(ledger, [root]);
        var rows = ledger.Artifacts.ToDictionary(a => a.ArtifactId);
        var roots = new ConcurrentDictionary<ArtifactContentHandle, string>();
        var snapshots = new ConcurrentDictionary<ArtifactId, BackupArtifactSnapshot>();
        var temporary = Path.Combine(Root, ".read", Guid.NewGuid().ToString("N"));
        try
        {
            await Parallel.ForEachAsync(closure.TopologicallySorted,
                new ParallelOptions { MaxDegreeOfParallelism = EnvelopeConcurrency, CancellationToken = token }, async (id, workerToken) =>
            {
                var token = workerToken;
                using var stage = ArtifactDiagnostics.Source.StartActivity("artifact.open.verify");
                var node = rows[id]; var directory = ArtifactPathRules.ResolveUnderRoot(Root, node.ContentRelativePath);
                stage?.SetTag("logical.bytes", node.LogicalSize);
                stage?.SetTag("storage.bytes", node.StorageSize);
                var envelopePath = Path.Combine(directory, "envelope.json");
                var logicalDirectory = directory;
                if (File.Exists(envelopePath))
                {
                    var envelope = JsonSerializer.Deserialize<NativeEnvelope>(await File.ReadAllTextAsync(envelopePath, token)) ?? throw new InvalidDataException();
                    if (envelope.Version != 1) throw new InvalidDataException("Unknown envelope version.");
                    using (var blob = File.OpenRead(PayloadPath(node)))
                        if (blob.Length != node.StorageSize || !StringComparer.OrdinalIgnoreCase.Equals(
                            Convert.ToHexString(await SHA256.HashDataAsync(blob, token)), node.StorageSha256))
                        {
                            await RecordCorruptionAsync(node, token);
                            throw new InvalidDataException("Artifact ciphertext/storage hash mismatch.");
                        }
                    logicalDirectory = Path.Combine(temporary, id.Value.ToString("N"));
                    using (ArtifactDiagnostics.Source.StartActivity("artifact.unpack"))
                        await _codec.UnpackAsync(PayloadPath(node), logicalDirectory, envelope.Encrypted, token);
                }
                var logical = await HostArtifactReadService.ComputeLogicalFactsAsync(logicalDirectory, token);
                if (logical.Size != node.LogicalSize || !StringComparer.OrdinalIgnoreCase.Equals(logical.Sha256, node.LogicalSha256))
                {
                    await RecordCorruptionAsync(node, token);
                    throw new InvalidDataException("Artifact logical tree mismatch.");
                }
                // Only a fresh full verification clears an observed failure (for example after replica repair).
                if (File.Exists(CorruptionPath(node))) File.Delete(CorruptionPath(node));
                var handle = new ArtifactContentHandle(Guid.NewGuid().ToString("N")); roots.TryAdd(handle, logicalDirectory);
                snapshots.TryAdd(id, new(node.ArtifactId, node.Format, node.FormatVersion, node.RestoreStrategyId, handle,
                    node.LogicalSha256, node.LogicalSize, node.Completeness, node.CoreCaptureMode, node.ConfigId, node.FolderId, node.Dependencies));
            });
            return new(closure.TopologicallySorted.Select(id => snapshots[id]).ToArray(), new HostArtifactReadService(roots), held, temporary, closure.TopologicallySorted.Select(id => rows[id]).ToArray());
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
    private static async Task<byte[]> HashFileAsync(string path, CancellationToken token)
    { await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true); return await SHA256.HashDataAsync(input, token); }

    public async Task CleanupPrimaryAsync(NativeArtifactPending intent, CancellationToken token)
    {
        if (!intent.TemporaryCoreInput || intent.CorePayloadPath is not { } path || !File.Exists(path)) return;
        // The owner is the configuration's backup destination, never an arbitrary journal path.
        var destination = Directory.GetParent(Root)?.Parent?.Parent?.FullName ?? throw new InvalidDataException("Invalid Artifact store layout.");
        var relative = Path.GetRelativePath(destination, Path.GetFullPath(path));
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Any(p => p == "..")
            || path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || intent.CorePayloadSha256 is null)
            throw new InvalidDataException("Temporary primary escaped its capture owner.");
        HostArtifactReadService.EnsureNoReparsePoints(destination, path);
        if (!StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(await HashFileAsync(path, token)), intent.CorePayloadSha256))
            throw new InvalidDataException("Temporary primary changed; retaining it for recovery.");
        _observe?.Invoke("PrimaryCleanup:before");
        File.Delete(path);
        _observe?.Invoke("PrimaryCleanup:after");
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
    public async ValueTask DisposeAsync()
    { try { if (Read is IAsyncDisposable disposable) await disposable.DisposeAsync(); if (temporary.Length != 0) NativeArtifactStore.DeleteDirectory(temporary); } finally { held.Dispose(); } }
}

public sealed class PreparedNativeArtifact : IAsyncDisposable
{
    private readonly NativeArtifactStore _store;
    private readonly ArtifactTransformStagingArea _staging;
    private IReadOnlyDictionary<ArtifactStagingHandle, StagedArtifactFacts> _facts;
    private readonly PluginOperationLease _lease;
    private readonly IDisposable _held;
    private readonly ArtifactLedgerDocument _before;
    private readonly bool _encrypted;
    private readonly NativeArtifactReadLease? _baselineRead;
    private readonly Dictionary<ArtifactId, string> _verifiedDirectories = [];
    public ArtifactLedgerDocument Ledger { get; private set; }
    public NativeArtifactPending Intent { get; private set; }
    public IReadOnlyList<PluginDiagnostic> Diagnostics { get; }
    public NativeArtifactReadLease? BaselineRead => _baselineRead;
    public PluginOperationLease Operation => _lease;
    public bool RequiresFullVerification { get; }
    internal PreparedNativeArtifact(NativeArtifactStore store, PluginRuntimeManager runtime, ConfigSnapshot config,
        FolderSnapshot folder, string version, ArtifactLedgerDocument before, ArtifactLedgerDocument after,
        ArtifactTransformStagingArea staging, IReadOnlyDictionary<ArtifactStagingHandle, StagedArtifactFacts> facts,
        NativeArtifactPending intent, PluginOperationLease lease, IDisposable held,
        IReadOnlyList<PluginDiagnostic> diagnostics, bool encrypted, NativeArtifactReadLease? baselineRead = null, bool requiresFullVerification = false)
    { _store = store; _before = before; Ledger = after; _staging = staging; _facts = facts; Intent = intent;
        _lease = lease; _held = held; Diagnostics = diagnostics; _encrypted = encrypted; _baselineRead = baselineRead;
        RequiresFullVerification = requiresFullVerification; }
    public async Task SealAndCommitGraphAsync(CancellationToken token)
    {
        _lease.Context.OperationCancellation.ThrowIfCancellationRequested();
        _facts = await _store.SealEnvelopesAsync(_staging, _facts, _encrypted, token, _verifiedDirectories);
        var byId = _facts.Values.ToDictionary(f => f.ArtifactId);
        Ledger = Ledger with { Artifacts = Ledger.Artifacts.Select(a => byId.TryGetValue(a.ArtifactId, out var f)
            ? a with { StorageSha256 = f.StorageSha256, StorageSize = f.StorageSize } : a).ToArray() };
        await _store.SavePendingAsync(Intent = Intent with { Phase = "PayloadsSealed" }, token);
        await _store.Ledger.CommitAsync(_before, Ledger, Intent.Id, _staging, _facts, token);
        await _store.SavePendingAsync(Intent = Intent with { Phase = "ArtifactGraphCommitted" }, token);
    }
    public Task<NativeArtifactReadLease> OpenAsync(CancellationToken token)
        => _store.OpenWithoutGateAsync(Ledger, Intent.Root, new NativeArtifactStore.ActionLease(() => { }), token);
    public NativeArtifactReadLease OpenLazy()
        => _store.OpenLazyWithoutGate(Ledger, Intent.Root, new NativeArtifactStore.ActionLease(() => { }), _verifiedDirectories, _baselineRead);
    public async ValueTask DisposeAsync()
    { try { if (_baselineRead is not null) await _baselineRead.DisposeAsync(); await _staging.DisposeAsync(); } finally { _lease.Dispose(); _held.Dispose(); } }
}

using System.Collections.Concurrent;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Operations;

namespace FolderRewind.Plugin.Runtime.Artifacts;

public sealed record ArtifactTransformLimits(int MaximumFiles, long MaximumBytes)
{
    public static ArtifactTransformLimits Default { get; } = new(100_000, 16L * 1024 * 1024 * 1024);
}

public sealed record ArtifactTransformRunResult(
    OperationOutcome Outcome,
    ArtifactLedgerDocument Ledger,
    IReadOnlyList<PluginDiagnostic> Diagnostics,
    bool GraphCommitted);

public sealed class ArtifactTransformCoordinator
{
    private readonly PluginRuntimeManager _runtime;
    private readonly FileArtifactLedgerStore _store;

    public ArtifactTransformCoordinator(PluginRuntimeManager runtime, FileArtifactLedgerStore store)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask<ArtifactTransformRunResult> TransformAsync(
        PluginId pluginId,
        ConfigSnapshot config,
        FolderSnapshot folder,
        string historyItemId,
        ArtifactTransformPolicy policy,
        IReadOnlyList<string> compatibleHistoryItemIds,
        ArtifactTransformLimits? limits = null,
        IPluginOperationProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(compatibleHistoryItemIds);
        if (policy.TransformerId.PluginId != pluginId)
        {
            throw new InvalidOperationException("Artifact Transform Policy does not select the requested plugin.");
        }

        using var lease = _runtime.TryAcquire<IBackupArtifactTransformerCapability>(pluginId, cancellationToken)
            ?? throw new InvalidOperationException($"Plugin '{pluginId}' has no active Artifact Transformer.");
        if (lease.Capability.TransformerId != policy.TransformerId)
        {
            throw new InvalidOperationException("Runtime Artifact Transformer does not match the selected policy.");
        }

        var current = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var roots = current.HistoryRoots
            .Where(root => root.FolderId == folder.FolderId && StringComparer.Ordinal.Equals(root.ConfigId, config.ConfigId))
            .ToDictionary(root => root.HistoryItemId, StringComparer.Ordinal);
        if (!roots.TryGetValue(historyItemId, out var primaryRoot))
        {
            throw new KeyNotFoundException("Primary History root is missing from the Artifact Ledger.");
        }
        var compatibleRoots = compatibleHistoryItemIds
            .Distinct(StringComparer.Ordinal)
            .Select(id => roots.TryGetValue(id, out var root)
                ? root
                : throw new InvalidOperationException("Compatible History root is outside the transform scope."))
            .ToArray();
        var readableIds = compatibleRoots.Select(root => root.RootArtifactId)
            .Append(primaryRoot.RootArtifactId)
            .ToHashSet();
        await _store.VerifyArtifactsAsync(current, readableIds, cancellationToken).ConfigureAwait(false);
        var readSession = _store.CreateReadSession(current, readableIds);
        var transactionId = "transform-" + Guid.NewGuid().ToString("N");
        var bounded = limits ?? ArtifactTransformLimits.Default;
        await using var staging = _store.CreateStagingArea(transactionId, bounded.MaximumFiles, bounded.MaximumBytes);
        var request = new ArtifactTransformRequest(
            config,
            folder,
            historyItemId,
            current.Revision,
            readSession.Snapshots[primaryRoot.RootArtifactId],
            compatibleRoots.Select(root => readSession.Snapshots[root.RootArtifactId]).ToArray(),
            CloneJson(policy.Parameters),
            readSession.ReadService,
            staging,
            progress ?? NullPluginOperationProgress.Instance);

        ArtifactTransformResult result;
        try
        {
            result = await lease.Capability.TransformAsync(request, lease.Context).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Artifact Transformer returned null.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ArtifactTransformRunResult(OperationOutcome.Canceled, current, Array.Empty<PluginDiagnostic>(), false);
        }
        catch (Exception ex) when (policy.FailureBehavior == ArtifactTransformFailureBehavior.KeepPrimaryWithWarnings)
        {
            return new ArtifactTransformRunResult(
                OperationOutcome.SuccessWithWarnings,
                current,
                [RuntimeDiagnostic.Warning("artifact.transform_failed_primary_kept", pluginId, ex.Message)],
                false);
        }

        var diagnostics = CloneDiagnostics(result.Diagnostics);
        if (result.Outcome is not OperationOutcome.Success and not OperationOutcome.SuccessWithWarnings)
        {
            if (policy.FailureBehavior == ArtifactTransformFailureBehavior.KeepPrimaryWithWarnings)
            {
                return new ArtifactTransformRunResult(
                    OperationOutcome.SuccessWithWarnings,
                    current,
                    diagnostics.Append(RuntimeDiagnostic.Warning(
                        "artifact.transform_primary_kept",
                        pluginId,
                        "Artifact transform did not complete; the Core primary Artifact was retained.")).ToArray(),
                    false);
            }
            return new ArtifactTransformRunResult(result.Outcome, current, diagnostics, false);
        }
        if (result.Patch is null) throw new InvalidOperationException("Successful Artifact Transform requires a graph patch.");

        var facts = await staging.SealAsync("artifacts", cancellationToken).ConfigureAwait(false);
        var replaceable = compatibleRoots.Append(primaryRoot)
            .ToDictionary(root => root.HistoryItemId, root => root.RootArtifactId, StringComparer.Ordinal);
        var committedRevision = new ArtifactGraphRevision(Guid.NewGuid().ToString("N"));
        var candidate = ArtifactGraphPatchApplier.Apply(
            current,
            result.Patch,
            new ArtifactPatchScope(
                pluginId,
                config.ConfigId,
                folder.FolderId,
                replaceable,
                readableIds,
                transactionId,
                committedRevision),
            facts);
        await _store.CommitAsync(current, candidate, transactionId, staging, facts, cancellationToken).ConfigureAwait(false);
        return new ArtifactTransformRunResult(result.Outcome, candidate, diagnostics, true);
    }

    private static IReadOnlyDictionary<string, JsonElement> CloneJson(IReadOnlyDictionary<string, JsonElement> values)
        => values.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);

    private static IReadOnlyList<PluginDiagnostic> CloneDiagnostics(IReadOnlyList<PluginDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return diagnostics.Select(diagnostic => diagnostic with
        {
            Arguments = diagnostic.Arguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        }).ToArray();
    }
}

public sealed record BackupCompletionRunResult(
    OperationOutcome Outcome,
    IReadOnlyList<PluginDiagnostic> Diagnostics);

public sealed class BackupCompletionObserverCoordinator
{
    private readonly PluginRuntimeManager _runtime;
    private readonly ConcurrentDictionary<string, byte> _deliveries = new(StringComparer.Ordinal);

    public BackupCompletionObserverCoordinator(PluginRuntimeManager runtime)
        => _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public async ValueTask<BackupCompletionRunResult> ObserveAsync(
        BackupCompletionSnapshot snapshot,
        IReadOnlyList<PluginId> observerPluginIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(observerPluginIds);
        var diagnostics = snapshot.Diagnostics.ToList();
        var outcome = snapshot.CoreOutcome;
        foreach (var pluginId in observerPluginIds.Distinct())
        {
            var deliveryId = $"{snapshot.BackupRunId}:{pluginId.Value}";
            if (!_deliveries.TryAdd(deliveryId, 0)) continue;
            using var lease = _runtime.TryAcquire<IBackupCompletionObserverCapability>(pluginId, cancellationToken);
            if (lease is null) continue;
            var context = lease.Context with
            {
                HostServices = new ObserverHostServices(lease.Context.HostServices)
            };
            try
            {
                var result = await lease.Capability.ObserveAsync(snapshot, context).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Backup Completion Observer returned null.");
                diagnostics.AddRange(result.Diagnostics);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                diagnostics.Add(RuntimeDiagnostic.Warning("backup.observer_failed", pluginId, ex.Message));
            }
        }
        if (outcome == OperationOutcome.Success && diagnostics.Any(value => value.Severity != DiagnosticSeverity.Information))
        {
            outcome = OperationOutcome.SuccessWithWarnings;
        }
        return new BackupCompletionRunResult(outcome, diagnostics);
    }

    public static PluginDiagnostic ObserverIncomplete(PluginId pluginId)
        => RuntimeDiagnostic.Warning(
            "backup.observer_incomplete",
            pluginId,
            "The process ended before observer finalization; external side effects were not replayed.");

    private sealed class ObserverHostServices(IPluginHostServices inner) : IPluginHostServices
    {
        public IReadOnlyConfigQueryService Configs => inner.Configs;
        public IBackupRequestService Backups { get; } = new BlockedBackupRequests();
        public IRestoreRequestService Restores { get; } = new BlockedRestoreRequests();
        public IHistoryQueryService History => inner.History;
        public IPluginNotificationService Notifications => inner.Notifications;
        public IKnotLinkHostService KnotLink => inner.KnotLink;
        public IPluginDataStore DataStore => inner.DataStore;
        public IPluginTemporaryStorage TemporaryStorage => inner.TemporaryStorage;
        public IPluginLogger Logger => inner.Logger;
    }

    private sealed class BlockedBackupRequests : IBackupRequestService
    {
        public ValueTask<OperationOutcome> RequestAsync(string configId, Guid? folderId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Completion Observers cannot request backups.");
    }

    private sealed class BlockedRestoreRequests : IRestoreRequestService
    {
        public ValueTask<OperationOutcome> RequestAsync(string configId, Guid folderId, string historyItemId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Completion Observers cannot request restores.");
    }
}

public sealed class RestoreMaterializationLease : IAsyncDisposable
{
    private RestoreMaterializationWorkspace? _workspace;

    internal RestoreMaterializationLease(
        OperationOutcome outcome,
        IReadOnlyList<PluginDiagnostic> diagnostics,
        RestoreMaterializationWorkspace? workspace,
        IReadOnlyList<ArtifactFileEntry> files)
    {
        Outcome = outcome;
        Diagnostics = diagnostics;
        _workspace = workspace;
        Files = files;
    }

    public OperationOutcome Outcome { get; }
    public IReadOnlyList<PluginDiagnostic> Diagnostics { get; }
    public string? WorkspacePath => _workspace?.RootPath;
    public IReadOnlyList<ArtifactFileEntry> Files { get; }

    public async ValueTask DisposeAsync()
    {
        var workspace = Interlocked.Exchange(ref _workspace, null);
        if (workspace is not null) await workspace.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class RestoreMaterializationCoordinator
{
    private readonly PluginRuntimeManager _runtime;
    private readonly FileArtifactLedgerStore _store;

    public RestoreMaterializationCoordinator(PluginRuntimeManager runtime, FileArtifactLedgerStore store)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask<RestoreMaterializationLease> MaterializeAsync(
        ConfigSnapshot config,
        FolderSnapshot folder,
        string historyItemId,
        RestoreMode requestedMode,
        RestoreMode effectiveMode,
        string workspaceRoot,
        ArtifactTransformLimits? limits = null,
        IPluginOperationProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ledger = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var history = ledger.HistoryRoots.SingleOrDefault(root => StringComparer.Ordinal.Equals(root.HistoryItemId, historyItemId))
            ?? throw new InvalidOperationException("Restore History root is missing.");
        if (!StringComparer.Ordinal.Equals(history.ConfigId, config.ConfigId) || history.FolderId != folder.FolderId)
        {
            throw new InvalidOperationException("Restore History root belongs to a different Config or Folder.");
        }
        var byId = ledger.Artifacts.ToDictionary(artifact => artifact.ArtifactId);
        var root = byId[history.RootArtifactId];
        if (root.Completeness == ArtifactCompleteness.Partial && effectiveMode != RestoreMode.Overwrite)
        {
            throw new InvalidOperationException("Partial Artifacts require effective Overwrite restore mode.");
        }

        var orderedIds = TopologicalClosure(root.ArtifactId, byId);
        await _store.VerifyArtifactsAsync(ledger, orderedIds, cancellationToken).ConfigureAwait(false);
        var readSession = _store.CreateReadSession(ledger, orderedIds);
        using var lease = _runtime.TryAcquire<IRestoreMaterializerCapability>(root.RestoreStrategyId.PluginId, cancellationToken)
            ?? throw new InvalidOperationException("Restore Materializer owner is missing, disabled, or failed.");
        if (lease.Capability.RestoreStrategyId != root.RestoreStrategyId)
        {
            throw new InvalidOperationException("Runtime Restore Materializer does not match the Artifact strategy.");
        }

        var bounded = limits ?? ArtifactTransformLimits.Default;
        var workspace = await RestoreMaterializationWorkspace.CreateAsync(
            workspaceRoot,
            bounded.MaximumFiles,
            bounded.MaximumBytes,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var request = new RestoreMaterializationRequest(
                config,
                folder,
                historyItemId,
                root.ArtifactId,
                orderedIds.Select(id => readSession.Snapshots[id]).ToArray(),
                requestedMode,
                effectiveMode,
                readSession.ReadService,
                workspace,
                progress ?? NullPluginOperationProgress.Instance);
            RestoreMaterializationResult result;
            try
            {
                result = await lease.Capability.MaterializeAsync(request, lease.Context).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Restore Materializer returned null.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await workspace.DisposeAsync().ConfigureAwait(false);
                return new RestoreMaterializationLease(OperationOutcome.Canceled, Array.Empty<PluginDiagnostic>(), null, Array.Empty<ArtifactFileEntry>());
            }
            if (result.Outcome is not OperationOutcome.Success and not OperationOutcome.SuccessWithWarnings)
            {
                await workspace.DisposeAsync().ConfigureAwait(false);
                return new RestoreMaterializationLease(result.Outcome, result.Diagnostics.ToArray(), null, Array.Empty<ArtifactFileEntry>());
            }
            var files = await workspace.SealAsync(cancellationToken).ConfigureAwait(false);
            return new RestoreMaterializationLease(result.Outcome, result.Diagnostics.ToArray(), workspace, files);
        }
        catch
        {
            await workspace.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static IReadOnlyList<ArtifactId> TopologicalClosure(
        ArtifactId root,
        IReadOnlyDictionary<ArtifactId, ArtifactLedgerEntry> artifacts)
    {
        var ordered = new List<ArtifactId>();
        var visited = new HashSet<ArtifactId>();
        Visit(root);
        return ordered;

        void Visit(ArtifactId id)
        {
            if (!visited.Add(id)) return;
            foreach (var dependency in artifacts[id].Dependencies) Visit(dependency);
            ordered.Add(id);
        }
    }
}

public sealed class NullPluginOperationProgress : IPluginOperationProgress
{
    public static NullPluginOperationProgress Instance { get; } = new();
    private NullPluginOperationProgress() { }
    public void Report(PluginProgress progress) { }
}

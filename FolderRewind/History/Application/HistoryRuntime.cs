using FolderRewind.History.Domain;
using FolderRewind.History.Capture;
using FolderRewind.History.Index;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

[Flags]
public enum HistoryRuntimeHealth
{
    Ready = 0,
    WorkspaceRecoveryRequired = 1,
    LocalReplicaCatalogRecoveryRequired = 2
}

public sealed class HistoryRuntime : IAsyncDisposable
{
    private readonly HistoryPackCodec _codec;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public HistoryRuntime(FileHistoryRepository repository, HistoryPackCodec? codec = null)
    {
        Repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _codec = codec ?? new HistoryPackCodec();
        Index = new HistoryIndex(
            Path.Combine(repository.Paths.IndexRoot, "history-index.db"),
            _codec);
        WorkspaceStore = new HistoryWorkspaceStore(
            repository.ConfigId,
            Path.Combine(repository.Paths.LocalStateRoot, "workspace.json"));
        LocalReplicaCatalogStore = new LocalReplicaCatalogStore(
            repository.ConfigId,
            Path.Combine(repository.Paths.LocalStateRoot, "replicas.json"));
        CaptureBaselines = new SourceCaptureBaselineCache(repository.Paths.LocalStateRoot);
        MergeSessions = new MergeSessionStore(repository.Paths.LocalStateRoot);
        MutationGate = new HistoryMutationGate(repository.ConfigId, () =>
        {
            HistoryRestoreTransactionJournalStore.RequireRecovered(repository.Paths.TransactionsRoot);
            repository.Journals.RequireRecovered();
            Retention.HistoryChainRewriteJournalStore.RequireRecovered(repository.Paths.LocalStateRoot);
        });
        ChangeFeed = new HistoryChangeFeed();
        Query = new HistoryQueryService(Index);
        Commit = new HistoryCommitCoordinator(this, _codec);
        Branches = new HistoryBranchService(this, _codec);
        Annotations = new HistoryAnnotationService(this, _codec);
        MaterializationPolicies = new MaterializationPolicyService(this, _codec);
    }

    public HistoryConfigId ConfigId => Repository.ConfigId;
    public FileHistoryRepository Repository { get; }
    public HistoryIndex Index { get; }
    public HistoryWorkspaceStore WorkspaceStore { get; }
    public LocalReplicaCatalogStore LocalReplicaCatalogStore { get; }
    public SourceCaptureBaselineCache CaptureBaselines { get; }
    public MergeSessionStore MergeSessions { get; }
    public HistoryMutationGate MutationGate { get; }
    public HistoryChangeFeed ChangeFeed { get; }
    public HistoryQueryService Query { get; }
    public HistoryCommitCoordinator Commit { get; }
    public HistoryBranchService Branches { get; }
    public HistoryAnnotationService Annotations { get; }
    public MaterializationPolicyService MaterializationPolicies { get; }
    public HistoryRuntimeHealth Health { get; private set; }
    public string? MaintenanceDiagnostic { get; private set; }
    public string? HealthDiagnostic { get; private set; }

    internal async Task<bool> CleanupMergeArtifactsAsync()
    {
        if (!Directory.Exists(MergeSessions.Root)) return true;
        try
        {
            var catalog = (await LocalReplicaCatalogStore.LoadAsync(CancellationToken.None).ConfigureAwait(false)).Value;
            if (catalog is null) throw new InvalidDataException("Merge cleanup requires a valid replica catalog.");
            MergeSessions.CleanupTerminalArtifacts(catalog);
            MaintenanceDiagnostic = null; return true;
        }
        catch (Exception ex) { MaintenanceDiagnostic = ex.Message; return false; }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await Repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var packs = await Repository.ReadAllPacksAsync(cancellationToken).ConfigureAwait(false);
            new HistoryRepositoryValidator(_codec).Validate(ConfigId, packs);

            await using (var operation = await Services.NativeHistoryConfigurationOperationGate.EnterHistoryAsync(ConfigId, cancellationToken).ConfigureAwait(false))
                await new HistoryRestoreTransactionJournalStore(this, new FileSystemHistoryRestoreMutationBackend())
                    .RecoverIncompleteAsync(cancellationToken).ConfigureAwait(false);

            var localRecovery = new HistoryLocalStateJournalRecovery(
                WorkspaceStore,
                LocalReplicaCatalogStore);
            await Repository.Journals.RecoverAsync(
                localRecovery.ApplyCommittedStateAsync,
                (_, _) => Task.CompletedTask,
                cancellationToken).ConfigureAwait(false);
            await CleanupMergeArtifactsAsync().ConfigureAwait(false);

            var rebuilt = false;
            try
            {
                if (!File.Exists(Index.IndexPath)
                    || await Index.GetSchemaVersionAsync(cancellationToken).ConfigureAwait(false) != HistoryIndex.CurrentSchemaVersion
                    || await Index.GetIndexedPackCountAsync(cancellationToken).ConfigureAwait(false) != packs.Count)
                {
                    await Index.RebuildAsync(packs, cancellationToken).ConfigureAwait(false);
                    rebuilt = true;
                }
            }
            catch (Exception ex) when (ex is SqliteException or InvalidDataException or HistoryRepositoryValidationException or IOException)
            {
                // SQLite 只是派生缓存；损坏时直接从 immutable packs 重建，不把 db 当 authority。
                await Index.RebuildAsync(packs, cancellationToken).ConfigureAwait(false);
                rebuilt = true;
            }

            await RecoverChainRewritesAsync(cancellationToken).ConfigureAwait(false);
            await RefreshLocalStateHealthAsync(cancellationToken).ConfigureAwait(false);

            _initialized = true;
            ChangeFeed.Publish(ConfigId, HistoryChangeKind.RepositoryInitialized);
            if (rebuilt)
            {
                ChangeFeed.Publish(ConfigId, HistoryChangeKind.IndexRebuilt);
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        MutationGate.Dispose();
        WorkspaceStore.Dispose();
        LocalReplicaCatalogStore.Dispose();
        CaptureBaselines.Dispose();
        Index.Dispose();
        Repository.Dispose();
        _initializationGate.Dispose();
        return ValueTask.CompletedTask;
    }

    internal void ObservePendingRecovery()
    {
        try
        {
            HistoryRestoreTransactionJournalStore.RequireRecovered(Repository.Paths.TransactionsRoot);
            Retention.HistoryChainRewriteJournalStore.RequireRecovered(Repository.Paths.LocalStateRoot);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Health |= HistoryRuntimeHealth.WorkspaceRecoveryRequired;
            HealthDiagnostic = $"Config {ConfigId}: transactions ({Repository.Paths.TransactionsRoot}): {ex.Message}";
        }
    }

    internal async Task RecoverChainRewritesAsync(CancellationToken token)
    {
        if (!Retention.HistoryChainRewriteJournalStore.HasPending(Repository.Paths.LocalStateRoot)) return;
        await EnsureIndexCurrentAsync(token).ConfigureAwait(false);
        var completed = await new Retention.HistoryChainRewriteJournalStore(this).RecoverAsync(token).ConfigureAwait(false);
        MaintenanceDiagnostic = completed ? null : "Backup chain recovery or archive reclamation needs attention; original archive bytes have been retained.";
        await RefreshLocalStateHealthAsync(token).ConfigureAwait(false);
        ChangeFeed.Publish(ConfigId, HistoryChangeKind.LocalStateChanged);
    }

    internal async Task RefreshLocalStateHealthAsync(CancellationToken cancellationToken = default)
    {
        var workspace = await WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var catalog = await LocalReplicaCatalogStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var packCount = (await Repository.ReadAllPacksAsync(cancellationToken).ConfigureAwait(false)).Count;
        Health = HistoryRuntimeHealth.Ready;
        HealthDiagnostic = null;
        ObservePendingRecovery();
        if (workspace.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible
            || (workspace.Status == DeviceLocalStateStatus.Missing && packCount > 0))
        {
            Health |= HistoryRuntimeHealth.WorkspaceRecoveryRequired;
            HealthDiagnostic = AppendDiagnostic(HealthDiagnostic, $"Config {ConfigId}: workspace.json: {workspace.Status}. {workspace.Diagnostic}");
        }
        if (catalog.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible
            || (catalog.Status == DeviceLocalStateStatus.Missing && packCount > 0))
        {
            Health |= HistoryRuntimeHealth.LocalReplicaCatalogRecoveryRequired;
            HealthDiagnostic = AppendDiagnostic(HealthDiagnostic, $"Config {ConfigId}: replicas.json: {catalog.Status}. {catalog.Diagnostic}");
        }
    }

    private static string AppendDiagnostic(string? previous, string next)
        => string.IsNullOrWhiteSpace(previous) ? next : previous + "\n" + next;

    internal async Task EnsureIndexCurrentAsync(CancellationToken cancellationToken = default)
    {
        var packs = await Repository.ReadAllPacksAsync(cancellationToken).ConfigureAwait(false);
        var needsRebuild = false;
        try
        {
            if (!File.Exists(Index.IndexPath)
                || await Index.GetSchemaVersionAsync(cancellationToken).ConfigureAwait(false) != HistoryIndex.CurrentSchemaVersion
                || await Index.GetIndexedPackCountAsync(cancellationToken).ConfigureAwait(false) != packs.Count)
            {
                needsRebuild = true;
            }
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException or HistoryRepositoryValidationException or IOException)
        {
            needsRebuild = true;
        }

        if (needsRebuild)
        {
            await Index.RebuildAsync(packs, cancellationToken).ConfigureAwait(false);
            ChangeFeed.Publish(ConfigId, HistoryChangeKind.IndexRebuilt);
        }
    }
}

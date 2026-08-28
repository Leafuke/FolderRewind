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
        MutationGate = new HistoryMutationGate(repository.ConfigId);
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
    public HistoryMutationGate MutationGate { get; }
    public HistoryChangeFeed ChangeFeed { get; }
    public HistoryQueryService Query { get; }
    public HistoryCommitCoordinator Commit { get; }
    public HistoryBranchService Branches { get; }
    public HistoryAnnotationService Annotations { get; }
    public MaterializationPolicyService MaterializationPolicies { get; }
    public HistoryRuntimeHealth Health { get; private set; }

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

            var localRecovery = new HistoryLocalStateJournalRecovery(
                WorkspaceStore,
                LocalReplicaCatalogStore);
            await Repository.Journals.RecoverAsync(
                localRecovery.ApplyCommittedStateAsync,
                (_, _) => Task.CompletedTask,
                cancellationToken).ConfigureAwait(false);

            var rebuilt = false;
            try
            {
                if (!File.Exists(Index.IndexPath)
                    || await Index.GetIndexedPackCountAsync(cancellationToken).ConfigureAwait(false) != packs.Count)
                {
                    await Index.RebuildAsync(packs, cancellationToken).ConfigureAwait(false);
                    rebuilt = true;
                }
            }
            catch (Exception ex) when (ex is SqliteException or InvalidDataException or HistoryRepositoryValidationException)
            {
                // SQLite 只是派生缓存；损坏时直接从 immutable packs 重建，不把 db 当 authority。
                await Index.RebuildAsync(packs, cancellationToken).ConfigureAwait(false);
                rebuilt = true;
            }

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

    internal async Task RefreshLocalStateHealthAsync(CancellationToken cancellationToken = default)
    {
        var workspace = await WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var catalog = await LocalReplicaCatalogStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        Health = HistoryRuntimeHealth.Ready;
        if (workspace.Status != DeviceLocalStateStatus.Valid)
        {
            Health |= HistoryRuntimeHealth.WorkspaceRecoveryRequired;
        }
        if (catalog.Status != DeviceLocalStateStatus.Valid)
        {
            Health |= HistoryRuntimeHealth.LocalReplicaCatalogRecoveryRequired;
        }
    }

    internal async Task EnsureIndexCurrentAsync(CancellationToken cancellationToken = default)
    {
        var packs = await Repository.ReadAllPacksAsync(cancellationToken).ConfigureAwait(false);
        if (!File.Exists(Index.IndexPath)
            || await Index.GetIndexedPackCountAsync(cancellationToken).ConfigureAwait(false) != packs.Count)
        {
            await Index.RebuildAsync(packs, cancellationToken).ConfigureAwait(false);
        }
    }
}

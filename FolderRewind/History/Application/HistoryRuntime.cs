using FolderRewind.History.Domain;
using FolderRewind.History.Capture;
using FolderRewind.History.Index;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

[Flags]
public enum HistoryRuntimeHealth
{
    Ready = 0,
    WorkspaceRecoveryRequired = 1,
    LocalReplicaCatalogRecoveryRequired = 2,
    LocalStateInitializationRequired = 4
}

public enum HistoryRuntimeIssueKind { Workspace, Transaction, ReplicaCatalog, Migration, Maintenance }
public sealed record HistoryRuntimeIssue(HistoryRuntimeIssueKind Kind, string Diagnostic, bool BlocksMutation, int? ErrorCode = null);

public sealed class HistoryRuntime : IAsyncDisposable
{
    private readonly HistoryPackCodec _codec;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;
    private bool _disposed;
    private readonly object _restoreCleanupSync = new();
    private readonly CancellationTokenSource _restoreCleanupLifetime = new();
    private Task _restoreCleanupTask = Task.CompletedTask;
    private bool _restoreCleanupRunning, _restoreCleanupRequested;
    private int _restoreCleanupGeneration;

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
        MigrationState = new(repository.ConfigId, repository.Paths.LocalStateRoot);
        CaptureBaselines = new SourceCaptureBaselineCache(repository.Paths.LocalStateRoot);
        MergeSessions = new MergeSessionStore(repository.Paths.LocalStateRoot);
        MutationGate = new HistoryMutationGate(repository.ConfigId, () =>
        {
            HistoryRestoreTransactionJournalStore.RequireRecovered(repository.Paths.TransactionsRoot);
            repository.Journals.RequireRecovered();
            repository.Imports.RequireRecovered();
            MigrationState.RequireRecovered();
            Retention.HistoryChainRewriteJournalStore.RequireRecovered(repository.Paths.LocalStateRoot);
        });
        ChangeFeed = new HistoryChangeFeed();
        Query = new HistoryQueryService(Index);
        Commit = new HistoryCommitCoordinator(this, _codec);
        CloudJobs = new Cloud.HistoryCloudJobJournal(this);
        Branches = new HistoryBranchService(this, _codec);
        Annotations = new HistoryAnnotationService(this, _codec);
        MaterializationPolicies = new MaterializationPolicyService(this, _codec);
        Repository.PacksInstalled = ApplyInstalledPacksAsync;
    }

    internal HistoryMigrationStateStore MigrationState { get; }
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
    public Cloud.HistoryCloudJobJournal CloudJobs { get; }
    public HistoryBranchService Branches { get; }
    public HistoryAnnotationService Annotations { get; }
    public MaterializationPolicyService MaterializationPolicies { get; }
    public HistoryRuntimeHealth Health { get; private set; }
    public string? MaintenanceDiagnostic { get; private set; }
    public string? HealthDiagnostic { get; private set; }
    public IReadOnlyList<HistoryRuntimeIssue> Issues { get; private set; } = [];

    internal void ReportMaintenance(string? diagnostic) => MaintenanceDiagnostic = diagnostic;

    internal void ScheduleRestoreCleanup()
    {
        lock (_restoreCleanupSync)
        {
            if (_disposed) return;
            _restoreCleanupRequested = true;
            if (_restoreCleanupRunning) return;
            _restoreCleanupRunning = true;
            int generation = ++_restoreCleanupGeneration;
            _restoreCleanupTask = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        lock (_restoreCleanupSync) _restoreCleanupRequested = false;
                        // 等待核心操作结束后立即释放租约；清理只访问旧容器，不长期占用配置锁。
                        await using (await Services.NativeHistoryConfigurationOperationGate.EnterHistoryAsync(ConfigId, _restoreCleanupLifetime.Token).ConfigureAwait(false)) { }
                        _restoreCleanupLifetime.Token.ThrowIfCancellationRequested();
                        new HistoryRestoreTransactionJournalStore(this, new FileSystemHistoryRestoreMutationBackend()).RetryDeferredCleanup();
                        await RefreshLocalStateHealthAsync(_restoreCleanupLifetime.Token).ConfigureAwait(false);
                        ChangeFeed.Publish(ConfigId, HistoryChangeKind.LocalStateChanged);
                        lock (_restoreCleanupSync)
                        {
                            if (_restoreCleanupRequested) continue;
                            _restoreCleanupRunning = false;
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) when (_restoreCleanupLifetime.IsCancellationRequested) { }
                catch (Exception ex) { ReportMaintenance("Restore cleanup: " + ex.Message); }
                finally { lock (_restoreCleanupSync) if (_restoreCleanupGeneration == generation) _restoreCleanupRunning = false; }
            });
        }
    }
    internal async Task DrainRestoreCleanupAsync(CancellationToken token = default)
    {
        while (true)
        {
            Task pending;
            lock (_restoreCleanupSync) pending = _restoreCleanupTask;
            await pending.WaitAsync(token).ConfigureAwait(false);
            lock (_restoreCleanupSync) if (!_restoreCleanupRunning) return;
        }
    }

    internal async Task<bool> CleanupMergeArtifactsAsync()
    {
        if (!Directory.Exists(MergeSessions.Root)) return true;
        try
        {
            CloudJobs.RequireCleanupSafe();
            var catalog = (await LocalReplicaCatalogStore.LoadAsync(CancellationToken.None).ConfigureAwait(false)).Value;
            if (catalog is null) throw new InvalidDataException("Merge cleanup requires a valid replica catalog.");
            MergeSessions.CleanupTerminalArtifacts(catalog);
            MaintenanceDiagnostic = null; return true;
        }
        catch (Exception ex) { MaintenanceDiagnostic = ex.Message; return false; }
    }

    // Explicit compatibility entry for startup/recovery consumers. Queries use InitializeReadableAsync.
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await InitializeReadableAsync(cancellationToken).ConfigureAwait(false);
        await using (var operation = await Services.NativeHistoryConfigurationOperationGate.EnterHistoryAsync(ConfigId, cancellationToken).ConfigureAwait(false))
            await RecoverInsideOperationAsync(operation, new FileSystemHistoryRestoreMutationBackend(), cancellationToken).ConfigureAwait(false);
        await DrainRestoreCleanupAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task InitializeReadableAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await Repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var packs = await Repository.ReadAllPacksAsync(cancellationToken).ConfigureAwait(false);
            new HistoryRepositoryValidator(_codec).Validate(ConfigId, packs);

            var rebuilt = false;
            try
            {
                if (!File.Exists(Index.IndexPath)
                    || await Index.GetSchemaVersionAsync(cancellationToken).ConfigureAwait(false) != HistoryIndex.CurrentSchemaVersion
                    || !await Index.MatchesFilesAsync(Repository.GetPackFiles(), cancellationToken, packs).ConfigureAwait(false))
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

            await Index.RecordFilesAsync(Repository.GetPackFiles(), cancellationToken).ConfigureAwait(false);
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _restoreCleanupLifetime.Cancel();
        await _restoreCleanupTask.ConfigureAwait(false);
        MutationGate.Dispose();
        WorkspaceStore.Dispose();
        LocalReplicaCatalogStore.Dispose();
        CaptureBaselines.Dispose();
        Index.Dispose();
        Repository.Dispose();
        _initializationGate.Dispose();
        _restoreCleanupLifetime.Dispose();
    }

    // Compatibility observation for synchronous consumers; recovery always recomputes all causes.
    internal void ObservePendingRecovery()
    {
        try
        {
            HistoryRestoreTransactionJournalStore.RequireRecovered(Repository.Paths.TransactionsRoot);
            Retention.HistoryChainRewriteJournalStore.RequireRecovered(Repository.Paths.LocalStateRoot);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Health |= HistoryRuntimeHealth.WorkspaceRecoveryRequired;
            HealthDiagnostic = $"Config {ConfigId}: transactions: {ex.Message}";
        }
    }

    internal async Task RecoverInsideOperationAsync(Services.NativeHistoryConfigurationOperationGate.Lease operation,
        IHistoryRestoreMutationBackend backend, CancellationToken token,
        Func<Services.NativeHistoryConfigurationOperationGate.Lease, CancellationToken, Task>? recoverNative = null)
    {
        operation.Require(ConfigId);
        await using var lease = await MutationGate.EnterForRecoveryAsync(token).ConfigureAwait(false);
        Exception? failure = null;
        try
        {
            var local = new HistoryLocalStateJournalRecovery(WorkspaceStore, LocalReplicaCatalogStore);
            await MigrationState.RecoverAsync(local, token).ConfigureAwait(false);
            await Repository.Journals.RecoverAsync(local.ApplyCommittedStateAsync, (_, _) => Task.CompletedTask, token).ConfigureAwait(false);
            await new HistoryRestoreTransactionJournalStore(this, backend).RecoverIncompleteAsync(token).ConfigureAwait(false);
            await EnsureIndexCurrentAsync(token).ConfigureAwait(false);
            try { await CloudJobs.ReplayAsync(Repository.ReadAllPacksAsync, token).ConfigureAwait(false); }
            catch (Exception ex) { CloudJobs.ObserveFailure(ex); }
            await CleanupMergeArtifactsAsync().ConfigureAwait(false);
            await RecoverChainRewritesAsync(token, insideMutationGate: true).ConfigureAwait(false);
            if (recoverNative is not null) await recoverNative(operation, token).ConfigureAwait(false);
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            try { await RefreshLocalStateHealthAsync(CancellationToken.None).ConfigureAwait(false); }
            catch when (failure is not null) { /* Preserve the recovery failure. */ }
            ChangeFeed.Publish(ConfigId, HistoryChangeKind.LocalStateChanged);
        }
    }

    internal async Task RecoverChainRewritesAsync(CancellationToken token, bool insideMutationGate = false)
    {
        if (!Retention.HistoryChainRewriteJournalStore.HasPending(Repository.Paths.LocalStateRoot)) return;
        await EnsureIndexCurrentAsync(token).ConfigureAwait(false);
        var completed = await new Retention.HistoryChainRewriteJournalStore(this).RecoverAsync(token, insideMutationGate).ConfigureAwait(false);
        MaintenanceDiagnostic = completed ? null : "Backup chain recovery or archive reclamation needs attention; original archive bytes have been retained.";
        await RefreshLocalStateHealthAsync(token).ConfigureAwait(false);
        ChangeFeed.Publish(ConfigId, HistoryChangeKind.LocalStateChanged);
    }

    internal async Task RefreshLocalStateHealthAsync(CancellationToken cancellationToken = default)
    {
        var issues = new List<HistoryRuntimeIssue>();
        var health = HistoryRuntimeHealth.Ready;
        Observe(HistoryRuntimeIssueKind.Transaction, () => HistoryRestoreTransactionJournalStore.RequireRecovered(Repository.Paths.TransactionsRoot));
        Observe(HistoryRuntimeIssueKind.Transaction, Repository.Journals.RequireRecovered);
        Observe(HistoryRuntimeIssueKind.Transaction, () => Retention.HistoryChainRewriteJournalStore.RequireRecovered(Repository.Paths.LocalStateRoot));
        bool migrating = false;
        Observe(HistoryRuntimeIssueKind.Migration, () => { migrating = MigrationState.Load()?.InitializationPending == true; MigrationState.RequireRecovered(); });
        if (migrating)
        {
            health |= HistoryRuntimeHealth.LocalStateInitializationRequired;
            issues.Add(new(HistoryRuntimeIssueKind.Migration, $"Config {ConfigId}: migration initialization pending.", true));
        }
        var workspace = await WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var catalog = await LocalReplicaCatalogStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var hasPacks = Repository.GetPackFiles().Count > 0;
        AddState(HistoryRuntimeIssueKind.Workspace, "workspace.json", workspace.Status, workspace.Diagnostic,
            HistoryRuntimeHealth.WorkspaceRecoveryRequired, workspace.ErrorCode);
        AddState(HistoryRuntimeIssueKind.ReplicaCatalog, "replicas.json", catalog.Status, catalog.Diagnostic,
            HistoryRuntimeHealth.LocalReplicaCatalogRecoveryRequired, catalog.ErrorCode);
        try
        {
            var cleanup = Directory.Exists(Repository.Paths.TransactionsRoot)
                ? Directory.EnumerateFiles(Repository.Paths.TransactionsRoot, "restore-*.json", SearchOption.AllDirectories)
                    .Where(path => Path.GetFileName(path) is "restore-cleanup.json" or "restore-preparation.json").ToArray() : [];
            if (cleanup.Length > 0) issues.Add(new(HistoryRuntimeIssueKind.Maintenance, "Restore preparation/cleanup pending: " + string.Join(", ", cleanup), false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { issues.Add(new(HistoryRuntimeIssueKind.Maintenance, ex.Message, false, ex.HResult)); }
        if (MaintenanceDiagnostic is { Length: > 0 } maintenance)
            issues.Add(new(HistoryRuntimeIssueKind.Maintenance, maintenance, false));
        Health = health;
        Issues = issues.ToArray();
        HealthDiagnostic = issues.Any(i => i.BlocksMutation) ? string.Join("\n", issues.Where(i => i.BlocksMutation).Select(i => i.Diagnostic)) : null;

        void Observe(HistoryRuntimeIssueKind kind, Action read)
        {
            try { read(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException or ArgumentException)
            {
                health |= kind == HistoryRuntimeIssueKind.Migration ? HistoryRuntimeHealth.LocalStateInitializationRequired : HistoryRuntimeHealth.WorkspaceRecoveryRequired;
                issues.Add(new(kind, $"Config {ConfigId}: transactions/local state: {ex.Message}", true, ex.HResult));
            }
        }
        void AddState(HistoryRuntimeIssueKind kind, string role, DeviceLocalStateStatus status, string diagnostic, HistoryRuntimeHealth flag, int? errorCode)
        {
            if (status == DeviceLocalStateStatus.Valid || status == DeviceLocalStateStatus.Missing && !hasPacks) return;
            health |= migrating && status == DeviceLocalStateStatus.Missing ? HistoryRuntimeHealth.LocalStateInitializationRequired : flag;
            issues.Add(new(kind, $"Config {ConfigId}: {role}: {status}. {diagnostic}", true, errorCode));
        }
    }

    private async Task ApplyInstalledPacksAsync(IReadOnlyList<HistoryPackReadResult> existing,
        IReadOnlyList<HistoryPackReadResult> installed, CancellationToken token)
    {
        // Called inside the repository gate after durable installation and full union validation.
        try
        {
            var files = Repository.GetPackFiles();
            var installedIds = installed.Select(p => p.Pack.PackId).ToHashSet();
            var previousFiles = files.Where(f => !installedIds.Contains(f.PackId)).ToArray();
            if (File.Exists(Index.IndexPath)
                && await Index.GetSchemaVersionAsync(token).ConfigureAwait(false) == HistoryIndex.CurrentSchemaVersion
                && await Index.MatchesFilesAsync(previousFiles, token).ConfigureAwait(false))
            {
                await Index.ApplyPacksAsync(installed, token).ConfigureAwait(false);
                await Index.RecordFilesAsync(files.Where(f => installedIds.Contains(f.PackId)).ToArray(), token).ConfigureAwait(false);
            }
            else
            {
                await Index.RebuildAsync(existing.Concat(installed).ToArray(), token).ConfigureAwait(false);
                await Index.RecordFilesAsync(files, token).ConfigureAwait(false);
                ChangeFeed.Publish(ConfigId, HistoryChangeKind.IndexRebuilt);
            }
        }
        catch
        {
            // Facts are already durable. A missing/stale inventory forces recovery on the next query.
        }
    }

    internal Task EnsureIndexCurrentAsync(CancellationToken cancellationToken = default)
        => Repository.WithGateAsync(async () =>
        {
            var files = Repository.GetPackFiles();
            try
            {
                if (File.Exists(Index.IndexPath)
                    && await Index.GetSchemaVersionAsync(cancellationToken).ConfigureAwait(false) == HistoryIndex.CurrentSchemaVersion
                    && await Index.MatchesFilesAsync(files, cancellationToken).ConfigureAwait(false)) return;
            }
            catch (Exception ex) when (ex is SqliteException or InvalidDataException or HistoryRepositoryException or IOException) { }
            var packs = await Repository.ReadAllPacksInsideGateAsync(cancellationToken).ConfigureAwait(false);
            new HistoryRepositoryValidator(_codec).Validate(ConfigId, packs);
            await Index.RebuildAsync(packs, cancellationToken).ConfigureAwait(false);
            await Index.RecordFilesAsync(files, cancellationToken).ConfigureAwait(false);
            ChangeFeed.Publish(ConfigId, HistoryChangeKind.IndexRebuilt);
        }, cancellationToken);
}

using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Models;
using System.Text.Json;

namespace FolderRewind.Services
{
    internal static class UiDispatcherService
    {
        public static Task<T> RunOnUiAsync<T>(Func<Task<T>> action) => action();
    }

    internal static class NativeHistoryConfigLease
    {
        internal static string Signature(BackupConfig config) => JsonSerializer.Serialize(new
        { config.Id, config.ConfigRevision, config.DestinationPath, Sources = config.SourceFolders.Select(f => f.Path) });
    }

    // Only the host boundary is replaced. Tests exercise the production service, tracker and persisted sessions.
    internal static class NativeHistoryApplicationService
    {
        internal static Func<BackupConfig, HistoryRestoreService> RestoreFactory { get; set; } = null!;
        internal static Func<BackupConfig, BranchId, CancellationToken, Task<MergeSession?>> Start { get; set; } = null!;
        internal static Func<BackupConfig, MergeSession, CancellationToken, Task<MergeSession>> Recompute { get; set; } = null!;
        internal static Func<BackupConfig, MergeSession, CancellationToken, Task<MergeReviewSnapshot>> ReviewFactory { get; set; } = null!;
        public static Task<HistoryRestoreService> CreateRestoreServiceAsync(BackupConfig config, CancellationToken token)
            => Task.FromResult(RestoreFactory(config));
        internal static Task<MergeSession?> StartMergeAsync(BackupConfig config, BranchId branch, CancellationToken token)
            => Start(config, branch, token);
        internal static Task<MergeSession> RecomputeMergeAsync(BackupConfig config, MergeSession session, CancellationToken token)
            => Recompute(config, session, token);
        internal static Task<MergeSession> PrepareMergeReplicasAsync(BackupConfig config, MergeSession session, CancellationToken token)
            => throw new NotSupportedException();
        internal static Task<MergeReviewSnapshot> PrepareMergeReviewAsync(BackupConfig config, MergeSession session,
            Action<MergeOperationStage> progress, CancellationToken token) => ReviewFactory(config, session, token);
        internal static Task<HistoryRestoreResult> ApplyMergeAsync(BackupConfig config, MergeSession session, CancellationToken token,
            MergeReviewSnapshot review, Action<MergeOperationStage> progress, Func<CancellationToken, CancellationToken> enterCritical)
            => throw new NotSupportedException();
        internal static Task<MergeTreeManifest> MaterializeMergeComparisonAsync(HistoryRuntime runtime, HistoryRestoreService restore,
            MergeSession session, CheckpointSource checkpoint, CancellationToken token) => throw new NotSupportedException();
    }
}

namespace FolderRewind.History.Application
{
    internal static class NativeHistoryCoreGateway
    {
        internal static HistoryRuntime Runtime { get; set; } = null!;
        public static Task<HistoryRuntime> EnsureReadyAsync(BackupConfig config, CancellationToken token) => Task.FromResult(Runtime);
    }
}

using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static partial class NativeHistoryApplicationService
{
    internal static async Task<MergeSession?> StartMergeAsync(BackupConfig config, BranchId source, CancellationToken token)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        if (!NativeHistoryRestoreOrchestrator.IsCoordinatorAvailable(config, out var diagnostic))
            throw new InvalidOperationException(diagnostic);
        var restore = CreateRestoreService(config, runtime);
        await restore.RecoverIncompleteAsync(token).ConfigureAwait(false);
        return await new HistoryMergeService(runtime, restore).StartAsync(source, NativeHistoryConfigLease.Signature(config),
            await BindingsAsync(config, config.SourceFolders, token).ConfigureAwait(false), token).ConfigureAwait(false);
    }

    internal static async Task<MergeSession> RecomputeMergeAsync(BackupConfig config, MergeSession session, CancellationToken token)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        return await new HistoryMergeService(runtime, CreateRestoreService(config, runtime)).RecomputeAsync(session,
            NativeHistoryConfigLease.Signature(config), await BindingsAsync(config, config.SourceFolders, token).ConfigureAwait(false), token).ConfigureAwait(false);
    }

    internal static async Task<MergeSession> PrepareMergeReplicasAsync(BackupConfig config, MergeSession session, CancellationToken token)
    {
        if (session.State is not (MergeSessionState.Preparing or MergeSessionState.Resolving or MergeSessionState.Ready))
            throw new HistoryMergeBlockedException(new(MergeDiagnosticCode.Stale));
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        var restore = CreateRestoreService(config, runtime);
        try
        {
            var inputs = session.Plan.Sources.SelectMany(s => s.Action == HistoryMergeSourceAction.Reuse
                ? new[] { s.Ours?.VersionId == s.ReuseVersionId ? s.Ours : s.Theirs }
                : s.Action == HistoryMergeSourceAction.Remove ? [] : new[] { s.Base, s.Ours, s.Theirs })
                .Where(s => s?.VersionId is not null).Select(s => (s!.SourceId, s.VersionId!.Value));
            await PrepareExactVersionsAsync(config, runtime, restore, inputs, token).ConfigureAwait(false);
            await using (var gate = await runtime.MutationGate.EnterAsync(token).ConfigureAwait(false))
            {
                var current = await new HistoryMergePlanner(runtime).BuildAsync(session.Plan.Theirs.BranchId,
                    await RequireWorkspaceAsync(runtime, token).ConfigureAwait(false), NativeHistoryConfigLease.Signature(config),
                    await BindingsAsync(config, config.SourceFolders, token).ConfigureAwait(false), token).ConfigureAwait(false);
                if (current.Ours.UpdateId != session.Plan.Ours.UpdateId || current.Theirs.UpdateId != session.Plan.Theirs.UpdateId
                    || current.ConfigRevision != session.Plan.ConfigRevision || !HistoryRestoreTransactionJournalStore.WorkspaceEquals(
                        current.ExpectedWorkspace, session.ProtectedWorkspace ?? session.Plan.ExpectedWorkspace))
                    return runtime.MergeSessions.Update(session, MergeSessionState.Stale);
            }
            return session.State == MergeSessionState.Preparing
                ? await new HistoryMergeService(runtime, restore).PrepareAsync(session, token).ConfigureAwait(false) : session;
        }
        catch (HistoryMergeBlockedException ex) { return runtime.MergeSessions.SetDiagnostic(session, ex.Diagnostic); }
    }

    internal static async Task<HistoryRestoreResult> ApplyMergeAsync(BackupConfig config, MergeSession session, CancellationToken token)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        var restore = CreateRestoreService(config, runtime);
        var archives = new SevenZipHistoryArchiveBackend(config);
        void ReportFailure(string stage, Exception ex) => LogService.LogError(
            $"Merge config={config.Id} session={session.Id} revision={session.Revision} stage={stage}", "Merge", ex);
        var builder = new HistoryMergeCommitBuilder(runtime, restore, archives, archives,
            (version, staging, cancellation) => CaptureMergeMetadataAsync(config, version, staging, cancellation));
        var apply = new HistoryMergeApplyService(runtime, restore, builder,
            new SafetySnapshotWorkingStateProtector(config, runtime, SafetySnapshotReason.BeforeMerge),
            async cancellation => (NativeHistoryConfigLease.Signature(config),
                await BindingsAsync(config, config.SourceFolders, cancellation).ConfigureAwait(false)), reportFailure: ReportFailure);
        PreparedMerge prepared;
        HistoryMergeApplyService.CoordinationPlan scope;
        var stage = "recover";
        try
        {
            await restore.RecoverIncompleteAsync(token).ConfigureAwait(false);
            stage = "build-result";
            prepared = await builder.BuildAsync(session, token).ConfigureAwait(false);
            stage = "payload-validation";
            using var validation = await builder.ValidateAndLockAsync(prepared, token).ConfigureAwait(false);
            stage = "coordination";
            scope = await apply.PlanCoordinationAsync(prepared, session.ProtectedWorkspace ?? session.Plan.ExpectedWorkspace,
                session.Plan.Bindings, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            stage = ex.Data["MergeStage"] as string ?? stage;
            ReportFailure(stage, ex);
            var diagnostic = (ex as HistoryMergeBlockedException)?.Diagnostic
                ?? new HistoryMergeDiagnostic(MergeDiagnosticCode.PreparationFailed, Detail: $"{stage}: {ex.Message}");
            return new(diagnostic.Code == MergeDiagnosticCode.PreparationRequired ? HistoryRestoreStatus.PreparationRequired
                : HistoryRestoreStatus.BlockedBeforeMutation, ex.Message, false, [], MergeDiagnostic: diagnostic);
        }
        async Task<HistoryRestoreResult> ExecuteAsync(CancellationToken ct)
        {
                var result = await apply.ApplyAsync(session, ct, prepared, scope).ConfigureAwait(false);
                var completed = await MergePostActions.CompleteAsync(result,
                    ct => BackupService.SynchronizeCaptureBaselinesWithWorkspaceAsync(config, prepared.Sources.Select(s => s.Version.SourceId).ToArray(), ct),
                    async ct =>
                    {
                        var failures = new List<Exception>();
                        foreach (var source in prepared.Sources.Select(s => s.Version.SourceId))
                        {
                            try
                            {
                                var baseline = await runtime.CaptureBaselines.LoadAsync(source, ct).ConfigureAwait(false);
                                if (baseline is not null) await runtime.CaptureBaselines.RemoveAsync(source, baseline.Revision, ct).ConfigureAwait(false);
                            }
                            catch (Exception ex) { failures.Add(ex); }
                        }
                        if (failures.Count > 0) throw new AggregateException(failures);
                    }, ReportFailure).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(completed.Diagnostic) && completed.Status != HistoryRestoreStatus.Committed)
                    LogService.LogWarning($"Merge config={config.Id} session={session.Id} revision={session.Revision} status={completed.Status}: {completed.Diagnostic}", "Merge");
                return completed;
        }
        if (scope.Writes.Count == 0) return await ExecuteAsync(token).ConfigureAwait(false);
        var affected = config.SourceFolders.Where(f => scope.NeedsProtection || scope.Writes.ContainsKey(Source(f))).ToArray();
        return await new NativeHistoryRestoreOrchestrator().ExecuteAsync(config, affected, session.Id.ToString(),
            ExecuteAsync, token, WorkspaceOperationKind.Merge).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<VersionMetadataSnapshot>> CaptureMergeMetadataAsync(BackupConfig config,
        SourceVersion version, string staging, CancellationToken token)
    {
        try
        {
            var folder = config.SourceFolders.Single(f => Source(f) == version.SourceId);
            await using var session = await PluginV3BackupSession.PrepareAsync(config, folder, token).ConfigureAwait(false);
            var result = await session.CaptureVersionMetadataAsync(token, staging).ConfigureAwait(false);
            foreach (var diagnostic in result.Diagnostics) LogService.LogWarning(diagnostic.Message, "Merge metadata");
            return result.Candidates.Select(c => VersionMetadataSnapshot.Create(version.VersionId, c.ProducerPluginId,
                c.SchemaId, c.SchemaVersion, c.Payload, DateTimeOffset.UtcNow)).ToArray();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogService.LogWarning($"Merged state metadata was skipped: {ex.Message}", "Merge metadata");
            return [];
        }
    }
}

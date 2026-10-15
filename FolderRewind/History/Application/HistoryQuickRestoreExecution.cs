using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

/// <summary>Quick 的同一 staging 同时用于内容比较和提交；恢复点策略独立于普通还原。</summary>
internal static class HistoryQuickRestoreExecution
{
    internal static async Task<HistoryRestoreResult> ExecuteAsync(HistoryRuntime history, HistoryRestoreService restore,
        HistoryRestoreSourceBinding binding, VersionId versionId, HistoryWorkspace workspace, HistoryRestoreApplyMode mode,
        bool snapshotEnabled, bool requiresCurrentInput,
        Func<CancellationToken, Task<(HistoryWorkspace? Workspace, HistoryRestoreResult? Result)>> protect,
        Func<CancellationToken, Task> validateSelection, CancellationToken token)
    {
        try
        {
            using var current = new HistoryWorkingTreeReadLease(binding);
            using var preparation = restore.CreatePreparationSession([binding], workspace);
            var version = await history.Query.GetVersionAsync(versionId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Quick restore Version is absent.");
            var prepared = await restore.PrepareSourceAsync(version, binding,
                mode == HistoryRestoreApplyMode.Clean ? MaterializationFidelity.Exact : MaterializationFidelity.Partial,
                mode, token, preparation).ConfigureAwait(false);
            prepared = await restore.PrepareOrdinaryRestoreAsync(prepared, token).ConfigureAwait(false);
            preparation?.RecordPrepared(binding.SourceId);
            var baseline = workspace.GetSourceState(binding.SourceId);
            if (mode == HistoryRestoreApplyMode.Clean && prepared.Fidelity == MaterializationFidelity.Exact
                && baseline.Relation == WorkspaceBaselineRelation.Exact && baseline.BaseVersionId == versionId
                && version.EffectiveSourceBoundaryFingerprint == binding.Boundary.Fingerprint
                && await HistoryExactWorkingStateProbe.MatchesPreparedAsync(prepared, binding, token).ConfigureAwait(false))
            {
                current.Validate();
                await using var guard = await restore.EnterFinalGuardAsync(token).ConfigureAwait(false);
                _ = await restore.RequireExpectedWorkspaceAsync(workspace, token).ConfigureAwait(false);
                await validateSelection(token).ConfigureAwait(false);
                preparation?.Abort();
                if (preparation?.Diagnostic is { } diagnostic) throw new IOException(diagnostic);
                return new(HistoryRestoreStatus.NoChanges, "Already at the active Branch's latest committed state.", false, []);
            }
            // 无当前数据补丁时，目标历史已固定；无需为原目录交换持有旧成员句柄。
            if (prepared.Fidelity == MaterializationFidelity.Exact && !requiresCurrentInput) current.Dispose();
            await validateSelection(token).ConfigureAwait(false);
            if (snapshotEnabled)
            {
                var protection = await protect(token).ConfigureAwait(false);
                if (protection.Result is not null) return protection.Result;
                workspace = protection.Workspace ?? throw new InvalidOperationException("Safety snapshot did not return its Workspace.");
            }
            token.ThrowIfCancellationRequested();
            preparation?.RebindExpected(workspace);
            current.Validate();
            restore.ReusablePreparation = prepared;
            restore.PreparationSession = preparation;
            try { return await restore.RestoreVersionAsync(versionId, binding, workspace, mode, token).ConfigureAwait(false); }
            finally { restore.ReusablePreparation = null; restore.PreparationSession = null; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        { return new(HistoryRestoreStatus.BlockedBeforeMutation, error.Message, false, []); }
    }
}

using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static partial class NativeHistoryApplicationService
{
    internal sealed record RunRestoreOption(SourceId SourceId, VersionId? VersionId, string Name,
        string BranchName, string Diagnostic, bool CanRestore, bool IsPartial);

    internal static async Task<IReadOnlyList<RunRestoreOption>> GetRunRestoreOptionsAsync(
        BackupConfig config, RunId runId, CancellationToken token)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        var run = await runtime.Query.GetRunAsync(runId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Backup Run is missing.");
        var restore = CreateRestoreService(config, runtime);
        var result = new List<RunRestoreOption>();
        foreach (var source in run.SourceResults)
        {
            var folder = config.SourceFolders.SingleOrDefault(f => Source(f) == source.SourceId);
            var branch = source.BranchUpdateId is { } updateId
                ? await runtime.Query.GetBranchUpdateAsync(updateId, token).ConfigureAwait(false) : null;
            string diagnostic = I18n.GetString("History_Run_SourceUnavailable");
            bool ready = false;
            bool partial = false;
            if (folder is not null && source.VersionId is { } versionId
                && source.Outcome is BackupRunSourceOutcome.Captured or BackupRunSourceOutcome.Reused)
            {
                try
                {
                    var binding = await BindingAsync(config, folder, token).ConfigureAwait(false);
                    var version = await runtime.Query.GetVersionAsync(versionId, token).ConfigureAwait(false);
                    partial = version?.CaptureScope == CaptureScope.PartialSource;
                    if (version is not null && version.SourceId == source.SourceId
                        && version.EffectiveSourceBoundaryFingerprint == binding.Boundary.Fingerprint)
                    {
                        var assessment = await restore.AssessVersionAsync(versionId,
                            version.CaptureScope == CaptureScope.PartialSource ? MaterializationFidelity.Partial : MaterializationFidelity.Exact,
                            AssessmentDepth.Deep, token).ConfigureAwait(false);
                        ready = assessment.Readiness == HistoryReadiness.Ready && assessment.Selected is not null;
                        diagnostic = ready ? I18n.GetString("History_NativeReadiness_Ready") : string.Join("; ", assessment.Candidates.SelectMany(c => c.Diagnostics));
                    }
                    else diagnostic = I18n.GetString("History_CheckoutReadiness_ConfigurationBoundaryChangeRequired");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { diagnostic = ex.Message; }
            }
            result.Add(new(source.SourceId, source.VersionId, folder?.DisplayName ?? I18n.GetString("History_SourceUnavailable"),
                branch?.Name ?? I18n.GetString("History_Branch_None"), diagnostic, ready, partial));
        }
        return result;
    }

    internal static async Task<HistoryRestoreResult> RestoreRunAsync(BackupConfig config, RunId runId,
        IReadOnlyCollection<SourceId> selectedSources, BackupService.RestoreMode mode, CancellationToken token)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        var run = await runtime.Query.GetRunAsync(runId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Backup Run is missing.");
        if (selectedSources.Count == 0 || selectedSources.Distinct().Count() != selectedSources.Count)
            return Blocked("Restore selection is empty or contains duplicate Sources.");
        var selections = new Dictionary<SourceId, VersionId>();
        foreach (var sourceId in selectedSources)
        {
            var result = run.SourceResults.SingleOrDefault(s => s.SourceId == sourceId);
            if (result?.VersionId is not { } version || result.Outcome is not (BackupRunSourceOutcome.Captured or BackupRunSourceOutcome.Reused))
                return Blocked("Selected Source has no restorable Version in this Run.");
            selections.Add(sourceId, version);
        }
        return await RestoreSelectionsAsync(config, selections, mode, token).ConfigureAwait(false);
    }

    private static async Task<HistoryRestoreResult> RestoreSelectionsAsync(BackupConfig config,
        IReadOnlyDictionary<SourceId, VersionId> selections, BackupService.RestoreMode mode, CancellationToken token)
    {
        NativeHostMutationContext.ThrowIfNestedMutation();
        if (selections.Count == 0) return Blocked("Restore selection is empty.");
        var folders = config.SourceFolders.Where(f => selections.ContainsKey(Source(f))).ToArray();
        if (folders.Length != selections.Count) return Blocked("Restore requires current bindings for every selected Source.");
        var revision = config.ConfigRevision;
        return await new NativeHistoryRestoreOrchestrator().ExecuteAsync(config, folders, "batch-restore",
            async cancellation =>
            {
                if (config.ConfigRevision != revision) return Blocked("Configuration changed before batch restore.");
                var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellation).ConfigureAwait(false);
                var workspace = await RequireWorkspaceAsync(runtime, cancellation).ConfigureAwait(false);
                var bindings = await BindingsAsync(config, folders, cancellation).ConfigureAwait(false);
                if (config.Archive.BackupBeforeRestore)
                {
                    var protection = await ProtectBeforeRestoreAsync(config, runtime, selections.Keys.ToArray(), cancellation).ConfigureAwait(false);
                    if (protection.Result is not null) return protection.Result;
                    workspace = protection.Workspace!;
                }
                var result = await CreateRestoreService(config, runtime, ordinaryRestore: true).RestoreVersionsAsync(
                    selections, bindings, workspace, MapRestoreMode(mode), cancellation).ConfigureAwait(false);
                if (result.Succeeded)
                    await BackupService.SynchronizeCaptureBaselinesWithWorkspaceAsync(config, result.AppliedSources, cancellation).ConfigureAwait(false);
                return result;
            }, token).ConfigureAwait(false);
    }
}

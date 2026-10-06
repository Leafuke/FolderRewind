using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static partial class NativeHistoryApplicationService
{
    internal sealed record PreparedRestoreTarget(SourceVersion Version, string FileName,
        BackupService.RestoreMode RequestedMode, BackupService.RestoreMode EffectiveMode,
        string ConfigSignature, HistoryQuickRestoreResolution? QuickSelection);

    internal static async Task<(PreparedRestoreTarget? Target, string Diagnostic)> PrepareVersionRestoreAsync(
        BackupConfig config, ManagedFolder folder, VersionId? versionId, BackupService.RestoreMode requestedMode,
        CancellationToken token = default, string? expectedConfigSignature = null)
    {
        NativeHostMutationContext.ThrowIfNestedMutation();
        var signature = expectedConfigSignature ?? NativeHistoryConfigLease.Signature(config);
        if (NativeHistoryConfigLease.Signature(config) != signature)
            return (null, I18n.GetString("SettingsProject_Stale"));
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        await runtime.EnsureIndexCurrentAsync(token).ConfigureAwait(false);
        var source = Source(folder);
        var restore = CreateRestoreService(config, runtime, expectedSignature: signature);
        HistoryQuickRestoreResolution? quick = null;
        if (versionId is null)
        {
            quick = await new HistoryQuickRestoreResolver(runtime, restore).ResolveAsync(source, AssessmentDepth.Deep, token)
                .ConfigureAwait(false);
            if (!quick.IsReady || quick.VersionId is null) return (null, quick.Diagnostic);
            versionId = quick.VersionId;
        }
        var version = await runtime.Query.GetVersionAsync(versionId.Value, token).ConfigureAwait(false);
        if (version is null || version.SourceId != source)
            return (null, "Restore Version does not exist or belongs to another Source.");
        var policy = MaterializationPolicyProjection.Project(version.VersionId,
            await runtime.Query.GetMaterializationPolicyTipsAsync(version.VersionId, token).ConfigureAwait(false));
        if (policy.HasExplicitPolicy && policy.EffectiveState == MaterializationPolicyState.Released)
            return (null, "Restore Version's payload has been released.");
        var effectiveMode = LegacyRecoveryPolicy.RequiresOverwrite(version) ? BackupService.RestoreMode.Overwrite : requestedMode;
        var fidelity = effectiveMode == BackupService.RestoreMode.Clean ? MaterializationFidelity.Exact : MaterializationFidelity.Partial;
        var assessment = await restore.AssessVersionAsync(version.VersionId, fidelity, AssessmentDepth.Deep, token).ConfigureAwait(false);
        if (assessment.Readiness != HistoryReadiness.Ready || assessment.Selected is null)
            return (null, HistoryQuickRestoreResolver.AssessmentDiagnostic(assessment,
                $"Restore Version {version.VersionId} is not recoverable with {fidelity} fidelity."));
        var representation = await runtime.Query.GetRepresentationAsync(assessment.Selected.RepresentationId, token).ConfigureAwait(false);
        var file = representation?.RepresentationSpecificMetadata.GetValueOrDefault("fileName")
            ?? representation?.RepresentationSpecificMetadata.GetValueOrDefault("legacyFileName")
            ?? (assessment.Selected.SelectedLocalPath is { } path ? Path.GetFileName(path) : version.VersionId.ToString());
        return (new(version, file, requestedMode, effectiveMode, signature, quick), string.Empty);
    }

    internal static async Task<HistoryRestoreResult> ExecutePreparedRestoreAsync(BackupConfig config, ManagedFolder folder,
        PreparedRestoreTarget target, CancellationToken token = default, RestoreRequestOptions? options = null)
    {
        NativeHostMutationContext.ThrowIfNestedMutation();
        if (Source(folder) != target.Version.SourceId || NativeHistoryConfigLease.Signature(config) != target.ConfigSignature)
            return Blocked(I18n.GetString("SettingsProject_Stale"));
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        var restore = CreateRestoreService(config, runtime, expectedSignature: target.ConfigSignature);
        if (target.QuickSelection is { } quick)
        {
            try { await new HistoryQuickRestoreResolver(runtime, restore).ValidateAsync(Source(folder), quick, token).ConfigureAwait(false); }
            catch (InvalidOperationException ex) { return Blocked(ex.Message); }
            if (target.EffectiveMode == BackupService.RestoreMode.Clean
                && await BackupService.DeepProbeWorkspaceVersionAsync(config, folder, target.Version.VersionId, token).ConfigureAwait(false))
                return new(HistoryRestoreStatus.NoChanges, "Already at the active Branch's latest committed state.", false, []);
        }
        options = (options ?? new RestoreRequestOptions()) with { Mode = target.EffectiveMode.ToString().ToLowerInvariant() };
        LogService.Log($"Restore target '{target.FileName}': requested_mode={target.RequestedMode.ToString().ToLowerInvariant()}; "
            + $"effective_mode={options.Mode}; version={target.Version.VersionId}", source: "History");
        return await new NativeHistoryRestoreOrchestrator().ExecuteAsync(config, [folder], target.Version.VersionId.ToString(),
            cancellation => RestoreVersionCoreAsync(config, folder, target.Version.VersionId, target.EffectiveMode, cancellation,
                requireSafetySnapshot: target.QuickSelection is not null, expectedSignature: target.ConfigSignature,
                quickSelection: target.QuickSelection), token, options: options).ConfigureAwait(false);
    }
}

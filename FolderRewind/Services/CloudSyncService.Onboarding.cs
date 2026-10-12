using FolderRewind.History.Application;
using FolderRewind.History.Cloud;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static partial class CloudSyncService
{
    public static async Task<HistoryRecoveryPreview> PreviewVersionForExportPreparationAsync(BackupConfig config, ManagedFolder folder,
        VersionId version, CancellationToken token = default)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        var preview = await CloudReadOnlyRecoveryService.PreviewAsync(runtime, version, token).ConfigureAwait(false);
        if (preview.Version.SourceId.Value != Guid.Parse(folder.Id)) throw new InvalidOperationException(I18n.GetString("Export_IdentityChanged"));
        return preview;
    }

    public static async Task PrepareVersionForExportAsync(BackupConfig config, ManagedFolder folder, VersionId version,
        CancellationToken token = default, RepresentationId? confirmedRepresentation = null)
    {
        config = await PrepareConnectionAsync(config, token).ConfigureAwait(false);
        using var connection = CaptureConnection(config);
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        var preview = await PreviewVersionForExportPreparationAsync(config, folder, version, token).ConfigureAwait(false);
        if (!preview.CanPrepare || confirmedRepresentation is { } expected && preview.Assessment.Selected?.RepresentationId != expected)
            throw new InvalidOperationException(I18n.GetString("Export_IdentityChanged"));
        var restore = await NativeHistoryApplicationService.CreateRestoreServiceAsync(config, token).ConfigureAwait(false);
        await NativeHistoryApplicationService.PrepareVersionsAsync(config, runtime, restore, [(new SourceId(Guid.Parse(folder.Id)), version)], preview.RequiredFidelity, token).ConfigureAwait(false);
    }
    public static async Task<HistoryCloudBackupResult> UploadVersionClosureAsync(BackupConfig config, VersionId version, CancellationToken token = default)
    {
        RequireMigrationCloudPermission(config);
        config = await PrepareConnectionAsync(config, token).ConfigureAwait(false);
        using var connection = CaptureConnection(config);
        var assessment = await NativeHistoryApplicationService.PreviewExportAsync(config, version, token).ConfigureAwait(false);
        if (assessment.Readiness != HistoryReadiness.Ready || assessment.Selected is null) throw new InvalidOperationException(I18n.GetString("CloudSetup_VersionNotReady"));
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        var selected = assessment.Selected.RepresentationId;
        var packs = await runtime.Repository.ReadAllPacksAsync(token).ConfigureAwait(false);
        var pack = packs.First(p => p.Pack.Objects.Any(o => o.Kind == HistoryObjectKinds.VersionRepresentation && o.Id == selected.ToString())).Pack;
        HistoryCloudJob job;
        await using (var gate = await runtime.MutationGate.EnterAsync(token).ConfigureAwait(false))
            job = runtime.CloudJobs.RegisterManual(pack, FreezeTarget(config), [selected]);
        var result = await ExecuteCloudJobAsync(runtime, job, token).ConfigureAwait(false);
        if (!result.Complete) ResumeCloudJobs(config);
        return result;
    }
}

using FolderRewind.History.Application;
using FolderRewind.History.Cloud;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
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
        config = BackupConfigCloneService.CloneForRuntimeMutation(config, I18n.GetString("Common_Failed"));
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
        config = BackupConfigCloneService.CloneForRuntimeMutation(config, I18n.GetString("Common_Failed"));
        using var connection = CaptureConnection(config);
        var assessment = await NativeHistoryApplicationService.PreviewExportAsync(config, version, token).ConfigureAwait(false);
        if (assessment.Readiness != HistoryReadiness.Ready || assessment.Selected is null) throw new InvalidOperationException(I18n.GetString("CloudSetup_VersionNotReady"));
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        var metadata = new HistoryMetadataSyncService(runtime, CreateHistoryTransport(config));
        var service = new HistoryCloudBackupService(runtime, CreateReplicaTransport(config), ct => metadata.SyncAsync(ct));
        var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
        var task = new BackupTask { FolderName = config.Name, Status = I18n.GetString("CloudSetup_UploadRunning"), IsIndeterminate = true };
        await RunOnUIAsync(() => BackupService.ActiveTasks.Add(task)).ConfigureAwait(false);
        try
        {
            var result = await service.UploadClosureAsync([assessment.Selected.RepresentationId], (id, ct) => Task.FromResult(catalog?.Entries
                .FirstOrDefault(e => e.RepresentationId == id && e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath)?.Locator.AbsolutePath), token).ConfigureAwait(false);
            await RunOnUIAsync(() => { task.IsCompleted = true; task.IsSuccess = result.Complete; task.IsIndeterminate = false;
                task.Status = I18n.GetString(result.Canceled ? "Common_Canceled" : result.Complete ? "CloudSetup_UploadComplete" : "CloudSetup_UploadIncomplete"); task.ErrorMessage = result.Complete ? "" : task.Status; }).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await RunOnUIAsync(() => { task.IsCompleted = true; task.IsSuccess = false; task.IsIndeterminate = false; task.Status = I18n.GetString("CloudSetup_UploadIncomplete"); task.ErrorMessage = task.Status; }).ConfigureAwait(false);
            throw;
        }
    }
}

#if NATIVE_ARTIFACT_ACCEPTANCE
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.History.Application;
using FolderRewind.History.Cloud;
using FolderRewind.History.Domain;
using FolderRewind.History.Merge;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Services.Development;

internal static partial class NativeArtifactAcceptance
{
    private static async Task<BackupConfig> ConfigureCloudFixtureAsync(Request request, BackupConfig config, string root, string suffix)
    {
        var credential = Path.Combine(root, "cloud-local.conf");
        if (!File.Exists(credential)) await File.WriteAllTextAsync(credential, "[acceptance]\ntype = local\n");
        using var context = new RcloneExecutionContext(request.Rclone ?? throw new IOException("A real rclone is required."), credential,
            root, "acceptance:" + suffix, ConfigService.RcloneTasksDirectory);
        var probe = await RcloneConnectionService.VerifyWriteAsync(context);
        if (!probe.ReadWriteVerified) throw new IOException("Local rclone write verification failed: " + probe.Diagnostic);
        var evidence = context.TargetRevisionEvidence + "|" + context.RemoteRoot;
        var profile = new CloudConnectionProfile { OwnerProjectId = config.Id, Scope = CloudConnectionScope.Project,
            CredentialOwnership = CloudCredentialOwnership.ExternalReference, CredentialReference = credential,
            RemoteName = "acceptance", ExecutablePath = request.Rclone!, WorkingDirectory = root,
            Access = CloudConnectionAccess.VerifiedWritable, VerificationEvidence = evidence };
        var saved = await ConfigService.UpdateAndSaveAsync(c =>
        {
            c.CloudConnections.Add(profile);
            var live = c.BackupConfigs.Single(p => p.Id == config.Id);
            live.Cloud.ConnectionProfileId = profile.Id; live.Cloud.RemotePathSuffix = suffix;
            live.Cloud.BindingRevision = Guid.NewGuid().ToString("N"); live.Cloud.LegacyConversionRequired = false;
            live.Cloud.WritableEvidence = evidence; live.Cloud.ReadableEvidence = evidence; live.Cloud.Enabled = false;
        });
        if (!saved.Success) throw new IOException(saved.ErrorMessage);
        await File.WriteAllTextAsync(Path.Combine(root, "cloud-temporary-scope"), OwnedPluginTemporaryStorage.ForLogicalRoot(
            Path.Combine(ConfigService.RcloneTasksDirectory, "native-transfers")));
        return ConfigService.CurrentConfig.BackupConfigs.Single(c => c.Id == config.Id);
    }

    private static async Task<int> RunCloudIntegrationAsync(Request request, BackupConfig config, string root)
    {
        var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config);
        var folder = config.SourceFolders.Single();
        var current = (await history.WorkspaceStore.LoadAsync()).Value!.SourceBaselines.Single().BaseVersionId!.Value;
        if (request.Phase == "cloud-recover")
        {
            var before = history.CloudJobs.Read().Jobs;
            var identities = before.SelectMany(j => j.UploadIntents).Select(i => i.ReplicaId).ToArray();
            var restore = await NativeHistoryApplicationService.CreateRestoreServiceAsync(config);
            await restore.RecoverIncompleteAsync(); await restore.RecoverIncompleteAsync();
            await CloudSyncService.RetryCloudJobsAsync(config);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var recoveryStore = Plugins.V3.NativeArtifactHostService.Store(config);
            async Task<bool> PendingAsync()
            {
                if (history.CloudJobs.Read().Jobs.Any(j => j.Protection == CloudProtectionDisposition.Held)) return true;
                using (await recoveryStore.EnterAsync(default)) return recoveryStore.Transfers().Count != 0;
            }
            // The durable Succeeded fact is published before best-effort subintent cleanup.
            // Wait for both, without racing the worker's final store mutation.
            while (await PendingAsync() && watch.Elapsed < TimeSpan.FromMinutes(2))
                await Task.Delay(100);
            var after = history.CloudJobs.Read().Jobs;
            if (after.Length != before.Length || after.Any(j => j.Phase != CloudJobPhase.Succeeded || j.Protection != CloudProtectionDisposition.Released)
                || !identities.SequenceEqual(after.SelectMany(j => j.UploadIntents).Select(i => i.ReplicaId)))
                throw new IOException("Cloud recovery changed Job identity or failed: " + JsonSerializer.Serialize(after));
            if ((await history.Query.GetStorageReplicasAsync((await history.Query.GetAllRepresentationsAsync()).Single(r => r.VersionId == current).RepresentationId)).Count != identities.Length) throw new IOException("Cloud recovery duplicated replica facts.");
            if (Plugins.V3.NativeArtifactHostService.Store(config).Transfers().Count != 0) throw new IOException("Completed cloud recovery retained transfer roots.");
            await File.WriteAllTextAsync(Path.Combine(root, "cloud-recover-result.json"), JsonSerializer.Serialize(new { Success = true, StableReplicaIds = true, RepeatedRecovery = true, request.CrashAt }));
            return 0;
        }
        config = await ConfigureCloudFixtureAsync(request, config, root, "cloud-a");
        var first = await CloudSyncService.UploadVersionClosureAsync(config, current);
        if (!first.Complete) throw new IOException("Production Cloud Job failed: " + JsonSerializer.Serialize(first));
        if (request.CrashAt is not null) throw new IOException("Requested cloud fault was not reached.");
        config = await ConfigureCloudFixtureAsync(request, config, root, "cloud-b");
        if (!(await CloudSyncService.UploadVersionClosureAsync(config, current)).Complete) throw new IOException("Second target failed.");
        var reused = await CloudSyncService.UploadVersionClosureAsync(config, current);
        if (!reused.Complete || reused.Items.Any(i => i.State != HistoryCloudItemState.Reused)) throw new IOException("Native availability reuse failed.");
        var receipt = Directory.EnumerateFiles(Path.Combine(root, "cloud-b"), "*.verified.json", SearchOption.AllDirectories).First();
        File.Delete(receipt);
        if (!(await CloudSyncService.UploadVersionClosureAsync(config, current)).Complete || !File.Exists(receipt)) throw new IOException("Missing receipt was not repaired.");
        var blob = receipt[..^".verified.json".Length]; File.Delete(blob);
        if (!(await CloudSyncService.UploadVersionClosureAsync(config, current)).Complete || !File.Exists(blob)) throw new IOException("Missing blob was not repaired.");
        var original = await File.ReadAllBytesAsync(blob); var damaged = (byte[])original.Clone(); damaged[0] ^= 1;
        await File.WriteAllBytesAsync(blob, damaged);
        var availability = await CloudSyncService.UploadVersionClosureAsync(config, current);
        if (!availability.Complete || availability.Items.Any(i => i.State != HistoryCloudItemState.Reused))
            throw new IOException("Availability reuse falsely imposed a full source Deep check.");
        var store = Plugins.V3.NativeArtifactHostService.Store(config);
        await File.WriteAllBytesAsync(blob, original);
        await VerifyReadOnlyCloudRestoreAsync(request, config, root, current);
        await File.WriteAllBytesAsync(blob, damaged);
        foreach (var node in (await store.Ledger.LoadAsync()).Artifacts) File.Delete(store.PayloadPath(node));
        var sourceBefore = (await MergeTreeManifest.ReadAsync(folder.Path, _ => true, default)).Digest;
        var preview = await CloudSyncService.PreviewVersionForExportPreparationAsync(config, folder, current);
        if (!preview.CanPrepare) throw new IOException("Remote-only Native preview did not require preparation.");
        var blocked = false;
        try { await CloudSyncService.PrepareVersionForExportAsync(config, folder, current); }
        catch (Exception ex) when (ex is not OperationCanceledException) { blocked = true; }
        if (!blocked || (await MergeTreeManifest.ReadAsync(folder.Path, _ => true, default)).Digest != sourceBefore)
            throw new IOException("Corrupt cloud source was accepted or target bytes changed.");
        await File.WriteAllBytesAsync(blob, original);
        await CloudSyncService.PrepareVersionForExportAsync(config, folder, current);
        var output = Path.Combine(root, "cloud-restored-export");
        await NativeHistoryApplicationService.ExportVersionAsync(config, folder, current, output);
        var selected = (await history.Query.GetAllRepresentationsAsync()).Single(r => r.VersionId == current);
        await VerifyCapturedTreeAsync(history, selected, folder.Path, output);
        if (store.Transfers().Any(t => t.JobId is null)) throw new IOException("Successful realization retained stopped failed downloads.");
        config = await ConfigureCloudFixtureAsync(request, config, root, "cloud-canceled");
        using var cancellation = new CancellationTokenSource(); var observed = false; var unsafeReleaseRejected = false;
        Observation = stage =>
        {
            if (stage != "CloudObject:before" || observed) return;
            observed = true;
            var job = history.CloudJobs.Read().Jobs.Last(j => j.Protection == CloudProtectionDisposition.Held);
            try { CloudSyncService.CancelCloudJobAsync(config.Id, job.JobId, true).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { unsafeReleaseRejected = true; }
            cancellation.Cancel();
        };
        HistoryCloudBackupResult canceled;
        try { canceled = await CloudSyncService.UploadVersionClosureAsync(config, current, cancellation.Token); }
        finally { Observation = null; }
        if (!observed || !unsafeReleaseRejected || !canceled.Canceled) throw new IOException("Active cancellation/abandonment contract failed.");
        var held = history.CloudJobs.Read().Jobs.Last(j => j.Protection == CloudProtectionDisposition.Held);
        if (!store.Transfers().Any(t => t.JobId == held.JobId)) throw new IOException("Cancellation dropped retry protection.");
        config = await ConfigureCloudFixtureAsync(request, config, root, "cloud-changed");
        await CloudSyncService.RetryCloudJobsAsync(config);
        if (history.CloudJobs.Read().Jobs.Single(j => j.JobId == held.JobId).Target != held.Target)
            throw new IOException("Target change rebound an existing Native Job.");
        await CloudSyncService.CancelCloudJobAsync(config.Id, held.JobId, true);
        if (store.Transfers().Any(t => t.JobId == held.JobId)) throw new IOException("Stopped abandonment retained retry subintents.");
        await File.WriteAllTextAsync(Path.Combine(root, "cloud-result.json"), JsonSerializer.Serialize(new
        { Success = true, RealCloudJob = true, TwoTargets = true, MissingBlobAndReceipt = true, AvailabilityIsNotDeep = true,
            SameSizeDamageDetectedBeforeMutation = true, RemoteOnlyRestore = true, ReadOnlyCloudRestore = true,
            FailedDownloadRootsReleased = true, CancellationRetainsRoots = true, StoppedAbandonment = true, FrozenTarget = true }));
        return 0;
    }

    private static async Task VerifyReadOnlyCloudRestoreAsync(Request request, BackupConfig config, string root, VersionId version)
    {
        var remote = Path.Combine(root, "cloud-b");
        async Task<string> DigestAsync()
        {
            var tree = await MergeTreeManifest.ReadAsync(remote, _ => true, default);
            return tree.Digest;
        }
        var before = await DigestAsync();
        using var connection = new RcloneExecutionContext(request.Rclone!, Path.Combine(root, "cloud-local.conf"), root,
            "acceptance:cloud-b", ConfigService.RcloneTasksDirectory);
        await using var readOnly = new HistoryRuntime(new FileHistoryRepository(new(config.Id), new(Path.Combine(root, "readonly-history"))));
        await readOnly.InitializeAsync();
        var pull = await new HistoryMetadataSyncService(readOnly, new RcloneNativeHistoryTransport(connection)).PullAsync();
        if (!pull.Succeeded) throw new IOException("Read-only remote metadata pull failed: " + pull.Diagnostic);
        var preview = await CloudReadOnlyRecoveryService.PreviewAsync(readOnly, version);
        if (!preview.CanPrepare) throw new IOException("Read-only Native preview is not preparable.");
        var destination = Path.Combine(root, "readonly-restored");
        await CloudReadOnlyRecoveryService.RestoreToNewLocationAsync(readOnly, connection, version, destination,
            config.IsEncrypted ? "p2-isolated-acceptance-only" : null, confirmedRepresentation: preview.Assessment.Selected!.RepresentationId);
        var representation = (await readOnly.Query.GetAllRepresentationsAsync()).Single(r => r.VersionId == version);
        await VerifyCapturedTreeAsync(readOnly, representation, config.SourceFolders.Single().Path, destination);
        if (await DigestAsync() != before) throw new IOException("Read-only cloud recovery wrote to remote storage.");
    }
}
#endif

#if NATIVE_ARTIFACT_ACCEPTANCE
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Services.Development;

internal static partial class NativeArtifactAcceptance
{
    private static async Task<int> RunApplicationServicesAsync(BackupConfig config, string root)
    {
        var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config);
        var folder = config.SourceFolders.Single();
        var source = new SourceId(Guid.Parse(folder.Id));
        var current = (await history.WorkspaceStore.LoadAsync()).Value!.GetSourceState(source).BaseVersionId!.Value;
        var preview = await NativeHistoryApplicationService.PreviewVersionRestoreAsync(config, folder, current);
        if (preview.Assessment.Readiness != HistoryReadiness.Ready) throw new IOException("Production restore preview is not Ready.");
        var selected = (await history.Query.GetAllRepresentationsAsync()).Single(r => r.RepresentationId == preview.Assessment.Selected!.RepresentationId);
        var export = Path.Combine(root, "service-export");
        await NativeHistoryApplicationService.ExportVersionAsync(config, folder, current, export);
        await VerifyCapturedTreeAsync(history, selected, folder.Path, export);
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "service-untracked.bin"), "must be removed by clean restore");
        var restore = await NativeHistoryApplicationService.RestoreVersionAsync(config, folder, current, BackupService.RestoreMode.Clean);
        if (!restore.Succeeded || File.Exists(Path.Combine(folder.Path, "service-untracked.bin"))) throw new IOException("Production clean restore failed: " + restore.Diagnostic);
        await VerifyCapturedTreeAsync(history, selected, folder.Path, export);
        var checkpoint = (await history.Query.GetAllCheckpointsAsync()).First(c => c.VersionId == current);
        var side = await history.Branches.CreateFromCheckpointAsync(checkpoint.CheckpointId, "service-side");
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "service-next.bin"), "capture after restore");
        var next = await BackupService.BackupConfigAsync(config);
        if (!next.IsSuccessful || !next.CreatedNewArchive) throw new IOException("Capture after restore failed.");
        var checkout = await NativeHistoryApplicationService.CheckoutAsync(config, side.BranchUpdate.UpdateId);
        if (!checkout.Succeeded || File.Exists(Path.Combine(folder.Path, "service-next.bin"))) throw new IOException("Production checkout failed: " + checkout.Diagnostic);
        await VerifyCapturedTreeAsync(history, selected, folder.Path, export);
        var graph = await history.Query.GetAllRepresentationsAsync();
        var catalog = (await history.LocalReplicaCatalogStore.LoadAsync()).Value!;
        var versions = (await history.Query.GetAllVersionsAsync()).ToDictionary(v => v.VersionId);
        var deletion = graph.Where(r => r.VersionId != current && r.Kind == RepresentationKind.PluginArtifact)
            .Where(r => catalog.Entries.Any(e => e.RepresentationId == r.RepresentationId))
            .OrderBy(r => versions[r.VersionId].CreatedAtUtc).First();
        var local = catalog.Entries.Single(e => e.RepresentationId == deletion.RepresentationId).Locator.Resolve();
        await NativeHistoryApplicationService.DeleteVersionLocalPayloadAsync(config, deletion.VersionId, deletion.RepresentationId, local, releaseVersion: true);
        if ((await history.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries.Any(e => e.RepresentationId == deletion.RepresentationId))
            throw new IOException("Native deletion retained its local registration.");
        var after = Path.Combine(root, "service-export-after-gc");
        await NativeHistoryApplicationService.ExportVersionAsync(config, folder, current, after);
        await VerifyCapturedTreeAsync(history, selected, folder.Path, after);
        var cleanup = await NativeHistoryApplicationService.RunManualCleanupAsync(config.Id, config.ConfigRevision,
            History.Retention.HistoryRetentionBenefitPolicy.CountFirst, null, default);
        if (cleanup?.HasWarnings == true) throw new IOException("Production cleanup returned warnings: " + JsonSerializer.Serialize(cleanup));
        await File.WriteAllTextAsync(Path.Combine(root, "app-services-result.json"), JsonSerializer.Serialize(new
        { Success = true, Preview = true, Export = true, CleanRestore = true, CaptureAfterRestore = true, Checkout = true, NativeDeletion = true, Cleanup = true, DirectoryEvidence = true }));
        return 0;
    }

    private static async Task<int> RunMigrationExportAsync(BackupConfig config, string root)
    {
        var path = Path.Combine(root, "integration.frhistory");
        await DataMigrationService.ExportAsync(path, [new(config.Id)], DataMigrationScope.ConfigurationAndHistory, true, default);
        var preview = await DataMigrationService.PreviewAsync(path, default);
        if (preview.Bundle?.History.FormatVersion != 4 || preview.Projects.Single().Migration!.NativePayloads.IsEmpty)
            throw new IOException("Native migration export did not create the typed format.");
        await File.WriteAllTextAsync(Path.Combine(root, "migration-export-result.json"), JsonSerializer.Serialize(new
        { Success = true, Bundle = path, preview.FileSha256, Format = 4 }));
        return 0;
    }

    private static async Task<BackupConfig?> ImportMigrationFixtureAsync(Request request, string root)
    {
        var preview = await DataMigrationService.PreviewAsync(request.MigrationBundle ?? throw new IOException("Migration bundle is required."), default);
        var project = preview.Projects.Single();
        var paths = new MigrationProjectPaths(Path.Combine(root, "backup"),
            [.. project.Sources.Select(s => new MigrationSourcePath(s.SourceId.ToString(), s.Name, Path.Combine(root, "source")))]);
        var result = await DataMigrationService.ImportAsync(preview, [project.ConfigId], ConfigImportMode.CrossDevice, false, default,
            paths: new Dictionary<HistoryConfigId, MigrationProjectPaths> { [project.ConfigId] = paths });
        if (result.Issues.Length != 0) throw new IOException("Migration import reported: " + JsonSerializer.Serialize(result.Issues));
        if (result.Configuration?.Status == ConfigImportStatus.StagedRequiresRestart)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "migration-stage-result.json"), JsonSerializer.Serialize(new { Success = true, RestartRequired = true }));
            return null;
        }
        return ConfigService.CurrentConfig.BackupConfigs.Single();
    }

    private static async Task<int> RunMigrationRecoveryAsync(BackupConfig config, string root, bool recoverAgain)
    {
        var context = await DataMigrationService.PrepareRecoveryAsync(config, default);
        var storeRoot = NativeArtifactStore.ForConfig(config.DestinationPath, config.Id);
        if (!EncryptionService.HasStoredPassword(config.Id) && await NativeMigrationConsumerSession.RequiresCredentialAsync(config, context.Runtime, default))
        {
            var existed = Directory.Exists(storeRoot);
            var catalogRevision = (await context.Runtime.LocalReplicaCatalogStore.LoadAsync()).Value?.CatalogRevision;
            if (await BackupPasswordPreparationService.VerifyAndStoreAsync(context.Snapshot, "incorrect-test-password"))
                throw new IOException("The wrong migration password was accepted.");
            if (EncryptionService.HasStoredPassword(config.Id) || Directory.Exists(storeRoot) != existed
                || (await context.Runtime.LocalReplicaCatalogStore.LoadAsync()).Value?.CatalogRevision != catalogRevision)
                throw new IOException("A failed candidate credential changed authoritative Native storage.");
            if (!await BackupPasswordPreparationService.VerifyAndStoreAsync(context.Snapshot, "p2-isolated-acceptance-only"))
                throw new IOException("The correct migration password was not verified.");
        }
        var candidates = context.Replicas.Candidates.GroupBy(c => c.RepresentationId).Select(g => g.Single()).ToArray();
        var branches = context.Continuation.Sources.Select(s => new HistoryContinuationSelection(s.SourceId,
            s.SuggestedUpdateId ?? s.Candidates.Single().UpdateId)).ToArray();
        var result = await DataMigrationService.CompleteRecoveryAsync(context, candidates, branches, default);
        if (!result.CredentialReady || result.Replicas.Versions.Any(v => v.Readiness != HistoryReadiness.Ready))
            throw new IOException("Migration did not recover all available Versions: " + JsonSerializer.Serialize(result.Replicas));
        var before = await MigrationBatchRecoveryService.CheckProjectAsync(config);
        var authorized = before.Backup == MigrationCapabilityStatus.Released;
        if (!authorized && !before.CanConfirmBackup) throw new IOException("Validated Native recovery did not establish backup authorization: " + before.BackupReason);
        if (!authorized)
        {
            var ledger = await Plugins.V3.NativeArtifactHostService.Store(config).Ledger.LoadAsync();
            var dependency = ledger.Artifacts.First();
            var payload = Plugins.V3.NativeArtifactHostService.Store(config).PayloadPath(dependency);
            var original = await File.ReadAllBytesAsync(payload); var changed = (byte[])original.Clone(); changed[0] ^= 1;
            try
            {
                await File.WriteAllBytesAsync(payload, changed);
                if ((await MigrationBatchRecoveryService.CheckProjectAsync(config)).CanConfirmBackup)
                    throw new IOException("Changed Native dependencies retained stale recovery authorization.");
            }
            finally { await File.WriteAllBytesAsync(payload, original); }
            var assessment = await MigrationBatchRecoveryService.CheckProjectAsync(config);
            var release = await MigrationBatchRecoveryService.ResumeAsync([new(assessment, true, false)]);
            if (release.Any(r => !string.IsNullOrWhiteSpace(r.Diagnostic))) throw new IOException(JsonSerializer.Serialize(release));
        }
        var history = context.Runtime; var folder = config.SourceFolders.Single();
        var current = (await history.WorkspaceStore.LoadAsync()).Value!.SourceBaselines.Single().BaseVersionId!.Value;
        var selected = (await history.Query.GetAllRepresentationsAsync()).First(r => r.VersionId == current);
        var export = Path.Combine(root, (recoverAgain ? "migration-export-restarted-" : "migration-export-restored-") + Guid.NewGuid().ToString("N"));
        await NativeHistoryApplicationService.ExportVersionAsync(config, folder, current, export);
        await VerifyCapturedTreeAsync(history, selected, folder.Path, export);
        await File.WriteAllTextAsync(Path.Combine(root, recoverAgain ? "migration-restart-result.json" : "migration-import-result.json"),
            JsonSerializer.Serialize(new { Success = true, WrongPasswordRejected = true, VirtualPaths = true, CompleteSourceEvidence = true,
                ChangedDependencyBlocksAuthorization = true, Restart = recoverAgain, Versions = result.Replicas.Versions.Length }));
        return 0;
    }
}
#endif

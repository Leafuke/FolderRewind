using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal enum MigrationCapabilityStatus { AwaitingCheck, AwaitingConfirmation, Released, Blocked }
internal sealed record MigrationProjectAssessment(BackupConfig Snapshot, string Evidence,
    MigrationCapabilityStatus Backup, MigrationCapabilityStatus Cloud, string BackupReason, string CloudReason, bool IsMigrated)
{
    internal bool CanConfirmBackup => Backup == MigrationCapabilityStatus.AwaitingConfirmation;
    internal bool CanConfirmCloud => Cloud == MigrationCapabilityStatus.AwaitingConfirmation;
}
internal sealed record MigrationResumeRequest(MigrationProjectAssessment Assessment, bool Backup, bool Cloud);

internal static class MigrationBatchRecoveryService
{
    internal static bool HasCloudConfiguration(BackupConfig config) => config.Cloud.Enabled
        || !string.IsNullOrWhiteSpace(config.Cloud.ConnectionProfileId) || !string.IsNullOrWhiteSpace(config.Cloud.RcloneConfigPath);

    internal static bool NeedsCloudPermission(BackupConfig config) => HasCloudConfiguration(config)
        && MigrationSetupStateStore.IsCloudPending(ConfigService.ConfigDirectory, config.Id);

    internal static Task<BackupConfig[]> CaptureAsync() => UiDispatcherService.RunOnUiAsync(() =>
        Task.FromResult(ConfigService.CurrentConfig.BackupConfigs.Select(ConfigSnapshotMapper.Copy).ToArray()));

    internal static async Task<IReadOnlyList<MigrationProjectAssessment>> CheckAsync(CancellationToken token = default)
    {
        var configs = await CaptureAsync().ConfigureAwait(false);
        var results = new List<MigrationProjectAssessment>();
        foreach (var config in configs)
        {
            token.ThrowIfCancellationRequested();
            results.Add(await CheckProjectAsync(config, token).ConfigureAwait(false));
        }
        return results;
    }

    internal static Task<MigrationProjectAssessment> CheckProjectAsync(BackupConfig snapshot, CancellationToken token = default)
        => Task.Run(async () =>
        {
            var backupPending = MigrationSetupStateStore.IsBackupPending(ConfigService.ConfigDirectory, snapshot.Id);
            var cloudPending = MigrationSetupStateStore.IsCloudPending(ConfigService.ConfigDirectory, snapshot.Id);
            var isMigrated = backupPending || cloudPending || MigrationSetupStateStore.HasRequirement(ConfigService.ConfigDirectory, snapshot.Id);
            if (!isMigrated) return new MigrationProjectAssessment(snapshot, "", MigrationCapabilityStatus.Released,
                MigrationCapabilityStatus.Released, "", "", false);
            var backupErrors = new List<string>(); var cloudErrors = new List<string>();
            var stamp = new StringBuilder(JsonSerializer.Serialize(snapshot, AppJsonContext.Default.BackupConfig));
            try
            {
                MigrationSetupStateStore.Validate(ConfigService.ConfigDirectory, snapshot.Id);
                backupErrors.AddRange(MigrationProjectSetupService.Check(snapshot));
                var paths = HistoryRepositoryPaths.ForConfigDirectory(ConfigService.ConfigDirectory, new(snapshot.Id));
                if (File.Exists(paths.DescriptorPath))
                {
                    var descriptor = HistoryRepositoryDescriptor.Parse(ReadEvidence(paths.DescriptorPath, stamp));
                    if (descriptor.ConfigId != new HistoryConfigId(snapshot.Id)) throw new InvalidDataException("Repository identity mismatch.");
                    new HistoryPackImportJournalStore(descriptor.ConfigId, paths).RequireRecovered();
                    new HistoryTransactionJournalStore(paths).RequireRecovered();
                    HistoryRestoreTransactionJournalStore.RequireRecovered(paths.TransactionsRoot);
                    History.Retention.HistoryChainRewriteJournalStore.RequireRecovered(paths.LocalStateRoot);
                    var codec = new HistoryPackCodec();
                    var packs = Directory.Exists(paths.PacksRoot) ? Directory.GetFiles(paths.PacksRoot, "*.frpack", SearchOption.AllDirectories) : [];
                    var decoded = new List<HistoryPackReadResult>();
                    foreach (var pack in packs.Order(StringComparer.Ordinal))
                    { token.ThrowIfCancellationRequested(); decoded.Add(codec.Decode(ReadEvidence(pack, stamp))); }
                    new HistoryRepositoryValidator(codec).Validate(descriptor.ConfigId, decoded);
                    using var workspace = new HistoryWorkspaceStore(descriptor.ConfigId, Path.Combine(paths.LocalStateRoot, "workspace.json"));
                    using var catalog = new LocalReplicaCatalogStore(descriptor.ConfigId, Path.Combine(paths.LocalStateRoot, "replicas.json"));
                    var ws = await workspace.LoadAsync(token).ConfigureAwait(false);
                    var replicas = await catalog.LoadAsync(token).ConfigureAwait(false);
                    if (ws.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible
                        || replicas.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible)
                        backupErrors.Add(I18n.GetString("Migration_LocalStateDamaged"));
                    foreach (var name in new[] { "workspace.json", "replicas.json", "migration.json" })
                        ReadEvidence(Path.Combine(paths.LocalStateRoot, name), stamp);
                    var known = decoded.SelectMany(p => p.Pack.Objects).Where(o => o.SchemaVersion == 1 && HistoryObjectKinds.IsKnown(o.Kind))
                        .DistinctBy(o => (o.Kind, o.Id)).Select(codec.DeserializeKnown).ToArray();
                    var native = known.OfType<VersionRepresentation>().Where(NativePluginArtifactBackend.IsSupported).ToArray();
                    if (native.Any(r => replicas.Value?.Entries.Any(e => e.RepresentationId == r.RepresentationId) == true))
                    {
                        var root = Plugin.Runtime.Artifacts.NativeArtifactStore.ForConfig(snapshot.DestinationPath, snapshot.Id);
                        if (!Directory.Exists(root)) throw new IOException("Native recovery storage is missing.");
                        var proof = await NativeArtifactMaterializationEvidence.CaptureAsync(descriptor.ConfigId,
                            Plugins.V3.NativeArtifactHostService.Store(snapshot, BackupPasswordPreparationService.CreateBackend(snapshot)),
                            native, known.OfType<SourceVersion>().ToArray(), replicas.Value?.Entries ?? [], token).ConfigureAwait(false);
                        stamp.Append(proof);
                        var recovered = DataMigrationStateStore.Load(ConfigService.ConfigDirectory, descriptor.ConfigId);
                        if (recovered is not { RecoveryCompleted: true } || recovered.NativeEvidence != proof)
                            backupErrors.Add(I18n.GetString("Migration_CheckExpired"));
                    }
                }
                ReadEvidence(Path.Combine(ConfigService.ConfigDirectory, "migration", "config-import", "setup.json"), stamp);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { backupErrors.Add(CloudCommandSecurity.Redact(error.Message)); }
            try
            {
                var cloudError = HasCloudConfiguration(snapshot) ? CloudSyncService.CheckMigrationConnection(snapshot) : null;
                if (cloudError is not null) cloudErrors.Add(cloudError);
                else if (HasCloudConfiguration(snapshot))
                {
                    var connection = CloudConnectionCatalog.Resolve(snapshot);
                    stamp.Append(JsonSerializer.Serialize(connection, AppJsonContext.Default.CloudSettings));
                    ReadEvidence(connection.RcloneConfigPath, stamp);
                }
            }
            catch (Exception error) { cloudErrors.Add(CloudCommandSecurity.Redact(error.Message)); }
            // Uploads depend on valid local history, but local backup never depends on cloud credentials.
            cloudErrors.AddRange(backupErrors);
            stamp.Append(backupPending).Append(cloudPending).Append(string.Join(";", backupErrors)).Append(string.Join(";", cloudErrors));
            return new MigrationProjectAssessment(snapshot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp.ToString()))),
                Status(backupPending, backupErrors), Status(cloudPending, cloudErrors),
                string.Join(Environment.NewLine, backupErrors.Distinct()), string.Join(Environment.NewLine, cloudErrors.Distinct()), isMigrated);
        }, token);

    private static MigrationCapabilityStatus Status(bool pending, List<string> errors) => errors.Count > 0
        ? MigrationCapabilityStatus.Blocked : !pending ? MigrationCapabilityStatus.Released : MigrationCapabilityStatus.AwaitingConfirmation;
    private static byte[] ReadEvidence(string path, StringBuilder stamp)
    {
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : [];
        stamp.Append(path).Append(':').Append(Convert.ToHexString(SHA256.HashData(bytes))).Append(';');
        return bytes;
    }
    internal static Task GrantIfUnchangedAsync(BackupConfig expected, bool backup, bool cloud)
        => UiDispatcherService.RunOnUiAsync(() =>
        {
            var live = ConfigService.CurrentConfig.BackupConfigs.Single(c => c.Id == expected.Id);
            if (JsonSerializer.Serialize(live, AppJsonContext.Default.BackupConfig) != JsonSerializer.Serialize(expected, AppJsonContext.Default.BackupConfig))
                throw new InvalidOperationException(I18n.GetString("Migration_CheckExpired"));
            // The tiny atomic permission record and final comparison share one dispatcher turn.
            MigrationSetupStateStore.Grant(ConfigService.ConfigDirectory, expected.Id, backup, cloud);
            ConfigService.PublishSaved();
            return Task.CompletedTask;
        });
    internal static Task<IReadOnlyList<MigrationResumeResult>> ResumeAsync(IEnumerable<MigrationResumeRequest> requests, CancellationToken token = default)
        => MigrationBatchConfirmation.RunAsync(requests, request => request.Assessment.Snapshot.Id, async (request, cancellation) =>
        {
            var id = request.Assessment.Snapshot.Id;
            var live = (await CaptureAsync().ConfigureAwait(false)).Single(c => MigrationRecoveryPolicy.SameId(c.Id, id));
            var fresh = await CheckProjectAsync(live, cancellation).ConfigureAwait(false);
            if ((!request.Backup || fresh.Backup == MigrationCapabilityStatus.Released)
                && (!request.Cloud || fresh.Cloud == MigrationCapabilityStatus.Released)) return new MigrationResumeResult(id, false, false, "");
            if (fresh.Evidence != request.Assessment.Evidence) throw new InvalidOperationException(I18n.GetString("Migration_CheckExpired"));
            var backup = request.Backup && fresh.CanConfirmBackup;
            var cloud = request.Cloud && fresh.CanConfirmCloud;
            if (!backup && !cloud) return new MigrationResumeResult(id, false, false, "");
            if (backup || MigrationSetupStateStore.IsBackupPending(ConfigService.ConfigDirectory, id))
                await MigrationProjectSetupService.CompleteAsync(live, live, releaseBackup: backup, releaseCloud: cloud,
                    expectedEvidence: fresh.Evidence, cancellationToken: cancellation).ConfigureAwait(false);
            else
            {
                await using var lease = await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(new(id), cancellation).ConfigureAwait(false);
                var checkedAgain = await CheckProjectAsync((await CaptureAsync().ConfigureAwait(false)).Single(c => c.Id == id), cancellation).ConfigureAwait(false);
                if (checkedAgain.Evidence != fresh.Evidence) throw new InvalidOperationException(I18n.GetString("Migration_CheckExpired"));
                cancellation.ThrowIfCancellationRequested();
                await GrantIfUnchangedAsync(checkedAgain.Snapshot, backup, cloud).ConfigureAwait(false);
            }
            return new MigrationResumeResult(id, backup, cloud, "");
        }, error =>
        {
            LogService.LogError("Migration confirmation failed.", nameof(MigrationBatchRecoveryService), error);
            return CloudCommandSecurity.Redact(error.Message);
        }, token);
}

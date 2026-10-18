using FolderRewind.Models;
using System;
using System.Linq;
using System.Text.Json;
using System.IO;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed class ConfigSettingsDraftService
{
    private BackupConfig _original;
    private string _expected;
    private string _baseline;
    public BackupConfig Draft { get; }
    internal BackupConfig? LastCommittedSnapshot { get; private set; }
    internal BackupConfigurationHandoff? LastCommittedHandoff { get; private set; }
    public ConfigSettingsDraftService(BackupConfig original)
    {
        _original = original;
        _expected = Serialize(original);
        Draft = BackupConfigCloneService.CloneForRuntimeMutation(original, I18n.GetString("Common_Failed"));
        _baseline = Serialize(Draft);
    }
    public bool IsDirty => Serialize(Draft) != _baseline;
    public void AcceptInitialNormalization() => _baseline = Serialize(Draft);
    // A backup updates timestamps in the saved configuration. Rebase only those known
    // runtime facts; unrelated configuration edits must still produce a stale-draft error.
    internal void RefreshBackupTimesAfterOperation()
    {
        var current = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(c => c.Id == _original.Id);
        if (current is null || !ReferenceEquals(current, _original)) return;
        var expected = JsonSerializer.Deserialize(_expected, AppJsonContext.Default.BackupConfig)!;
        var comparison = ConfigSnapshotMapper.Copy(current);
        foreach (var folder in comparison.SourceFolders)
        {
            var prior = expected.SourceFolders.FirstOrDefault(f => f.Id == folder.Id);
            if (prior is not null) folder.LastBackupTime = prior.LastBackupTime;
        }
        if (Serialize(comparison) != _expected) return;
        var baseline = JsonSerializer.Deserialize(_baseline, AppJsonContext.Default.BackupConfig)!;
        foreach (var saved in current.SourceFolders)
        {
            var draftFolder = Draft.SourceFolders.FirstOrDefault(f => f.Id == saved.Id);
            var baselineFolder = baseline.SourceFolders.FirstOrDefault(f => f.Id == saved.Id);
            if (draftFolder is not null) draftFolder.LastBackupTime = saved.LastBackupTime;
            if (baselineFolder is not null) baselineFolder.LastBackupTime = saved.LastBackupTime;
        }
        _expected = Serialize(current);
        _baseline = Serialize(baseline);
    }
    private static string Serialize(BackupConfig config) => JsonSerializer.Serialize(config, AppJsonContext.Default.BackupConfig);
    public Task<ConfigSaveResult> CommitAsync() => CommitAsync(default);
    public async Task<ConfigSaveResult> CommitAsync(System.Threading.CancellationToken token)
    {
        var submitted = BackupConfigCloneService.CloneForRuntimeMutation(Draft, I18n.GetString("Common_Failed"));
#if NATIVE_ARTIFACT_ACCEPTANCE
        await Development.NativeArtifactAcceptance.PauseUiFixtureAsync("settings-save", token);
#endif
        await using var gate = await NativeHistoryConfigurationOperationGate.EnterAsync(_original.Id, token);
        RefreshBackupTimesAfterOperation();
        var configs = ConfigService.CurrentConfig.BackupConfigs;
        var current = configs.FirstOrDefault(c => c.Id == _original.Id);
        if (current is null || !ReferenceEquals(current, _original) || Serialize(current) != _expected)
            return new() { Success = false, ErrorMessage = I18n.GetString("SettingsProject_Stale") };
        if (string.IsNullOrWhiteSpace(submitted.Name) || configs.Any(c => c.Id != submitted.Id && string.Equals(c.Name.Trim(), submitted.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            return new() { Success = false, ErrorMessage = I18n.GetString("Setup_InvalidName") };
        if (string.IsNullOrWhiteSpace(submitted.DestinationPath) || configs.Any(c => c.Id != submitted.Id && string.Equals(
            FolderNameConflictService.NormalizeDestinationPath(c.DestinationPath), FolderNameConflictService.NormalizeDestinationPath(submitted.DestinationPath), StringComparison.OrdinalIgnoreCase)))
            return new() { Success = false, ErrorMessage = I18n.GetString("Setup_InvalidDestination") };
        if (submitted.DestinationPath != current.DestinationPath)
        {
            if (!Path.IsPathFullyQualified(submitted.DestinationPath))
                return new() { Success = false, ErrorMessage = I18n.GetString("Setup_InvalidDestination") };
            foreach (var source in submitted.SourceFolders)
            {
                if (!BackupStoragePathService.TryResolveBackupStoragePaths(submitted.DestinationPath, source.DisplayName, source.Path,
                    out _, out var archivePath, out var metadataPath)
                    || !BackupPathOverlapPolicy.Validate(source.Path, submitted.DestinationPath, archivePath, metadataPath).IsSafe)
                    return new() { Success = false, ErrorMessage = I18n.GetString("Setup_PathOverlap") };
            }
        }
        ConfigMutationProtection.RequireWritable(current);
        foreach (var source in submitted.SourceFolders)
            if (current.SourceFolders.FirstOrDefault(f => f.Id == source.Id) is { } savedSource) source.LastBackupTime = savedSource.LastBackupTime;
        var replacement = BackupConfigCloneService.CloneForRuntimeMutation(submitted, I18n.GetString("Common_Failed"));
        var cloudChanged = replacement.Cloud.Enabled != current.Cloud.Enabled || replacement.Cloud.RemoteBasePath != current.Cloud.RemoteBasePath
            || replacement.Cloud.ConnectionProfileId != current.Cloud.ConnectionProfileId || replacement.Cloud.RcloneConfigPath != current.Cloud.RcloneConfigPath
            || replacement.Cloud.WritableEvidence != current.Cloud.WritableEvidence;
        if (replacement.Cloud.Enabled != current.Cloud.Enabled || replacement.Cloud.RemoteBasePath != current.Cloud.RemoteBasePath
            || replacement.Cloud.ConnectionProfileId != current.Cloud.ConnectionProfileId
            || replacement.Cloud.RcloneConfigPath != current.Cloud.RcloneConfigPath || replacement.Cloud.WritableEvidence != current.Cloud.WritableEvidence)
            await CloudSyncService.CloseCloudAdmissionAsync(current, token);
        var cloudRuntime = cloudChanged ? await History.Application.NativeHistoryCoreGateway.EnsureReadyAsync(current, token) : null;
        await using var cloudGate = cloudRuntime is not null ? await cloudRuntime.MutationGate.EnterAsync(token) : null;
        replacement.ConfigRevision = Guid.NewGuid().ToString("N");
        var index = configs.IndexOf(current);
        var oldProfiles = ConfigService.CurrentConfig.CloudConnections.ToArray();
        await ConfigEditTransaction.ApplyAsync(() =>
        {
            CloudConnectionCatalog.BindLegacy(ConfigService.CurrentConfig, replacement);
            if (cloudRuntime is not null)
            {
                cloudRuntime.CloudJobs.ConfigureAdmission(replacement.Cloud.Enabled && replacement.Cloud.WritableEvidence.Length != 0
                    ? CloudSyncService.FreezeTarget(replacement) : null);
                cloudRuntime.Repository.CloudAdmissionExpected = replacement.Cloud.Enabled && replacement.Cloud.WritableEvidence.Length != 0;
            }
            configs[index] = replacement;
        }, () =>
        {
            configs[index] = current;
            ConfigService.CurrentConfig.CloudConnections.Clear();
            foreach (var profile in oldProfiles) ConfigService.CurrentConfig.CloudConnections.Add(profile);
            if (cloudRuntime is not null)
            {
                cloudRuntime.CloudJobs.ConfigureAdmission(current.Cloud.Enabled && current.Cloud.WritableEvidence.Length != 0 ? CloudSyncService.FreezeTarget(current) : null);
                cloudRuntime.Repository.CloudAdmissionExpected = current.Cloud.Enabled && current.Cloud.WritableEvidence.Length != 0;
            }
        }, () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"));
        _original = replacement;
        _expected = Serialize(replacement);
        Draft.ConfigRevision = replacement.ConfigRevision;
        if (Draft.Cloud.ConnectionProfileId == submitted.Cloud.ConnectionProfileId) Draft.Cloud.ConnectionProfileId = replacement.Cloud.ConnectionProfileId;
        if (Draft.Cloud.RemotePathSuffix == submitted.Cloud.RemotePathSuffix) Draft.Cloud.RemotePathSuffix = replacement.Cloud.RemotePathSuffix;
        if (Draft.Cloud.BindingRevision == submitted.Cloud.BindingRevision) Draft.Cloud.BindingRevision = replacement.Cloud.BindingRevision;
        LastCommittedSnapshot = ConfigSnapshotMapper.Copy(replacement);
        LastCommittedHandoff = replacement.SourceFolders.Count == 0 ? null
            : new(replacement, replacement.Id, replacement.ConfigRevision, replacement.SourceFolders.Select(f => f.Id));
        _baseline = Serialize(LastCommittedSnapshot);
        CloudSyncService.ResumeCloudJobs(replacement);
        return new() { Success = true };
    }
}

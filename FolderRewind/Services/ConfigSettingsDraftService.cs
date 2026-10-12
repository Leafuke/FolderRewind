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
    public ConfigSettingsDraftService(BackupConfig original)
    {
        _original = original;
        _expected = Serialize(original);
        Draft = BackupConfigCloneService.CloneForRuntimeMutation(original, I18n.GetString("Common_Failed"));
        _baseline = Serialize(Draft);
    }
    public bool IsDirty => Serialize(Draft) != _baseline;
    public void AcceptInitialNormalization() => _baseline = Serialize(Draft);
    private static string Serialize(BackupConfig config) => JsonSerializer.Serialize(config, AppJsonContext.Default.BackupConfig);
    public Task<ConfigSaveResult> CommitAsync() => CommitAsync(default);
    public async Task<ConfigSaveResult> CommitAsync(System.Threading.CancellationToken token)
    {
        await using var gate = await NativeHistoryConfigurationOperationGate.EnterAsync(_original.Id, token);
        var configs = ConfigService.CurrentConfig.BackupConfigs;
        var current = configs.FirstOrDefault(c => c.Id == _original.Id);
        if (current is null || !ReferenceEquals(current, _original) || Serialize(current) != _expected)
            return new() { Success = false, ErrorMessage = I18n.GetString("SettingsProject_Stale") };
        if (string.IsNullOrWhiteSpace(Draft.Name) || configs.Any(c => c.Id != Draft.Id && string.Equals(c.Name.Trim(), Draft.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            return new() { Success = false, ErrorMessage = I18n.GetString("Setup_InvalidName") };
        if (string.IsNullOrWhiteSpace(Draft.DestinationPath) || configs.Any(c => c.Id != Draft.Id && string.Equals(
            FolderNameConflictService.NormalizeDestinationPath(c.DestinationPath), FolderNameConflictService.NormalizeDestinationPath(Draft.DestinationPath), StringComparison.OrdinalIgnoreCase)))
            return new() { Success = false, ErrorMessage = I18n.GetString("Setup_InvalidDestination") };
        if (Draft.DestinationPath != current.DestinationPath)
        {
            if (!Path.IsPathFullyQualified(Draft.DestinationPath))
                return new() { Success = false, ErrorMessage = I18n.GetString("Setup_InvalidDestination") };
            foreach (var source in Draft.SourceFolders)
            {
                if (!BackupStoragePathService.TryResolveBackupStoragePaths(Draft.DestinationPath, source.DisplayName, source.Path,
                    out _, out var archivePath, out var metadataPath)
                    || !BackupPathOverlapPolicy.Validate(source.Path, Draft.DestinationPath, archivePath, metadataPath).IsSafe)
                    return new() { Success = false, ErrorMessage = I18n.GetString("Setup_PathOverlap") };
            }
        }
        ConfigMutationProtection.RequireWritable(current);
        var replacement = BackupConfigCloneService.CloneForRuntimeMutation(Draft, I18n.GetString("Common_Failed"));
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
        Draft.Cloud.ConnectionProfileId = replacement.Cloud.ConnectionProfileId;
        Draft.Cloud.RemotePathSuffix = replacement.Cloud.RemotePathSuffix;
        Draft.Cloud.BindingRevision = replacement.Cloud.BindingRevision;
        _baseline = Serialize(Draft);
        CloudSyncService.ResumeCloudJobs(replacement);
        return new() { Success = true };
    }
}

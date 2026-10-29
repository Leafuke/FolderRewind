using FolderRewind.Models;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed class CloudSetupPersistence(Action? validateProfileCommit = null) : ICloudSetupPersistence
{
    public CloudConnectionProfile? FindProfile(string id) => ConfigService.CurrentConfig.CloudConnections.SingleOrDefault(p => p.Id == id);
    public async Task ConfirmPersistenceAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = await ConfigService.SaveAsync();
        if (!result.Success) throw new InvalidOperationException(result.ErrorMessage, result.Exception);
    }
    public Task SaveProfileAsync(CloudConnectionProfile profile, CloudConnectionProfile? expected, CancellationToken token)
        => CloudConnectionCatalog.SaveProfileAsync(profile, expected?.ProfileRevision, token, expected, validateProfileCommit);
    public async Task BindProjectAsync(CloudSetupSaveRequest request, CancellationToken token)
    {
        var current = ConfigService.CurrentConfig.BackupConfigs.SingleOrDefault(p => p.Id == request.ProjectId)
            ?? throw new InvalidOperationException(I18n.GetString("SettingsProject_Stale"));
        var profile = request.Profile;
        var draft = new ConfigSettingsDraftService(current);
        var cloud = draft.Draft.Cloud;
        var changed = cloud.ConnectionProfileId != profile.Id || cloud.RemoteBasePath != request.RemoteRoot
            || cloud.RcloneConfigPath != profile.CredentialReference;
        if (changed && current.Cloud.ConnectionProfileId is not null && request.ReadableEvidence.Length == 0 && request.WritableEvidence.Length == 0)
            throw new InvalidOperationException(I18n.GetString("CloudWizard_VerificationRequired"));
        cloud.ExecutablePath = profile.ExecutablePath; cloud.WorkingDirectory = profile.WorkingDirectory;
        cloud.RcloneConfigPath = profile.CredentialReference; cloud.RemoteBasePath = request.RemoteRoot;
        cloud.CommandMode = CloudCommandMode.Rclone; cloud.LegacyConversionRequired = false;
        cloud.ReadableEvidence = request.ReadableEvidence; cloud.WritableEvidence = request.WritableEvidence;
        cloud.ConnectionProfileId = profile.Id; cloud.RemotePathSuffix = CloudRemotePath.Split(request.RemoteRoot).Suffix;
        if (changed || request.WritableEvidence.Length == 0) cloud.Enabled = false;
        if (!draft.IsDirty) return;
        if (request.ExpectedProjectRevision is not null && current.ConfigRevision != request.ExpectedProjectRevision)
            throw new InvalidOperationException(I18n.GetString("SettingsProject_Stale"));
        var result = await draft.CommitAsync(token);
        if (!result.Success) throw new InvalidOperationException(result.ErrorMessage, result.Exception);
        ConfigService.ConfirmRcloneCredentialPath(profile.CredentialReference);
    }
}

using FolderRewind.Models;
using System;
using System.IO;
using System.Linq;

namespace FolderRewind.Services;

internal static class CloudConnectionCatalog
{
    public static void MigrateLegacy(AppConfig config)
    {
        config.CloudConnections ??= new();
        foreach (var project in config.BackupConfigs)
        {
            var cloud = project.Cloud;
            if (cloud.CommandMode == CloudCommandMode.Custom)
            {
                cloud.LegacyConversionRequired = true;
                cloud.Enabled = false;
                continue; // 保留原命令用于安全副本和用户明确转换。
            }
            if (cloud.ConnectionProfileId is not null || string.IsNullOrEmpty(cloud.RcloneConfigPath)) continue;
            try { BindLegacy(config, project); }
            catch (InvalidDataException) { cloud.Enabled = false; }
        }
    }

    // 编辑旧字段仅修改本项目绑定；共享档案的认证/目标变更必须从连接管理入口进行。
    public static void BindLegacy(AppConfig app, BackupConfig project)
    {
        var cloud = project.Cloud;
        if (cloud.CommandMode != CloudCommandMode.Rclone) return;
        var (remote, suffix) = CloudRemotePath.Split(cloud.RemoteBasePath);
        var existing = app.CloudConnections.SingleOrDefault(p => p.Id == cloud.ConnectionProfileId);
        var sameConnection = existing is not null && existing.RemoteName == remote
            && existing.CredentialReference == cloud.RcloneConfigPath
            && existing.ExecutablePath == cloud.ExecutablePath && existing.WorkingDirectory == cloud.WorkingDirectory;
        if (sameConnection && cloud.RemotePathSuffix == suffix && cloud.BindingRevision.Length != 0) return;
        if (!sameConnection)
        {
            if (string.IsNullOrEmpty(cloud.RcloneConfigPath)) return;
            existing = new CloudConnectionProfile
            {
                DisplayName = project.Name, OwnerProjectId = project.Id, Scope = CloudConnectionScope.Project,
                RemoteName = remote, CredentialReference = cloud.RcloneConfigPath,
                ExecutablePath = cloud.ExecutablePath, WorkingDirectory = cloud.WorkingDirectory,
                CredentialOwnership = IsManaged(cloud.RcloneConfigPath) ? CloudCredentialOwnership.ManagedCopy : CloudCredentialOwnership.ExternalReference,
                Access = CloudConnectionAccess.ConnectedUnverified
            };
            app.CloudConnections.Add(existing);
        }
        cloud.ConnectionProfileId = existing!.Id;
        cloud.RemotePathSuffix = suffix;
        cloud.BindingRevision = Guid.NewGuid().ToString("N");
    }

    public static bool IsManaged(string path) => Path.IsPathFullyQualified(path)
        && Path.GetFullPath(path).StartsWith(Path.GetFullPath(ConfigService.RcloneConnectionsDirectory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static CloudSettings Resolve(BackupConfig project)
    {
        var input = project.Cloud;
        if (input.LegacyConversionRequired || input.CommandMode != CloudCommandMode.Rclone)
            throw RcloneConnectionDiagnostics.InvalidFailure("connection.legacy-conversion-required", "CloudConnection_LegacyConversionRequired");
        if (input.ConnectionProfileId is null) return ConfigSnapshotMapper.Copy(input);
        var profile = ConfigService.CurrentConfig.CloudConnections.SingleOrDefault(p => p.Id == input.ConnectionProfileId)
            ?? throw RcloneConnectionDiagnostics.InvalidFailure("connection.credentials-missing", "Migration_CloudBindingRequired");
        var resolved = CloudConnectionResolver.Resolve(profile, project.Id, input.RemotePathSuffix, input.BindingRevision);
        var copy = ConfigSnapshotMapper.Copy(input);
        copy.RemoteBasePath = resolved.RemoteRoot; copy.RcloneConfigPath = resolved.CredentialReference;
        copy.ExecutablePath = resolved.ExecutablePath; copy.WorkingDirectory = resolved.WorkingDirectory;
        return copy;
    }
}

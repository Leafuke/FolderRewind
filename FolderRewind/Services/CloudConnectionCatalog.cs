using FolderRewind.Models;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using FolderRewind.History.Cloud;

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

    internal static bool SameCredentialStorage(string first, string second)
        => Path.IsPathFullyQualified(first) && Path.IsPathFullyQualified(second)
            && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(first), Path.GetFullPath(second));

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
        // A shared profile can carry a directory probe already confirmed by the user.
        // Transfers still compare this evidence with the current credential target and exact root.
        if (copy.ReadableEvidence.Length == 0)
            copy.ReadableEvidence = CloudConnectionResolver.VerifiedAccessEvidence(profile, resolved.RemoteRoot, writable: false);
        if (copy.WritableEvidence.Length == 0)
            copy.WritableEvidence = CloudConnectionResolver.VerifiedAccessEvidence(profile, resolved.RemoteRoot, writable: true);
        return copy;
    }

    internal static async Task SaveProfileAsync(CloudConnectionProfile profile, string? expectedRevision, CancellationToken token)
    {
        var app = ConfigService.CurrentConfig;
        var existing = app.CloudConnections.SingleOrDefault(p => p.Id == profile.Id);
        if (existing is not null && existing.ProfileRevision != expectedRevision) throw new InvalidOperationException(I18n.GetString("SettingsProject_Stale"));
        var projects = app.BackupConfigs.Where(p => p.Cloud.ConnectionProfileId == profile.Id).ToArray();
        var leases = new System.Collections.Generic.List<NativeHistoryConfigurationOperationGate.Lease>();
        try
        {
            // 与本地备份接纳及项目编辑共用门闩；固定顺序避免多个共享档案编辑形成环路。
            foreach (var project in projects.OrderBy(p => p.Id, StringComparer.Ordinal))
                leases.Add(await NativeHistoryConfigurationOperationGate.EnterAsync(project.Id, token));
            if (app.CloudConnections.SingleOrDefault(p => p.Id == profile.Id) != existing
                || app.BackupConfigs.Where(p => p.Cloud.ConnectionProfileId == profile.Id).Except(projects).Any())
                throw new InvalidOperationException(I18n.GetString("SettingsProject_Stale"));
            foreach (var project in projects) await CloudSyncService.CloseCloudAdmissionAsync(project, token);
            var originals = projects.Select(p => ConfigSnapshotMapper.Copy(p.Cloud)).ToArray();
            var index = existing is null ? app.CloudConnections.Count : app.CloudConnections.IndexOf(existing);
            await ConfigEditTransaction.ApplyAsync(() =>
            {
                if (existing is null) app.CloudConnections.Add(profile); else app.CloudConnections[index] = profile;
                foreach (var project in projects)
                {
                    // 兼容字段随权威档案更新，避免随后编辑项目时把旧字段误转换成另一连接。
                    project.Cloud.RcloneConfigPath = profile.CredentialReference;
                    project.Cloud.ExecutablePath = profile.ExecutablePath;
                    project.Cloud.WorkingDirectory = profile.WorkingDirectory;
                    project.Cloud.RemoteBasePath = profile.RemoteName + ":" + project.Cloud.RemotePathSuffix;
                    if (existing?.ProfileRevision != profile.ProfileRevision)
                    {
                        project.Cloud.Enabled = false;
                        project.Cloud.ReadableEvidence = project.Cloud.WritableEvidence = "";
                    }
                }
            }, () =>
            {
                if (existing is null) app.CloudConnections.Remove(profile); else app.CloudConnections[index] = existing;
                for (var i = 0; i < projects.Length; i++) projects[i].Cloud = originals[i];
            }, () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"));
        }
        finally
        {
            for (var i = leases.Count - 1; i >= 0; i--) await leases[i].DisposeAsync();
        }
        foreach (var project in projects) CloudSyncService.ResumeCloudJobs(project);
    }

    internal static async Task DeleteProfileAsync(string id, CancellationToken token)
    {
        var app = ConfigService.CurrentConfig; var profile = app.CloudConnections.Single(p => p.Id == id);
        void RequireUnreferenced()
        {
            if (app.BackupConfigs.Any(p => p.Cloud.ConnectionProfileId == id) || RcloneExecutionContext.HasActiveCredential(profile.CredentialReference)
                || RcloneCredentialRefresh.HasPendingEvidence(ConfigService.RcloneTasksDirectory, profile.CredentialReference))
                throw new InvalidOperationException(I18n.GetString("CloudConnections_Referenced"));
            var root = Path.Combine(ConfigService.ConfigDirectory, "history");
            if (!Directory.Exists(root)) return;
            var directories = Directory.EnumerateDirectories(root).Take(1001).ToArray();
            if (directories.Length > 1000) throw new InvalidOperationException(I18n.GetString("CloudConnections_Referenced"));
            foreach (var directory in directories)
            {
                var path = Path.Combine(directory, "local-state", "cloud-jobs.json");
                if (File.Exists(path + ".attention") || !File.Exists(path) && File.Exists(path + ".required"))
                    throw new InvalidOperationException(I18n.GetString("CloudConnections_Referenced"));
                if (!File.Exists(path)) continue;
                RcloneCredentialRefresh.RejectReparsePoints(path);
                if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException(I18n.GetString("CloudConnections_Referenced"));
                var journal = JsonSerializer.Deserialize(File.ReadAllBytes(path), CloudJobJsonContext.Default.HistoryCloudJournalDocument);
                if (journal is null || journal.Jobs.Any(j => j.Target.ProfileId == id && j.Protection == CloudProtectionDisposition.Held))
                    throw new InvalidOperationException(I18n.GetString("CloudConnections_Referenced"));
            }
        }
        await Task.Run(RequireUnreferenced, token);
        await ConfigEditTransaction.ApplyAsync(() =>
        {
            if (app.BackupConfigs.Any(p => p.Cloud.ConnectionProfileId == id)) throw new InvalidOperationException(I18n.GetString("CloudConnections_Referenced"));
            app.CloudConnections.Remove(profile);
        }, () => { if (!app.CloudConnections.Contains(profile)) app.CloudConnections.Add(profile); }, () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"));
        // 档案删除不删除凭据、刷新证据或任何远端对象。
    }
}

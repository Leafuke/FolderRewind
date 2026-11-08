#if NATIVE_ARTIFACT_ACCEPTANCE
using FolderRewind.Models;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace FolderRewind.Services.Development;

internal static partial class NativeArtifactAcceptance
{
    private static async Task<int> RunRedundancyCloudAsync(Request request, BackupConfig config, string root)
    {
        var credential = Path.Combine(root, "legacy-credentials", "connection-" + Guid.NewGuid().ToString("N"), "rclone.conf");
        Directory.CreateDirectory(Path.GetDirectoryName(credential)!);
        await File.WriteAllTextAsync(credential, "[acceptance]\ntype = local\n");
        var legacy = new AppConfig();
        var old = new BackupConfig { Name = "legacy", Cloud = new() { RcloneConfigPath = credential, RemoteBasePath = "acceptance:/retained/suffix" } };
        legacy.BackupConfigs.Add(old);
        CloudConnectionCatalog.MigrateLegacy(legacy);
        Require(legacy.CloudConnections.Count == 1 && old.Cloud.ConnectionProfileId == legacy.CloudConnections[0].Id, "legacy binding");
        Require(old.Cloud.RcloneConfigPath.Length == 0 && old.Cloud.RemotePathSuffix == "/retained/suffix", "inline migration boundary");
        var custom = new BackupConfig { Cloud = new() { Enabled = true, CommandMode = CloudCommandMode.Custom, ArgumentsTemplate = "echo fixture" } };
        legacy.BackupConfigs.Add(custom); CloudConnectionCatalog.MigrateLegacy(legacy);
        Require(!custom.Cloud.Enabled && custom.Cloud.LegacyConversionRequired && legacy.CloudConnections.Count == 1, "custom disabled without inferred connection");

        config = await ConfigureCloudFixtureAsync(request, config, root, "authority-remote");
        var profile = ConfigService.CurrentConfig.CloudConnections.Single(p => p.Id == config.Cloud.ConnectionProfileId);
        var profileCount = ConfigService.CurrentConfig.CloudConnections.Count;
        var draft = new ConfigSettingsDraftService(config);
        draft.Draft.Name += " authority";
        draft.Draft.Cloud.RcloneConfigPath = Path.Combine(root, "wrong.conf");
        draft.Draft.Cloud.RemoteBasePath = "wrong:location";
        if (!(await draft.CommitAsync()).Success) throw new IOException("Ordinary project edit failed.");
        config = ConfigService.CurrentConfig.BackupConfigs.Single();
        var resolved = CloudConnectionCatalog.Resolve(config);
        Require(ConfigService.CurrentConfig.CloudConnections.Count == profileCount && resolved.RcloneConfigPath == profile.CredentialReference
            && resolved.RemoteBasePath == "acceptance:authority-remote" && config.Cloud.RcloneConfigPath.Length == 0, "ordinary edits use authoritative profile");
        Require(CloudSyncService.FreezeTarget(config).ProfileId == profile.Id, "job snapshot uses authoritative profile");
        var edited = CloudConnectionEditPolicy.Copy(profile);
        edited.DisplayName = "edited connection";
        await CloudConnectionCatalog.SaveProfileAsync(edited, profile.ProfileRevision, default, profile);
        try
        {
            await CloudConnectionCatalog.SaveProfileAsync(profile, "stale-revision", default);
            throw new IOException("Stale profile edit was accepted.");
        }
        catch (InvalidOperationException) { }
        var canceled = CloudConnectionEditPolicy.Copy(edited); canceled.DisplayName = "canceled connection";
        try
        {
            await CloudConnectionCatalog.SaveProfileAsync(canceled, edited.ProfileRevision, new(true), edited);
            throw new IOException("Canceled profile edit was accepted.");
        }
        catch (OperationCanceledException) { }
        Require(ConfigService.CurrentConfig.CloudConnections.Single(p => p.Id == edited.Id).DisplayName == "edited connection", "failed edits preserve profile");

        var migrationProfile = legacy.CloudConnections.Single();
        var saved = await ConfigService.UpdateAndSaveAsync(c => c.CloudConnections.Add(migrationProfile));
        if (!saved.Success) throw new IOException(saved.ErrorMessage);
        var target = Path.Combine(ConfigService.RcloneConnectionsDirectory, "connection-" + Guid.NewGuid().ToString("N"), "rclone.conf");
        var before = SHA256.HashData(await File.ReadAllBytesAsync(credential));
        await ConfigService.MigrateRedundancyCredentialFixtureAsync(migrationProfile, credential, target, default);
        Require(migrationProfile.CredentialReference == target && migrationProfile.CredentialOwnership == CloudCredentialOwnership.ManagedCopy, "profile credential migration owns target");
        var afterSource = SHA256.HashData(await File.ReadAllBytesAsync(credential));
        var afterTarget = SHA256.HashData(await File.ReadAllBytesAsync(target));
        Require(before.SequenceEqual(afterSource) && before.SequenceEqual(afterTarget), "credential migration preserves original bytes");
        await File.WriteAllTextAsync(Path.Combine(root, "redundancy-cloud-result.json"), JsonSerializer.Serialize(new
        { Success = true, LegacyBinding = true, CustomDisabled = true, AuthoritativeProfile = true, FrozenTarget = true,
            StaleRejected = true, CancelAtomic = true, CredentialOwnership = true, OriginalCredentialUnchanged = true }));
        return 0;
        static void Require(bool condition, string check) { if (!condition) throw new IOException("Cloud authority regression: " + check); }
    }
}
#endif

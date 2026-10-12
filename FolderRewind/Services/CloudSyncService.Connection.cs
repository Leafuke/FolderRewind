using FolderRewind.Models;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static partial class CloudSyncService
{
    internal static string? CheckMigrationConnection(BackupConfig config)
    {
        var settings = config.Cloud;
        if (settings.CommandMode != CloudCommandMode.Rclone || !Path.IsPathFullyQualified(settings.RcloneConfigPath)
            || !File.Exists(settings.RcloneConfigPath)) return I18n.GetString("Migration_CloudBindingRequired");
        if (!TryResolveSharedRcloneRuntime(settings, out _, out _, out var error)) return error;
        var content = System.Text.Encoding.UTF8.GetString(RcloneExecutionContext.ReadConfigurationBytes(settings.RcloneConfigPath));
        var remote = settings.RemoteBasePath.Split(':', 2)[0];
        if (!settings.RemoteBasePath.Contains(':') || string.IsNullOrWhiteSpace(remote)
            || remote.IndexOfAny(['[', ']', '\r', '\n', '/', '\\']) >= 0
            || !System.Linq.Enumerable.Any(content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries), line => line.Trim() == "[" + remote + "]"))
            return I18n.GetString("Migration_CloudBindingRequired");
        return null;
    }

    private static void RequireMigrationCloudPermission(BackupConfig config)
    {
        if (MigrationSetupStateStore.IsCloudPending(ConfigService.ConfigDirectory, config.Id))
            throw new InvalidOperationException(I18n.GetString("Migration_CloudPaused"));
        if (MigrationSetupStateStore.HasRequirement(ConfigService.ConfigDirectory, config.Id) && CheckMigrationConnection(config) is { } error)
            throw new InvalidOperationException(error);
    }

    // Await before freezing settings; construct the AsyncLocal scope in the caller.
    private static async Task<BackupConfig> PrepareConnectionAsync(BackupConfig config, CancellationToken token = default)
    {
        var resolved = CloudConnectionCatalog.Resolve(config);
        config = BackupConfigCloneService.CloneForRuntimeMutation(config, I18n.GetString("Common_Failed"));
        config.Cloud = resolved;
        var originalPath = config.Cloud.RcloneConfigPath;
        var path = await ConfigService.WaitForRcloneCredentialMigrationAsync(originalPath, token).ConfigureAwait(false);
        var copy = BackupConfigCloneService.CloneForRuntimeMutation(config, I18n.GetString("Common_Failed"));
        if (copy.Cloud.RcloneConfigPath == originalPath) copy.Cloud.RcloneConfigPath = path;
        return copy;
    }

    private static async Task<CloudSettings> PrepareTransferConnectionAsync(CloudSettings settings)
    {
        var originalPath = settings.RcloneConfigPath;
        var path = await ConfigService.WaitForRcloneCredentialMigrationAsync(originalPath).ConfigureAwait(false);
        var copy = FreezeGlobalTransferConnection(settings);
        if (copy.RcloneConfigPath == originalPath) copy.RcloneConfigPath = path;
        return copy;
    }

    private static RcloneExecutionScope CaptureConnection(BackupConfig config)
        => CaptureConnection(config.Cloud);
    private static RcloneExecutionScope CaptureConnection(CloudSettings settings)
    {
        if (settings.CommandMode != CloudCommandMode.Rclone || string.IsNullOrWhiteSpace(settings.RcloneConfigPath))
            return new(null, owns: false);
        if (RcloneExecutionScope.Current is { } current)
        {
            if (current.ConfigPath != Path.GetFullPath(settings.RcloneConfigPath) || current.RemoteRoot != settings.RemoteBasePath)
                throw new InvalidOperationException("The operation attempted to use a different connection.");
            current.RequireUnchanged();
            return new(current, owns: false);
        }
        return new(CreateBoundConnection(settings));
    }
    private static RcloneExecutionContext? CreateBoundConnection(BackupConfig config)
        => CreateBoundConnection(config.Cloud);
    private static RcloneExecutionContext? CreateBoundConnection(CloudSettings settings)
    {
        if (settings.CommandMode != CloudCommandMode.Rclone || string.IsNullOrWhiteSpace(settings.RcloneConfigPath)) return null;
        if (!TryResolveSharedRcloneRuntime(settings, out var executable, out var working, out var error)) throw new IOException(error);
        return new(executable, settings.RcloneConfigPath, working, settings.RemoteBasePath,
            ConfigService.RcloneTasksDirectory);
    }
}

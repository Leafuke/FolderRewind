using FolderRewind.Models;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static partial class CloudSyncService
{
    // Await before freezing settings; construct the AsyncLocal scope in the caller.
    private static async Task<BackupConfig> PrepareConnectionAsync(BackupConfig config, CancellationToken token = default)
    {
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

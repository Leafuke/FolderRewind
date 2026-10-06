using FolderRewind.Models;
using System;
using System.IO;

namespace FolderRewind.Services;

public static partial class CloudSyncService
{
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
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderRewind", "credentials", "rclone-tasks"));
    }
}

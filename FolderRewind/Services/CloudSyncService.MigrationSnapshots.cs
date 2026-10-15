using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static partial class CloudSyncService
{
    internal static async Task<MigrationCloudSnapshot> ExportMigrationSnapshotAsync(CloudSettings input, string path, CancellationToken token = default)
    {
        var settings = await PrepareTransferConnectionAsync(input).ConfigureAwait(false);
        using var scope = CaptureConnection(settings);
        return await new RcloneMigrationSnapshotTransport(RcloneExecutionScope.Current!, settings.TimeoutSeconds).UploadAsync(path, token).ConfigureAwait(false);
    }
    internal static async Task<IReadOnlyList<MigrationCloudSnapshot>> ListMigrationSnapshotsAsync(CloudSettings input, CancellationToken token = default)
    {
        var settings = await PrepareTransferConnectionAsync(input).ConfigureAwait(false);
        using var scope = CaptureConnection(settings);
        return await new RcloneMigrationSnapshotTransport(RcloneExecutionScope.Current!, settings.TimeoutSeconds).ListAsync(token).ConfigureAwait(false);
    }
    internal static async Task DownloadMigrationSnapshotAsync(CloudSettings input, MigrationCloudSnapshot snapshot, string path, CancellationToken token = default)
    {
        var settings = await PrepareTransferConnectionAsync(input).ConfigureAwait(false);
        using var scope = CaptureConnection(settings);
        await new RcloneMigrationSnapshotTransport(RcloneExecutionScope.Current!, settings.TimeoutSeconds).DownloadAsync(snapshot, path, token).ConfigureAwait(false);
    }

    internal static async Task DownloadLegacyConfigurationExportAsync(CloudSettings input, string name, string destination, CancellationToken token)
    {
        if (name != "config.json" && (!name.StartsWith("config-", StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal)
            || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '.')))
            throw new InvalidDataException(I18n.GetString("CloudConfig_InvalidExport"));
        var settings = await PrepareTransferConnectionAsync(input).ConfigureAwait(false);
        using var scope = CaptureConnection(settings);
        var context = RcloneExecutionScope.Current!;
        var remote = CloudRemotePath.Append(context.RemoteRoot, name == "config.json" ? name : "_folderrewind/config-exports/" + name);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            await RcloneConnectionService.RunAsync(context.CreateStartInfo(["copyto", remote, temporary, "--max-size", "64M"]),
                TimeSpan.FromSeconds(settings.TimeoutSeconds), token).ConfigureAwait(false);
            if (!File.Exists(temporary) || new FileInfo(temporary).Length > 64L * 1024 * 1024) throw new InvalidDataException(I18n.GetString("Migration_ErrorData"));
            token.ThrowIfCancellationRequested(); File.Move(temporary, destination, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

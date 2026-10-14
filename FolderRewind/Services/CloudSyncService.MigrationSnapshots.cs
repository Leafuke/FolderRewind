using FolderRewind.Models;
using System.Collections.Generic;
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
}

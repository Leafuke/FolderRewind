using FolderRewind.History.Application;
using FolderRewind.History.LocalState;
using FolderRewind.Models;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static partial class NativeHistoryApplicationService
{
    internal static async Task<HistoryWorkspaceRepairPreview> PreviewWorkspaceRepairAsync(BackupConfig config, CancellationToken token)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadableAsync(config, token).ConfigureAwait(false);
        return await new HistoryWorkspaceRepairService(runtime).PreviewAsync(config.SourceFolders.Select(Source).ToArray(),
            NativeHistoryConfigLease.Signature(config), token).ConfigureAwait(false);
    }

    internal static async Task<HistoryWorkspaceRepairResult> RepairWorkspaceAsync(BackupConfig config, HistoryWorkspaceRepairPreview preview, CancellationToken token)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadableAsync(config, token).ConfigureAwait(false);
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(runtime.ConfigId, token).ConfigureAwait(false);
        await using var guard = await NativeHistoryConfigLease.EnterInsideOperationAsync(config, preview.ConfigurationSignature, operation, token).ConfigureAwait(false);
        return await new HistoryWorkspaceRepairService(runtime).RepairInsideOperationAsync(preview,
            config.SourceFolders.Select(Source).ToArray(), NativeHistoryConfigLease.Signature(config), operation, token).ConfigureAwait(false);
    }
}

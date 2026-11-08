using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services.KnotLink;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Plugins.V3;

internal static class PluginV3CoordinationRecoveryService
{
    private static readonly PluginId MineRewind = new("com.folderrewind.minerewind");
    internal static async Task<BackupCoordinationStatus?> GetStatusAsync(CancellationToken token)
    {
        using var lease = PluginV3RuntimeService.Runtime.TryAcquire<IBackupRunCoordinationCapability>(MineRewind, token);
        if (lease is null) return null;
        var status = await lease.Capability.GetStatusAsync(lease.Context).ConfigureAwait(false);
        var state = status.State == BackupCoordinationState.Busy || KnotLinkCoordinationChannel.Gate.IsBusy
            ? BackupCoordinationState.Busy : KnotLinkCoordinationChannel.Gate.NeedsReset ? BackupCoordinationState.NeedsReset : status.State;
        return status with { State = state, Diagnostics = state == status.State ? status.Diagnostics : status.Diagnostics.Append(new PluginDiagnostic(
            state == BackupCoordinationState.Busy ? "minerewind.coordination_busy" : "minerewind.coordination_needs_reset",
            state == BackupCoordinationState.Busy ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning,
            "BackupConsistency", "folderrewind.core", new Dictionary<string, string> { ["reason"] = "host_channel_or_terminal_dispatch" })).ToArray() };
    }

    internal static async Task<bool> ResetAsync(CancellationToken token)
    {
        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(token);
        cleanup.CancelAfter(TimeSpan.FromSeconds(5));
        using var lease = PluginV3RuntimeService.Runtime.TryAcquire<IBackupRunCoordinationCapability>(MineRewind, cleanup.Token);
        if (lease is null) return false;
        if ((await lease.Capability.GetStatusAsync(lease.Context).ConfigureAwait(false)).State == BackupCoordinationState.Busy) return false;
        return await KnotLinkCoordinationChannel.Gate.ResetAsync(cleanup.Token, async ct =>
        {
            var context = lease.Context with { OperationCancellation = ct };
            var status = await lease.Capability.GetStatusAsync(context).ConfigureAwait(false);
            if (status.State == BackupCoordinationState.Busy) return false;
            if (!KnotLinkCoordinationChannel.Gate.NeedsReset && status.State != BackupCoordinationState.NeedsReset) return false;
            return (await lease.Capability.ResetAsync(context).ConfigureAwait(false)).Outcome == OperationOutcome.Success;
        }).ConfigureAwait(false);
    }
}

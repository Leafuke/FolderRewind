using FolderRewind.History.Application;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Operations;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed class NativeHistoryRestoreOrchestrator
{
    public static bool IsCoordinatorAvailable(BackupConfig config, out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(config);
        diagnostic = string.Empty;
        var kind = PluginV3ModelMapper.ToKind(config);
        if (StringComparer.Ordinal.Equals(kind.OwnerId.Value, "folderrewind.core")) return true;

        var pluginId = new PluginId(kind.OwnerId.Value);
        var declaration = PluginV3RuntimeService.FindKind(kind)
            ?? PluginV3PackageService.GetInstalledConfigKinds()
                .Where(pair => pair.PluginId == pluginId)
                .Select(pair => pair.Kind)
                .SingleOrDefault(item => item.Kind == kind);
        if (declaration?.RestoreCoordination != RestoreCoordinationPolicy.Required) return true;

        using var lease = PluginV3RuntimeService.Runtime.TryAcquire<IRestoreCoordinatorCapability>(
            pluginId,
            capability => capability.Kind == kind,
            CancellationToken.None);
        if (lease is not null) return true;
        diagnostic = "Required RestoreCoordinator is unavailable.";
        return false;
    }

    public async Task<HistoryRestoreResult> ExecuteAsync(
        BackupConfig config,
        IReadOnlyList<ManagedFolder> affectedFolders,
        string targetIdentity,
        Func<CancellationToken, Task<HistoryRestoreResult>> hostMutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(affectedFolders);
        ArgumentNullException.ThrowIfNull(hostMutation);
        if (affectedFolders.Count == 0)
            return Blocked("Restore has no affected Source binding.");

        var kind = PluginV3ModelMapper.ToKind(config);
        if (StringComparer.Ordinal.Equals(kind.OwnerId.Value, "folderrewind.core"))
            return await hostMutation(cancellationToken).ConfigureAwait(false);

        var pluginId = new PluginId(kind.OwnerId.Value);
        var declaration = PluginV3RuntimeService.FindKind(kind)
            ?? PluginV3PackageService.GetInstalledConfigKinds()
                .Where(pair => pair.PluginId == pluginId)
                .Select(pair => pair.Kind)
                .SingleOrDefault(item => item.Kind == kind);
        var required = declaration?.RestoreCoordination == RestoreCoordinationPolicy.Required;
        using var lease = PluginV3RuntimeService.Runtime.TryAcquire<IRestoreCoordinatorCapability>(
            pluginId,
            capability => capability.Kind == kind,
            cancellationToken);
        if (lease is null)
        {
            return required
                ? Blocked("Required RestoreCoordinator is unavailable.")
                : await hostMutation(cancellationToken).ConfigureAwait(false);
        }

        var configSnapshot = PluginV3ModelMapper.ToSnapshot(config);
        var firstFolderId = Guid.Parse(affectedFolders[0].Id);
        var folderSnapshot = configSnapshot.Folders.Single(item => item.FolderId == firstFolderId);
        HistoryRestoreResult? mutationResult = null;
        var gate = new RestoreMutationContinuationGate(async token =>
        {
            mutationResult = await NativeHostMutationContext.RunSuppliedContinuationAsync(
                () => hostMutation(token)).ConfigureAwait(false);
            return mutationResult.Succeeded ? OperationOutcome.Success : OperationOutcome.Failed;
        });

        try
        {
            RestoreCoordinatorResult coordinated;
            using (NativeHostMutationContext.EnterCoordinatorCallback())
            {
                coordinated = await lease.Capability.CoordinateAsync(
                    new RestoreCoordinatorRequest(
                        configSnapshot,
                        folderSnapshot,
                        targetIdentity,
                        gate.InvokeAsync),
                    lease.Context).ConfigureAwait(false);
            }

            if (!gate.WasInvoked || mutationResult is null)
                return Blocked("RestoreCoordinator did not invoke the supplied Host continuation.");
            if (!mutationResult.TargetCommitted)
                return mutationResult;
            if (coordinated.Outcome is OperationOutcome.Success or OperationOutcome.SuccessWithWarnings)
            {
                return coordinated.Outcome == OperationOutcome.SuccessWithWarnings
                    ? WithPostActionWarning(mutationResult, coordinated.Diagnostics)
                    : mutationResult;
            }
            return WithPostActionWarning(mutationResult, coordinated.Diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && mutationResult is null)
        {
            return Blocked("Restore was canceled before Host target mutation.");
        }
        catch (Exception ex)
        {
            return mutationResult?.TargetCommitted == true
                ? mutationResult with
                {
                    Status = HistoryRestoreStatus.CommittedWithPostActionWarning,
                    Diagnostic = $"Target mutation committed; coordinator post-action failed: {ex.Message}"
                }
                : Blocked(ex.Message);
        }
    }

    private static HistoryRestoreResult WithPostActionWarning(
        HistoryRestoreResult committed,
        IReadOnlyList<PluginDiagnostic> diagnostics)
        => committed with
        {
            Status = HistoryRestoreStatus.CommittedWithPostActionWarning,
            Diagnostic = diagnostics.Count == 0
                ? "Target mutation committed; coordinator reported a post-action warning."
                : string.Join("; ", diagnostics.Select(item => item.Code))
        };

    private static HistoryRestoreResult Blocked(string diagnostic)
        => new(HistoryRestoreStatus.BlockedBeforeMutation, diagnostic, false, []);
}

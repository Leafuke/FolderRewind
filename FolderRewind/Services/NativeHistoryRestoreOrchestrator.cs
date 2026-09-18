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
    internal sealed record Operation(Guid Id, bool PreservePlayerData);
    private static readonly AsyncLocal<Operation?> CurrentOperation = new();
    internal static Operation? Current => CurrentOperation.Value;
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
        CancellationToken cancellationToken,
        WorkspaceOperationKind operationKind = WorkspaceOperationKind.Restore,
        RestoreRequestOptions? options = null)
    {
        var previous = CurrentOperation.Value;
        CurrentOperation.Value = new(Guid.NewGuid(), operationKind == WorkspaceOperationKind.Restore
            && options?.PreservePlayerData == true);
        try { return await ExecuteCoreAsync(config, affectedFolders, targetIdentity, hostMutation,
            cancellationToken, operationKind).ConfigureAwait(false); }
        finally { CurrentOperation.Value = previous; }
    }

    private async Task<HistoryRestoreResult> ExecuteCoreAsync(BackupConfig config,
        IReadOnlyList<ManagedFolder> affectedFolders, string targetIdentity,
        Func<CancellationToken, Task<HistoryRestoreResult>> hostMutation, CancellationToken cancellationToken,
        WorkspaceOperationKind operationKind)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(affectedFolders);
        ArgumentNullException.ThrowIfNull(hostMutation);
        if (affectedFolders.Count == 0)
            return await hostMutation(cancellationToken).ConfigureAwait(false);

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
        var folderSnapshots = affectedFolders.Select(folder => configSnapshot.Folders.Single(
            item => item.FolderId == Guid.Parse(folder.Id))).ToArray();
        HistoryRestoreResult? mutationResult = null;
        var gate = new RestoreMutationContinuationGate(async token =>
        {
            mutationResult = await NativeHostMutationContext.RunSuppliedContinuationAsync(
                () => hostMutation(token)).ConfigureAwait(false);
            return mutationResult.Status == HistoryRestoreStatus.CommittedRecoveryRequired
                ? OperationOutcome.CommittedRecoveryRequired
                : mutationResult.Status == HistoryRestoreStatus.MutationFailedRecoveryRequired
                ? OperationOutcome.RecoveryRequired
                : mutationResult.Succeeded ? OperationOutcome.Success : OperationOutcome.Failed;
        });

        try
        {
            RestoreCoordinatorResult coordinated;
            try
            {
                using (NativeHostMutationContext.EnterCoordinatorCallback())
                {
                coordinated = await lease.Capability.CoordinateAsync(
                    new RestoreCoordinatorRequest(
                        configSnapshot,
                        folderSnapshots,
                        targetIdentity,
                        Current!.Id,
                        operationKind,
                        gate.InvokeAsync),
                    lease.Context).ConfigureAwait(false);
                }
            }
            finally { await gate.CloseAndDrainAsync().ConfigureAwait(false); }

            if (!gate.WasInvoked || mutationResult is null)
                return Blocked("RestoreCoordinator did not invoke the supplied Host continuation.");
            if (!mutationResult.TargetCommitted || mutationResult.Status == HistoryRestoreStatus.CommittedRecoveryRequired)
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
            if (mutationResult?.Status == HistoryRestoreStatus.CommittedRecoveryRequired) return mutationResult;
            return mutationResult?.TargetCommitted == true
                ? mutationResult with
                {
                    Status = HistoryRestoreStatus.CommittedWithPostActionWarning,
                    Diagnostic = $"Target mutation committed; coordinator post-action failed: {ex.Message}"
                }
                : mutationResult ?? Blocked(ex.Message);
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

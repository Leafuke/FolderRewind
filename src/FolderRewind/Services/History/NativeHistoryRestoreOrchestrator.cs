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
    internal sealed record Operation(Guid Id, bool PreservePlayerData, bool? PreservePlayerDataOverride,
        IReadOnlyList<string> RestoreWhitelist, IReadOnlyList<string> RestorePreservePaths)
    {
        internal OperationOutcome? CoordinatorOutcome { get; set; }
    }
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
        diagnostic = I18n.GetString("Restore_CoordinatorUnavailable");
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
        var isRestore = operationKind == WorkspaceOperationKind.Restore;
        CurrentOperation.Value = new(Guid.NewGuid(), isRestore && options?.PreservePlayerData == true,
            isRestore ? options?.PreservePlayerDataOverride : null,
            isRestore ? options?.RestoreWhitelist?.ToArray() ?? [] : [],
            isRestore ? RestorePreservePaths.Normalize(options?.RestorePreservePaths ?? []) : []);
        try
        {
            var result = await ExecuteCoreAsync(config, affectedFolders, targetIdentity, hostMutation,
                cancellationToken, operationKind).ConfigureAwait(false);
            var canceled = cancellationToken.IsCancellationRequested || Current?.CoordinatorOutcome == OperationOutcome.Canceled;
            var level = canceled ? LogLevel.Info : result.Succeeded
                ? result.PostActionDiagnostic is null ? LogLevel.Info : LogLevel.Warning
                : result.Status == HistoryRestoreStatus.BlockedBeforeMutation && Current?.CoordinatorOutcome != OperationOutcome.Failed
                    ? LogLevel.Warning : LogLevel.Error;
            LogService.Log($"Restore completed: {Context(config, affectedFolders, targetIdentity, operationKind)}; status={result.Status}; "
                + $"outcome={Current?.CoordinatorOutcome}; diagnostic={result.Diagnostic}", level, "History");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogService.LogInfo("Restore canceled: " + Context(config, affectedFolders, targetIdentity, operationKind), "History");
            throw;
        }
        catch (Exception ex)
        {
            LogService.LogError($"Restore failed: {Context(config, affectedFolders, targetIdentity, operationKind)}; hresult=0x{ex.HResult:X8}", "History", ex);
            throw;
        }
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
                ? Blocked(I18n.GetString("Restore_CoordinatorUnavailable"))
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

            Current!.CoordinatorOutcome = coordinated.Outcome;
            var context = Context(config, affectedFolders, targetIdentity, operationKind)
                + $"; outcome={coordinated.Outcome}; continuation_invoked={gate.WasInvoked}";
            OperationDiagnosticFormatter.Log(coordinated.Diagnostics, context, "History",
                coordinated.Outcome == OperationOutcome.Canceled ? LogLevel.Info : null);
            return NativeRestoreCoordinationPolicy.Resolve(gate.WasInvoked, mutationResult, coordinated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && mutationResult is null)
        {
            Current!.CoordinatorOutcome = OperationOutcome.Canceled;
            return Blocked(I18n.GetString("Restore_CoordinatorCanceled"));
        }
        catch (Exception ex)
        {
            Current!.CoordinatorOutcome = OperationOutcome.Failed;
            LogService.LogError($"Restore callback/coordinator failed: {Context(config, affectedFolders, targetIdentity, operationKind)}; "
                + $"continuation_invoked={gate.WasInvoked}; hresult=0x{ex.HResult:X8}", "History", ex);
            if (mutationResult?.Status is HistoryRestoreStatus.CommittedRecoveryRequired or HistoryRestoreStatus.MutationFailedRecoveryRequired) return mutationResult;
            return mutationResult?.WithPostActionWarning(I18n.Format("Restore_CoordinatorPostActionFailed", ex.Message)) ?? Blocked(ex.Message);
        }
    }

    private static string Context(BackupConfig config, IReadOnlyList<ManagedFolder> folders, string target, WorkspaceOperationKind kind)
        => $"operation={Current?.Id}; config={config.Id}; sources={string.Join(",", folders.Select(folder => folder.Id))}; target={target}; kind={kind}";

    private static HistoryRestoreResult Blocked(string diagnostic)
        => new(HistoryRestoreStatus.BlockedBeforeMutation, diagnostic, false, []);
}

using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed class HistoryCheckoutService
{
    private readonly HistoryRuntime _history;
    private readonly HistoryRestoreService _restore;
    private readonly IHistoryWorkingStateProtector? _protector;

    public HistoryCheckoutService(
        HistoryRuntime history,
        HistoryRestoreService restore,
        IHistoryWorkingStateProtector? protector = null)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _restore = restore ?? throw new ArgumentNullException(nameof(restore));
        _protector = protector;
    }

    public async Task<HistoryRestoreResult> CheckoutAsync(
        BranchUpdateId selectedTipId,
        IReadOnlyList<HistoryRestoreSourceBinding> currentConfigSources,
        HistoryWorkspace expectedWorkspace,
        HistoryCheckoutProtectionMode protectionMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentConfigSources);
        ArgumentNullException.ThrowIfNull(expectedWorkspace);
        await _restore.RecoverIncompleteAsync(cancellationToken).ConfigureAwait(false);

        CheckoutSelection selection;
        try
        {
            selection = await ResolveSelectionAsync(
                selectedTipId,
                currentConfigSources,
                cancellationToken).ConfigureAwait(false);
            _ = await _restore.RequireExpectedWorkspaceAsync(expectedWorkspace, cancellationToken).ConfigureAwait(false);
            foreach (var checkpointSource in selection.Checkpoint.Sources)
            {
                await _restore.EnsureReadyAsync(
                    checkpointSource.VersionId!.Value,
                    MaterializationFidelity.Exact,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            return Blocked(ex.Message);
        }

        HistoryWorkspace protectedWorkspace;
        try
        {
            if (protectionMode == HistoryCheckoutProtectionMode.ProtectCurrentWork)
            {
                if (_protector is null)
                    return Blocked("Current-work protection is required but no protector is available.");
                protectedWorkspace = await _protector.ProtectAsync(
                    expectedWorkspace,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                protectedWorkspace = expectedWorkspace;
            }
            _ = await _restore.RequireExpectedWorkspaceAsync(protectedWorkspace, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Blocked(ex.Message);
        }

        var prepared = new List<HistoryRestoreService.PreparedRestoreSource>();
        try
        {
            foreach (var checkpointSource in selection.Checkpoint.Sources)
            {
                var version = await _history.Query.GetVersionAsync(
                    checkpointSource.VersionId!.Value,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Checkpoint SourceVersion is missing.");
                var binding = currentConfigSources.Single(source => source.SourceId == checkpointSource.SourceId);
                prepared.Add(await _restore.PrepareSourceAsync(
                    version,
                    binding,
                    MaterializationFidelity.Exact,
                    HistoryRestoreApplyMode.Clean,
                    cancellationToken).ConfigureAwait(false));
            }
        }
        catch (Exception ex)
        {
            HistoryRestoreTransactionJournalStore.CleanupStaging(prepared.Select(item => item.StagingDirectory));
            return Blocked(ex.Message);
        }

        await using var lease = await _history.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _restore.RequireExpectedWorkspaceAsync(protectedWorkspace, cancellationToken).ConfigureAwait(false);
            var revalidated = await ResolveSelectionAsync(
                selectedTipId,
                currentConfigSources,
                cancellationToken).ConfigureAwait(false);
            if (revalidated.Update.UpdateId != selection.Update.UpdateId
                || revalidated.Checkpoint.CheckpointId != selection.Checkpoint.CheckpointId)
            {
                throw new InvalidOperationException("Branch selection changed during materialization.");
            }

            var desired = new HistoryWorkspace(
                _history.ConfigId,
                checked(current.StateRevision + 1),
                selection.Update.BranchId,
                selection.Update.UpdateId,
                selection.Checkpoint.Sources.Select(source => new WorkspaceSourceBaseline(
                    source.SourceId,
                    source.VersionId,
                    WorkspaceBaselineRelation.Exact)));
            return await _restore.ExecuteMutationAsync(
                prepared,
                current,
                desired,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            HistoryRestoreTransactionJournalStore.CleanupStaging(prepared.Select(item => item.StagingDirectory));
            return Blocked(ex.Message);
        }
    }

    private async Task<CheckoutSelection> ResolveSelectionAsync(
        BranchUpdateId selectedTipId,
        IReadOnlyList<HistoryRestoreSourceBinding> currentConfigSources,
        CancellationToken cancellationToken)
    {
        await _history.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        var update = await _history.Query.GetBranchUpdateAsync(selectedTipId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Selected BranchUpdate does not exist.");
        if (update.IsDeleted || update.TargetCheckpointId is null)
            throw new InvalidOperationException("Deleted or unborn Branch cannot be checked out.");
        var tips = await _history.Query.GetBranchTipsAsync(update.BranchId, cancellationToken).ConfigureAwait(false);
        if (tips.All(tip => tip.UpdateId != update.UpdateId))
            throw new InvalidOperationException("Selected BranchUpdate is not a current tip.");
        var checkpoint = await _history.Query.GetCheckpointAsync(
            update.TargetCheckpointId.Value,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Branch target Checkpoint is missing.");
        if (!checkpoint.IsStructurallyComplete)
            throw new InvalidOperationException("Partial Checkpoint cannot be activated as a Branch checkout.");

        var bindingIds = currentConfigSources.Select(source => source.SourceId).ToArray();
        if (bindingIds.Distinct().Count() != bindingIds.Length
            || currentConfigSources.Select(source => Path.GetFullPath(source.TargetDirectory))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != currentConfigSources.Count
            || !bindingIds.OrderBy(id => id.ToString(), StringComparer.Ordinal).SequenceEqual(
                checkpoint.Sources.Select(source => source.SourceId)
                    .OrderBy(id => id.ToString(), StringComparer.Ordinal)))
        {
            throw new InvalidOperationException("BlockedConfigurationMismatch: Checkpoint roster does not match current Config Sources.");
        }
        return new CheckoutSelection(update, checkpoint);
    }

    private static HistoryRestoreResult Blocked(string diagnostic)
        => new(HistoryRestoreStatus.Blocked, diagnostic, false, []);

    private sealed record CheckoutSelection(
        BranchUpdate Update,
        ConfigurationCheckpoint Checkpoint);
}

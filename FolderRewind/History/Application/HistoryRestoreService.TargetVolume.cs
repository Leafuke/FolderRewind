using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed partial class HistoryRestoreService
{
    internal HistoryRestorePreparationSession? CreatePreparationSession(IReadOnlyList<HistoryRestoreSourceBinding> bindings, HistoryWorkspace expected)
        => UseTargetVolumeWorkspaces && bindings.Count > 0 ? new(_history, _journals, expected, bindings, ProtectedRestoreRoots, RestoreConfigurationSignature) : null;
    internal HistoryRestorePreparationSession? TryCreatePreparationSession(IReadOnlyList<HistoryRestoreSourceBinding> bindings,
        HistoryWorkspace expected, out string? diagnostic)
    {
        diagnostic = null;
        try { return CreatePreparationSession(bindings, expected); }
        catch (Exception error) { diagnostic = error.Message; return null; }
    }

    private async Task<HistoryRestoreResult> ExecuteTargetVolumeMutationAsync(HistoryRestorePreparationSession session,
        IReadOnlyList<PreparedRestoreSource> prepared, HistoryWorkspace expected, HistoryWorkspace desired, CancellationToken token)
    {
        var applied = prepared.Select(p => p.Binding.SourceId).ToArray();
        HistoryRestoreTransactionJournal? journal = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var plan = HistoryRestoreMutationPlanner.Build(session, prepared, AllowDirectorySwap && !HoldCurrentReadProtection,
                HoldCurrentReadProtection, UnchangedTargetSources);
            ValidateCurrentInputs?.Invoke();
            journal = session.Freeze(plan, expected, desired);
            using (HistoryRestoreFileSystemPlanStore.Execute(plan,
                Path.Combine(_journals.TransactionDirectory(journal.TransactionId), "restore-progress.bin"), token))
            {
                HistoryRestoreFileSystemPlanStore.ValidatePublished(plan);
                journal = journal with { Phase = HistoryRestoreTransactionPhase.WorkspaceApplying }; session.SetJournal(journal);
                HistoryRestoreFileSystemPlanStore.Reach("before-workspace");
                await _journals.CompleteLocalStateAsync(journal, token).ConfigureAwait(false);
                HistoryRestoreFileSystemPlanStore.Reach("after-workspace");
                HistoryRestoreFileSystemPlanStore.ValidatePublished(plan);
                _journals.CompleteWithDeferredCleanup(journal, committed: true);
            }
            session.Finish();
            await _history.EnsureIndexCurrentAsync(CancellationToken.None).ConfigureAwait(false);
            await _history.RefreshLocalStateHealthAsync(CancellationToken.None).ConfigureAwait(false);
            _history.ChangeFeed.Publish(_history.ConfigId, HistoryChangeKind.LocalStateChanged);
            return new(HistoryRestoreStatus.Committed, "", true, applied);
        }
        catch (Exception error)
        {
            if (journal is null)
            {
                session.Abort(); return new(HistoryRestoreStatus.BlockedBeforeMutation,
                    session.Diagnostic is null ? error.Message : error.Message + "; " + session.Diagnostic, false, []);
            }
            bool committed = false;
            try
            {
                var actual = (await _history.WorkspaceStore.LoadAsync(CancellationToken.None).ConfigureAwait(false)).Value;
                committed = actual is not null && HistoryRestoreTransactionJournalStore.WorkspaceEquals(actual, desired);
                await _journals.RecoverIncompleteAsync(CancellationToken.None).ConfigureAwait(false); session.Finish();
                return new(committed ? HistoryRestoreStatus.CommittedWithPostActionWarning : HistoryRestoreStatus.MutationFailedRolledBack,
                    error.Message, committed, committed ? applied : []);
            }
            catch (Exception recovery)
            {
                session.Finish(); return new(committed ? HistoryRestoreStatus.CommittedRecoveryRequired : HistoryRestoreStatus.MutationFailedRecoveryRequired,
                    error.Message + "; recovery required: " + recovery.Message, committed, committed ? applied : []);
            }
        }
    }
}

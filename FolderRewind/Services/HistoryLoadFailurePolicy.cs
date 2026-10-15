using FolderRewind.History.Storage;
using System;
using System.IO;

namespace FolderRewind.Services;

internal enum HistoryLoadAction { Retry, ImportHistory, ContinueMigration, None }
internal sealed record HistoryLoadFailurePresentation(string SummaryKey, HistoryLoadAction Action);

internal static class HistoryLoadFailurePolicy
{
    internal static HistoryLoadFailurePresentation Present(Exception error, bool hasPendingMigration)
    {
        var (key, action) = error switch
        {
            HistoryRepositoryBindingException { Status: HistoryRepositoryBindingStatus.CompatibilityBlocked }
                or HistoryPackCompatibilityException => ("HistoryLoad_Incompatible", HistoryLoadAction.None),
            HistoryRepositoryBindingException { Status: HistoryRepositoryBindingStatus.RecoveryRequired }
                => ("HistoryLoad_RecoveryRequired", HistoryLoadAction.ImportHistory),
            HistoryRepositoryBindingException { Status: HistoryRepositoryBindingStatus.BindingPersistenceFailed }
                => ("HistoryLoad_BindingSaveFailed", HistoryLoadAction.Retry),
            UnauthorizedAccessException => ("HistoryLoad_AccessDenied", HistoryLoadAction.Retry),
            IOException => ("HistoryLoad_ReadFailed", HistoryLoadAction.Retry),
            _ => ("HistoryLoad_Failed", HistoryLoadAction.Retry)
        };
        return new(key, hasPendingMigration && action != HistoryLoadAction.None ? HistoryLoadAction.ContinueMigration : action);
    }
}

using FolderRewind.History.Application;

namespace FolderRewind.Services;

internal enum MigrationHistoryStatus { None, AwaitingHistory, AwaitingRecovery, AwaitingPermissions, MissingArchives, AwaitingCloudPermissions }
internal static class MigrationHistoryStatusPolicy
{
    internal static bool ShowPendingVersion(MigrationHistoryStatus status, HistoryPresentationReadiness actual)
        => status == MigrationHistoryStatus.AwaitingRecovery && actual is HistoryPresentationReadiness.Unavailable
            or HistoryPresentationReadiness.PreparationRequired or HistoryPresentationReadiness.PluginOrCredentialRequired;
}

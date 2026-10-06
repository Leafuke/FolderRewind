using FolderRewind.Models;

namespace FolderRewind.Services;

/// <summary>
/// 判断精确来源在本次备份中是否暂时不可用。
/// </summary>
internal static class BackupSourceAvailabilityPolicy
{
    public static bool IsUnavailable(BackupSourceScope? sourceScope, int matchedFileCount)
    {
        return sourceScope?.Mode == BackupSourceScopeMode.Include && matchedFileCount == 0;
    }
}

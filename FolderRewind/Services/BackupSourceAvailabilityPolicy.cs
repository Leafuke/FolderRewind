using FolderRewind.Models;

namespace FolderRewind.Services;

/// <summary>
/// 判断精确来源在本次备份中是否暂时不可用。
/// Include 范围下一个文件都没匹配到，通常意味着磁盘没挂载或路径已失效，
/// 这时必须报「不可用」而不是「无变化」——后者会把一次真实的数据消失记成一次空备份。
/// </summary>
internal static class BackupSourceAvailabilityPolicy
{
    public static bool IsUnavailable(BackupSourceScope? sourceScope, int matchedFileCount)
    {
        return sourceScope?.Mode == BackupSourceScopeMode.Include && matchedFileCount == 0;
    }
}

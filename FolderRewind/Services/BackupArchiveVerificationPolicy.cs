using FolderRewind.Models;
using System;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static class BackupArchiveVerificationPolicy
{
    internal static async Task<bool> VerifyFinalAsync(BackupArchiveVerificationLevel level,
        Func<Task<bool>> safeLogicalListing, Func<Task<bool>> fullContentTest)
    {
        if (!Enum.IsDefined(level)) throw new ArgumentOutOfRangeException(nameof(level));
        // 清单与内容测试是不同证据；清单失败不提交，也不靠 t 的成功绕过逻辑状态不匹配。
        return await safeLogicalListing().ConfigureAwait(false)
            && (level == BackupArchiveVerificationLevel.Balanced || await fullContentTest().ConfigureAwait(false));
    }
}

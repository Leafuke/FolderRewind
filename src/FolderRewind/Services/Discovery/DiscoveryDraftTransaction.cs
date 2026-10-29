using System;

namespace FolderRewind.Services.Discovery;

public readonly record struct DiscoveryDraftTransactionResult(bool Success, string ErrorMessage);

public static class DiscoveryDraftTransaction
{
    public static DiscoveryDraftTransactionResult Execute(
        Action apply,
        Action rollback,
        Func<DiscoveryDraftTransactionResult> save)
    {
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(rollback);
        ArgumentNullException.ThrowIfNull(save);
        try
        {
            apply();
            var result = save();
            if (!result.Success)
            {
                rollback();
            }
            return result;
        }
        catch (Exception ex)
        {
            try { rollback(); } catch { }
            return new DiscoveryDraftTransactionResult(false, ex.Message);
        }
    }
}

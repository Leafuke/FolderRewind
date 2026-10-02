using System;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.Models;

namespace FolderRewind.Services;

public sealed record TemplateMutationResult(bool Success, string Message, BackupPreset? Template = null);

public static partial class BackupPresetService
{
    private static readonly SemaphoreSlim TemplateWriteGate = new(1, 1);

    private static async Task<TemplateMutationResult> PersistTemplateAsync(
        BackupPreset? original, BackupPreset? draft, string successMessage)
    {
        if (!await TemplateWriteGate.WaitAsync(0))
            return new(false, I18n.GetString("GameDiscovery_OperationRunning"));
        try
        {
            if (ConfigService.IsRecoveryMode)
                return new(false, I18n.GetString("Config_MutationDisabledInRecovery"));
            var presets = ConfigService.CurrentConfig.BackupPresets;
            await ConfigEditTransaction.ReplaceItemAsync(presets, original, draft,
                () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"));
            return new(true, successMessage, draft);
        }
        catch (ConfigEditRollbackException ex)
        {
            LogService.LogError(ex.Message, nameof(BackupPresetService), ex);
            return new(false, I18n.Format("Config_CompensationFailed", ex.Message));
        }
        catch (Exception ex)
        {
            LogService.LogError(ex.Message, nameof(BackupPresetService), ex);
            return new(false, I18n.Format("Config_SaveFailed", ex.Message));
        }
        finally
        {
            TemplateWriteGate.Release();
        }
    }
}

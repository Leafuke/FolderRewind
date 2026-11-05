using FolderRewind.Models;
using System;

namespace FolderRewind.Services;

public static partial class ConfigService
{
    private static ConfigSaveResult ReportSaveResult(ConfigSaveResult result)
    {
        if (result.Success) return result;
        if (result.Exception is ConfigSourceIdentityException invalidIdentity)
            result = new ConfigSaveResult { ErrorMessage = I18n.GetString(invalidIdentity.Duplicate
                ? "Config_SourceIdDuplicate" : "Config_SourceIdInvalid"), Exception = invalidIdentity };
        // A save can be requested without an awaiting UI caller. Use the session notification
        // store's existing result-key deduplication for coalesced and repeated failures.
        var diagnostic = CloudCommandSecurity.Redact(result.ErrorMessage ?? string.Empty);
        var code = result.Exception is ConfigSourceIdentityException identity
            ? identity.Duplicate ? "configuration.source-id-duplicate" : "configuration.source-id-invalid"
            : "configuration.save-failed";
        try
        {
            UiDispatcherService.Enqueue(() => NotificationService.ShowNotification(new()
            {
                RecordId = "configuration:persistence-failure",
                Target = new(NotificationTargetKind.Configuration, "configuration:persistence-failure"),
                Source = "configuration", FailureCode = code, ResultKey = code + ":" + diagnostic,
                Category = NotificationCategory.Attention, KeepInCenter = true, AlertOnce = false,
                Severity = NotificationSeverity.Error,
                Title = I18n.GetString("Config_SaveFailureTitle"),
                Message = I18n.GetString("Config_SaveFailureMessage"), Details = diagnostic,
                TechnicalDetails = code
            }, NotificationImportance.Error));
        }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
        return result;
    }
}

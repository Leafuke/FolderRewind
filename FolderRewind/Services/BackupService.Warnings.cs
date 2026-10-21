using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static partial class BackupService
{
    private static void AddSourceWarning(BackupSourceExecutionOutcome outcome, string code, string message)
    {
        var diagnostic = new PluginDiagnostic(code, DiagnosticSeverity.Warning, "Backup", "folderrewind.core",
            new Dictionary<string, string> { ["message"] = message });
        outcome.WarningDiagnostics = OperationDiagnosticFormatter.Snapshot(outcome.WarningDiagnostics.Append(diagnostic));
        if (outcome.OperationOutcome is OperationOutcome.Success or OperationOutcome.NoChanges)
            outcome.OperationOutcome = OperationOutcome.SuccessWithWarnings;
    }

    private static async Task PublishSourceWarningsAsync(BackupConfig config, ManagedFolder folder, BackupSourceExecutionOutcome outcome)
    {
        if (outcome.WarningDiagnostics.Count == 0 && outcome.OperationOutcome == OperationOutcome.SuccessWithWarnings)
            AddSourceWarning(outcome, "backup.warning_unspecified", "");
        if (outcome.WarningDiagnostics.Count == 0) return;
        var diagnostics = OperationDiagnosticFormatter.Snapshot(outcome.WarningDiagnostics);
        var unpublished = diagnostics.Where(item => outcome.PublishedWarningIdentities.Add(OperationDiagnosticFormatter.Identity(item))).ToArray();
        OperationDiagnosticFormatter.Log(unpublished, $"config={config.Id}; source={outcome.FolderId}; folder={folder.DisplayName}; file={outcome.GeneratedFileName}", "Backup");
        try
        {
            await RunOnUIAsync(() =>
            {
                if (outcome.Task is not { } task) return;
                task.WarningDiagnostics = diagnostics;
                if (task.IsCompleted && task.IsSuccess)
                    folder.StatusText = task.Status = diagnostics.FirstOrDefault(item => item.Code == "backup.history_recovery_required")?
                        .Arguments.GetValueOrDefault("message") ?? I18n.GetString("BackupService_Task_CompletedWithWarnings");
            });
        }
        catch (Exception ex) { LogService.LogWarning("Could not present backup warning: " + ex.Message, "Backup"); }
        if (unpublished.Length == 0 || outcome.Status is not (BackupSourceExecutionStatus.NewArchive or BackupSourceExecutionStatus.Reused)) return;
        BroadcastBackupEvent(GetConfigIndex(config), config, folder, "backup_warning", new Dictionary<string, string?>
        {
            ["type"] = "source_warning", ["warning_codes"] = string.Join(",", outcome.WarningCodes),
            ["message"] = OperationDiagnosticFormatter.Summary(diagnostics)
        });
        if (!outcome.WarningNotificationSent)
        {
            outcome.WarningNotificationSent = true;
            NotificationService.ShowWarning(I18n.Format("BackupService_Warning_CompletedWithWarnings", folder.DisplayName));
        }
    }

    private static async Task AddPostCommitWarningAsync(BackupConfig config, IReadOnlyList<ManagedFolder> folders,
        IReadOnlyList<BackupSourceExecutionOutcome> outcomes, string code, string message)
    {
        for (var i = 0; i < outcomes.Count; i++)
        {
            var outcome = outcomes[i];
            if (outcome.Status is not (BackupSourceExecutionStatus.NewArchive or BackupSourceExecutionStatus.Reused)) continue;
            AddSourceWarning(outcome, code, message);
            await PublishSourceWarningsAsync(config, folders[i], outcome).ConfigureAwait(false);
        }
    }
}

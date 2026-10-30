using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static partial class BackupService
{
    // Presentation wraps the existing transaction; it cannot alter its result or commit order.
    internal static async Task<BackupTransactionExecutionResult> ExecuteBackupTransactionAsync(
        BackupConfig config, IReadOnlyList<ManagedFolder> requestedFolders, BackupInvocationOptions invocationOptions,
        HistoryCommitIntent intent = HistoryCommitIntent.AdvanceBranch, HistorySafetySnapshotIntent? safetySnapshotIntent = null,
        CancellationToken cancellationToken = default, NativeHistoryConfigurationOperationGate.Lease? existingOperation = null)
    {
        var internalCapture = invocationOptions?.Source == BackupInvocationSource.Internal || safetySnapshotIntent is not null;
        var target = new NotificationTarget(NotificationTargetKind.Tasks,
            internalCapture ? NotificationOperationScope.Current?.OperationId : null, config?.Id,
            requestedFolders?.Count == 1 ? requestedFolders[0].Id : null);
        using var operation = new NotificationOperationScope(target, internalCapture);
        using var batch = new BackupNotificationBatch(config?.Name ?? "", operation.Target, requestedFolders?.Count ?? 0,
            requestedFolders?.Count == 1 ? requestedFolders[0].DisplayName : config?.Name ?? "",
            invocationOptions?.Source == BackupInvocationSource.Automatic ? NotificationTrigger.Automatic
                : invocationOptions?.Source is BackupInvocationSource.Manual or BackupInvocationSource.PluginHotkey ? NotificationTrigger.Manual : NotificationTrigger.Unknown);
        try
        {
            var result = await ExecuteBackupTransactionCoreAsync(config!, requestedFolders!, invocationOptions!, intent,
                safetySnapshotIntent, cancellationToken, existingOperation).ConfigureAwait(false);
            batch.Complete(result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            batch.Complete(BackupTransactionExecutionResult.Canceled([]));
            throw;
        }
        catch (Exception error)
        {
            batch.Complete(BackupTransactionExecutionResult.Failed([], error.Message));
            if (operation.ResultReported) NotificationService.MarkExceptionReported(error);
            throw;
        }
    }

    private sealed class BackupNotificationBatch : IDisposable
    {
        private static readonly AsyncLocal<BackupNotificationBatch?> Ambient = new();
        private readonly BackupNotificationBatch? _previous;
        private readonly string _name;
        private readonly int _total;
        private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
        private readonly Dictionary<string, string> _failures = new(StringComparer.Ordinal);
        private NotificationTarget _target;
        private readonly string _objectName;
        private readonly NotificationTrigger _trigger;
        internal static BackupNotificationBatch? Current => Ambient.Value;
        public BackupNotificationBatch(string name, NotificationTarget target, int total, string objectName, NotificationTrigger trigger)
        { _previous = Ambient.Value; Ambient.Value = this; _name = name; _target = target; _total = total; _objectName = objectName; _trigger = trigger; }
        public void Dispose() => Ambient.Value = _previous;

        public void Failed(ManagedFolder folder, string detail)
        {
            try
            {
                if (_failures.TryGetValue(folder.Id, out var previous) && previous == detail) return;
                _failures[folder.Id] = detail;
                Publish(I18n.Format("NotificationCenter_BatchRunningFailure", _name, _failures.Count, _total),
                    I18n.GetString("OperationDiagnostic_Unknown"), NotificationSeverity.Error, NotificationImportance.Error,
                    "failure:" + _failures.Count + ":" + detail, false, result: NotificationResultKind.Failed, technical: string.Join(Environment.NewLine, _failures.Values));
            }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine($"[BackupNotification] {error}"); }
        }

        public void Complete(BackupTransactionExecutionResult result)
        {
            try
            {
                _target = _target with { RunId = result.CommittedBatch?.Run.RunId.ToString(), HistoryCreated = result.CommittedBatch is not null ? true : result.HistoryRecoveryRequired ? null : false };
                if (NotificationOperationScope.Current is { } operation) operation.Target = _target;
                var canceled = result.Outcome == OperationOutcome.Canceled;
                var failed = result.Outcome is OperationOutcome.Failed or OperationOutcome.Blocked or OperationOutcome.RecoveryRequired;
                var warning = result.HistoryRecoveryRequired || result.Outcome is OperationOutcome.SuccessWithWarnings or OperationOutcome.CommittedRecoveryRequired;
                var committed = result.CommittedBatch is not null;
                var succeeded = committed ? result.SourceOutcomes.Count(o => o.Status == BackupSourceExecutionStatus.NewArchive) : 0;
                var unchanged = committed || result.Outcome == OperationOutcome.NoChanges
                    ? result.SourceOutcomes.Count(o => o.Status == BackupSourceExecutionStatus.Reused) : 0;
                var warnings = result.SourceOutcomes.Count(o => o.WarningDiagnostics.Count > 0);
                var failures = committed ? result.SourceOutcomes.Count(o => o.Status is not (BackupSourceExecutionStatus.NewArchive or BackupSourceExecutionStatus.Reused))
                    : failed ? _total : 0;
                var statusKey = canceled ? "Canceled" : result.HistoryRecoveryRequired ? "RecoveryRequired"
                    : failed ? "Failed" : warning ? "Warning" : result.Outcome == OperationOutcome.NoChanges ? "NoChanges" : "Success";
                var message = I18n.Format("NotificationCenter_BatchSummary", _name, I18n.GetString("NotificationCenter_Result_" + statusKey),
                    succeeded, unchanged, warnings, failures);
                var errors = OperationDiagnosticFormatter.Snapshot(result.SourceOutcomes.SelectMany(o => o.ErrorDiagnostics));
                var reasons = result.SourceOutcomes.Select(o => o.FolderName + ": "
                    + (o.ErrorDiagnostics.Count > 0 ? OperationDiagnosticFormatter.Summary(o.ErrorDiagnostics)
                        : !string.IsNullOrWhiteSpace(o.ErrorMessage) ? o.ErrorSummary
                        : OperationDiagnosticFormatter.Summary(o.WarningDiagnostics))).Where(t => !t.EndsWith(": ")).ToArray();
                var impact = I18n.GetString(committed ? "Product_HistoryCreated" : result.HistoryRecoveryRequired ? "Product_HistoryUncertain" : "Product_HistoryNotCreated");
                if (result.HistoryRecoveryRequired) impact += Environment.NewLine + I18n.GetString("OperationDiagnostic_BackupHistoryRecovery");
                var details = string.Join(Environment.NewLine, reasons) + Environment.NewLine + impact;
                var technical = string.Join(Environment.NewLine, result.SourceOutcomes.Select(o =>
                    o.FolderName + ": " + o.ErrorMessage + Environment.NewLine
                    + string.Join(Environment.NewLine, o.ErrorDiagnostics.Concat(o.WarningDiagnostics).Select(OperationDiagnosticFormatter.Technical))))
                    + Environment.NewLine + (result.Diagnostic ?? result.RecoveryMessage ?? "")
                    + Environment.NewLine + "operation=" + _target.OperationId;
                var recommendation = errors.Any(e => e.Code.StartsWith("plugin.", StringComparison.Ordinal))
                    ? new NotificationTarget(NotificationTargetKind.Configuration, ConfigId: _target.ConfigId) : null;
                Publish(message, details, canceled ? NotificationSeverity.Informational : failed || result.HistoryRecoveryRequired ? NotificationSeverity.Error
                        : warning ? NotificationSeverity.Warning : NotificationSeverity.Success,
                    failed || result.HistoryRecoveryRequired ? NotificationImportance.Error : warning ? NotificationImportance.Important : NotificationImportance.Info,
                    "final:" + statusKey + ":" + message + ":" + details,
                    canceled ? null : !failed, canceled ? NotificationCategory.Cancellation : NotificationCategory.BusinessResult,
                    canceled ? NotificationResultKind.Canceled : result.HistoryRecoveryRequired ? NotificationResultKind.RecoveryRequired
                        : failed ? NotificationResultKind.Failed : warning ? NotificationResultKind.Warning
                        : result.Outcome == OperationOutcome.NoChanges ? NotificationResultKind.NoChanges : NotificationResultKind.Success,
                    _total == 1 ? null : new(succeeded, unchanged, warnings, failures), technical, recommendation,
                    reasons.FirstOrDefault() ?? I18n.GetString("NotificationCenter_Result_" + statusKey));
            }
            catch (Exception error) { LogService.LogError("Could not publish backup result.", nameof(BackupService), error); }
        }

        private void Publish(string message, string details, NotificationSeverity severity, NotificationImportance importance,
            string resultKey, bool? sound, NotificationCategory category = NotificationCategory.BusinessResult,
            NotificationResultKind result = NotificationResultKind.Unknown, NotificationStatistics? statistics = null,
            string technical = "", NotificationTarget? recommendation = null, string summary = "")
        {
            try
            {
                NotificationService.ShowNotification(new InAppNotificationRequest
                {
                    RecordId = "operation:" + _target.OperationId, Target = _target, KeepInCenter = true, AlertOnce = true,
                    Category = category, Source = "backup",
                    Content = new(NotificationOperationKind.Backup, _trigger, result, _name, _objectName, statistics, summary, recommendation),
                    Correlation = new("operation:" + _target.OperationId, _target.OperationId!, _started, DateTimeOffset.UtcNow),
                    Title = I18n.GetString("NotificationCenter_BackupTitle"), Message = message, Details = details, TechnicalDetails = technical,
                    ResultKey = resultKey, Severity = severity,
                    CompletionSoundSuccess = sound
                }, importance);
            }
            catch (Exception error) { LogService.LogError("Could not publish backup notification.", nameof(BackupService), error); }
        }
    }
}

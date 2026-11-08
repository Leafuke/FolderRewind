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
        using var operation = new NotificationOperationScope(target, internalCapture,
            new(target.OperationId ?? "", internalCapture ? "SafetySnapshot" : "Backup"));
        using var batch = new BackupNotificationBatch(config?.Name ?? "", operation.Target, requestedFolders?.Count ?? 0,
            requestedFolders?.Count == 1 ? requestedFolders[0].DisplayName : config?.Name ?? "",
            invocationOptions?.Source == BackupInvocationSource.Automatic ? NotificationTrigger.Automatic
                : invocationOptions?.Source is BackupInvocationSource.Manual or BackupInvocationSource.PluginHotkey ? NotificationTrigger.Manual : NotificationTrigger.Unknown);
        if (NativeHostMutationContext.IsNestedMutationBlocked)
        {
            var blocked = BackupTransactionExecutionResult.Blocked("NestedHostMutationNotAllowed");
            batch.Complete(blocked); return blocked;
        }
        if (App.IsShuttingDown && !internalCapture)
        {
            var blocked = BackupTransactionExecutionResult.Blocked(I18n.GetString("OperationDiagnostic_ApplicationClosing"));
            batch.Complete(blocked); return blocked;
        }
        try
        {
            invocationOptions ??= BackupInvocationOptions.Default;
            using var restoreCapture = NativeRestoreSafetyCaptureContext.TryEnter(config!, requestedFolders!,
                invocationOptions, safetySnapshotIntent, existingOperation);
            if (restoreCapture is not null)
            {
#if MINECRAFT_COORDINATION_UI_ACCEPTANCE
                Development.NativeArtifactAcceptance.Observe("restore-safety-capture-start");
#endif
                await restoreCapture.ValidateAsync(cancellationToken).ConfigureAwait(false);
            }
            for (var admission = 0; ; admission++)
            {
                await using var run = restoreCapture is not null || PluginV3BackupRunOwner.Current is not null ? null
                    : await PluginV3BackupRunOwner.PrepareAsync(config!, requestedFolders!, invocationOptions, cancellationToken).ConfigureAwait(false);
                if (run is not null)
                {
                    if (existingOperation is not null && safetySnapshotIntent is not null && NativeHistoryRestoreOrchestrator.Current is not null)
                        throw new RestoreCoordinationChangedException();
                    await run.AdmitAsync(cancellationToken, existingOperation is not null).ConfigureAwait(false);
#if MINECRAFT_COORDINATION_UI_ACCEPTANCE
                    Development.NativeArtifactAcceptance.Observe("backup-channel-admitted");
#endif
                }
                using var scope = run?.EnterScope();
                var result = BackupTransactionExecutionResult.Failed([], "Backup interrupted.");
                var retryAdmission = false;
                try
                {
                    result = await ExecuteBackupTransactionCoreAsync(config!, requestedFolders!, invocationOptions, intent,
                        safetySnapshotIntent, cancellationToken, existingOperation).ConfigureAwait(false);
                }
                catch (BackupCoordinationAdmissionRequiredException) when (admission == 0 && existingOperation is null) { retryAdmission = true; }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                { result = BackupTransactionExecutionResult.Canceled([]); throw; }
                catch (Exception error)
                { result = BackupTransactionExecutionResult.Failed([], error.Message); throw; }
                finally
                {
                    if (run is not null)
                    {
                        var completed = false;
                        try { completed = await run.CompleteAsync(result).ConfigureAwait(false); }
                        catch (Exception error) { LogService.LogWarning(DiagnosticPrivacy.ExceptionMetadata(error), "Backup coordination completion"); }
                        if (!completed)
                        {
                            var diagnostic = new PluginDiagnostic("backup.coordination_cleanup_failed", DiagnosticSeverity.Warning,
                                "BackupConsistency", "folderrewind.core", new Dictionary<string, string>
                                { ["operationId"] = run.OperationId.ToString("D"), ["runId"] = result.CommittedBatch?.Run.RunId.ToString() ?? "" });
                            result.CompletionDiagnostics = OperationDiagnosticFormatter.Snapshot(result.CompletionDiagnostics.Append(diagnostic));
                            OperationDiagnosticFormatter.Log([diagnostic], "Backup completion; config=" + config!.Id, "Backup");
                            await AddPostCommitWarningAsync(config!, requestedFolders!, result.SourceOutcomes,
                                "backup.coordination_cleanup_failed", I18n.GetString("OperationDiagnostic_CoordinationCleanupFailed")).ConfigureAwait(false);
                            if (result.Outcome is OperationOutcome.Success or OperationOutcome.NoChanges) result.Outcome = OperationOutcome.SuccessWithWarnings;
                        }
                        if (result.CompletionDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Warning)
                            && result.Outcome is OperationOutcome.Success or OperationOutcome.NoChanges) result.Outcome = OperationOutcome.SuccessWithWarnings;
                    }
                }
                if (retryAdmission) continue;
                batch.Complete(result);
                return result;
            }
        }
        catch (RestoreCoordinationChangedException error)
        {
            var blocked = BackupTransactionExecutionResult.Blocked(error.Message);
            batch.Complete(blocked); return blocked;
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
        private readonly BusinessOperationDiagnostics _diagnostics;
        internal static BackupNotificationBatch? Current => Ambient.Value;
        public BackupNotificationBatch(string name, NotificationTarget target, int total, string objectName, NotificationTrigger trigger)
        { _previous = Ambient.Value; Ambient.Value = this; _name = name; _target = target; _total = total; _objectName = objectName; _trigger = trigger;
            _diagnostics = new(NotificationOperationScope.Current?.Context.OperationType ?? "Backup", NotificationOperationScope.Current?.IsInternal == true); }
        public void Dispose() { _diagnostics.Dispose(); Ambient.Value = _previous; }

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
            _diagnostics.Complete(result.Outcome.ToString(), result.Outcome is OperationOutcome.Failed or OperationOutcome.RecoveryRequired
                    ? LogLevel.Error : result.HistoryRecoveryRequired || result.Outcome is OperationOutcome.SuccessWithWarnings or OperationOutcome.Blocked
                        or OperationOutcome.CommittedRecoveryRequired ? LogLevel.Warning : LogLevel.Info,
                $"config={_target.ConfigId}; run={result.CommittedBatch?.Run.RunId}; sourceCount={_total}; historyCommitted={(result.CommittedBatch is not null ? "true" : result.HistoryRecoveryRequired ? "unknown" : "false")}; recoveryRequired={result.HistoryRecoveryRequired}; "
                + $"capturedSources={result.SourceOutcomes.Count(o => o.Status == BackupSourceExecutionStatus.NewArchive)}; unchanged={result.SourceOutcomes.Count(o => o.Status == BackupSourceExecutionStatus.Reused)}; "
                + $"warnings={result.CompletionDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning) + result.SourceOutcomes.Sum(o => o.WarningDiagnostics.Count)}; warningCodes="
                + string.Join(",", result.CompletionDiagnostics.Concat(result.SourceOutcomes.SelectMany(o => o.WarningDiagnostics)).Where(d => d.Severity == DiagnosticSeverity.Warning).Select(d => d.Code).Distinct()));
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
                        : OperationDiagnosticFormatter.Summary(o.WarningDiagnostics))).Where(t => !t.EndsWith(": "))
                    .Concat(result.CompletionDiagnostics.Select(OperationDiagnosticFormatter.Describe)).ToArray();
                var impact = I18n.GetString(committed ? "Product_HistoryCreated" : result.HistoryRecoveryRequired ? "Product_HistoryUncertain" : "Product_HistoryNotCreated");
                if (result.HistoryRecoveryRequired) impact += Environment.NewLine + I18n.GetString("OperationDiagnostic_BackupHistoryRecovery");
                var details = string.Join(Environment.NewLine, reasons) + Environment.NewLine + impact;
                var technical = string.Join(Environment.NewLine, result.SourceOutcomes.Select(o =>
                    o.FolderName + ": " + o.ErrorMessage + Environment.NewLine
                    + string.Join(Environment.NewLine, o.ErrorDiagnostics.Concat(o.WarningDiagnostics).Select(OperationDiagnosticFormatter.Technical))))
                    + Environment.NewLine + string.Join(Environment.NewLine, result.CompletionDiagnostics.Select(OperationDiagnosticFormatter.Technical))
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

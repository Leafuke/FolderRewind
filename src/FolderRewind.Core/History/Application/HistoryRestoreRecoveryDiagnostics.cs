using FolderRewind.History.Domain;
using FolderRewind.Services;
using System;
using System.IO;

namespace FolderRewind.History.Application;

internal enum RestoreRecoveryFailureKind
{
    UnregisteredObject,
    IdentityMismatch,
    OwnershipUnproven,
    ConflictingExecutionEvidence,
    AccessFailure
}

internal static class HistoryRestoreRecoveryDiagnostics
{
    private const string ReasonKey = "FolderRewind.RestoreFailure";
    private const string ContainerKey = "FolderRewind.RestoreContainer";
    private const string MemberKey = "FolderRewind.RestoreMember";

    internal static T Mark<T>(T error, RestoreRecoveryFailureKind reason, string? container = null, string? member = null) where T : Exception
    {
        error.Data[ReasonKey] = reason;
        if (container is not null) error.Data[ContainerKey] = container;
        if (member is not null) error.Data[MemberKey] = member;
        return error;
    }

    internal static RestoreRecoveryFailureKind Reason(Exception error)
        => error.Data[ReasonKey] is RestoreRecoveryFailureKind reason ? reason
            : error is IOException or UnauthorizedAccessException ? RestoreRecoveryFailureKind.AccessFailure
            : RestoreRecoveryFailureKind.ConflictingExecutionEvidence;

    internal static string Describe(Exception error)
        => $"Reason={Reason(error)}; Container={Clean(error.Data[ContainerKey])}; RelativePath={Clean(error.Data[MemberKey])}; {Clean(error.Message)}";

    internal static void Report(HistoryConfigId config, HistoryTransactionId? transaction, string phase,
        bool hasPlan, bool? committed, Exception error, string action, Action<string, string>? diagnosticSink = null)
    {
        var detail = $"[RestoreRecovery] Config={config}; Transaction={transaction}; Phase={phase}; Plan={hasPlan}; "
            + $"Committed={committed?.ToString() ?? "unknown"}; Action={action}; {Describe(error)}; HResult=0x{error.HResult:X8}";
        HistoryOperationDiagnostics.Current?.Observe("restore-recovery", detail);
        diagnosticSink?.Invoke(detail, "RestoreRecovery");
    }

    private static string Clean(object? value) => (value?.ToString() ?? "unknown").Replace('\r', ' ').Replace('\n', ' ');
}

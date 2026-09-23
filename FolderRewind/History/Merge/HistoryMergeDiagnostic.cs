using FolderRewind.History.Domain;
using System;

namespace FolderRewind.History.Merge;

public enum MergeDiagnosticCode { PreparationRequired, ExactUnavailable, Stale, MappingRequired, CoordinationScopeChanged, RecoveryRequired, PostActionWarning, PreparationFailed }
public sealed record HistoryMergeDiagnostic(MergeDiagnosticCode Code, SourceId? SourceId = null,
    VersionId? VersionId = null, RepresentationId? RepresentationId = null, string? Detail = null)
{
    public string NextActionKey => Code switch
    {
        MergeDiagnosticCode.PreparationRequired or MergeDiagnosticCode.ExactUnavailable => "Merge_Action_Prepare",
        MergeDiagnosticCode.Stale or MergeDiagnosticCode.MappingRequired => "Merge_Action_Recompute",
        MergeDiagnosticCode.RecoveryRequired => "Merge_Action_Recover",
        MergeDiagnosticCode.PostActionWarning => "Merge_Action_Maintenance",
        _ => "Merge_Action_Retry"
    };
}
public sealed class HistoryMergeBlockedException(HistoryMergeDiagnostic diagnostic) : InvalidOperationException(diagnostic.Code.ToString())
{
    public HistoryMergeDiagnostic Diagnostic { get; } = diagnostic;
}

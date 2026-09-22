using FolderRewind.History.Domain;
using System;

namespace FolderRewind.History.Merge;

public enum MergeDiagnosticCode { PreparationRequired, ExactUnavailable, Stale, MappingRequired, CoordinationScopeChanged, RecoveryRequired, PostActionWarning, PreparationFailed }
public sealed record HistoryMergeDiagnostic(MergeDiagnosticCode Code, SourceId? SourceId = null,
    VersionId? VersionId = null, RepresentationId? RepresentationId = null, string? Detail = null);
public sealed class HistoryMergeBlockedException(HistoryMergeDiagnostic diagnostic) : InvalidOperationException(diagnostic.Code.ToString())
{
    public HistoryMergeDiagnostic Diagnostic { get; } = diagnostic;
}

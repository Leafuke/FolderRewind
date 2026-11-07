namespace FolderRewind.Plugin.Abstractions;

/// <summary>Host-owned, immutable scope queries. A provider may reduce but never expand this boundary.</summary>
public interface IBackupSourceBoundaryView
{
    bool ContainsFile(string relativePath);
    bool MayIntersectDirectory(string relativePath);
}

public sealed record BackupCoordinationSource(FolderSnapshot Folder, IBackupSourceBoundaryView Boundary)
{
    /// <summary>Logical Source identity; Folder.Path is the actual physical input binding for this operation.</summary>
    public string LogicalPath { get; init; } = Folder.Path;
}
public sealed record BackupRunCoordinationRequest(Guid OperationId, ConfigSnapshot Config,
    IReadOnlyList<BackupCoordinationSource> Sources, ConsistencyIntent Intent);

public enum BackupCoordinationState { Ready = 0, Busy = 1, NeedsReset = 2 }
public enum BackupTerminalDispatch { NotRequired = 0, Submitted = 1, Failed = 2 }
public sealed record BackupCoordinationStatus(BackupCoordinationState State, IReadOnlyList<PluginDiagnostic> Diagnostics);
public sealed record BackupSourceCoordinationCompletion(Guid FolderId, OperationOutcome Outcome, bool CreatedNewArchive);
public sealed record BackupRunCoordinationCompletion(OperationOutcome Outcome,
    IReadOnlyList<BackupSourceCoordinationCompletion> Sources, BackupTerminalDispatch TerminalDispatch);

/// <summary>No external request was sent. The Host must release its configuration lock before readmission.</summary>
public sealed class BackupCoordinationAdmissionRequiredException() : InvalidOperationException("Backup coordination needs exclusive channel readmission.");

/// <summary>Optional run-level coordination; creation observes inputs without contacting an external peer.</summary>
public interface IBackupRunCoordinationCapability : IPluginCapability
{
    ConfigKindRef Kind { get; }
    ValueTask<IBackupRunCoordinationSession> CreateSessionAsync(BackupRunCoordinationRequest request, PluginInvocationContext context);
    ValueTask<BackupCoordinationStatus> GetStatusAsync(PluginInvocationContext context);
    ValueTask<PluginCommandResult> ResetAsync(PluginInvocationContext context);
}

/// <summary>
/// The Host owns this session until terminal dispatch. Source leases only own source-local resources.
/// Completion and disposal are idempotent; disposal without completion must not claim remote release.
/// </summary>
public interface IBackupRunCoordinationSession : IAsyncDisposable
{
    bool RequiresExclusiveKnotLinkSession { get; }
    CancellationToken CaptureCancellation { get; }
    IReadOnlyList<PluginDiagnostic> Diagnostics { get; }
    ValueTask<IConsistencyLease> AcquireSourceAsync(BackupConsistencyRequest request, PluginInvocationContext context);
    /// <summary>Called only when this capture no longer reads the live source (including metadata).</summary>
    void ReportSourceReadCompleted(Guid folderId);
    ValueTask<IReadOnlyList<PluginDiagnostic>> CompleteAsync(BackupRunCoordinationCompletion completion, CancellationToken cancellationToken);
}

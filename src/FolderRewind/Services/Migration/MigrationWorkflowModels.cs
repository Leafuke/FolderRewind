using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace FolderRewind.Services;

internal enum MigrationWorkflowStage { AwaitingConfirmation, Processing, AwaitingRestart, NeedsAttention, AwaitingPermissions, Completed, PartiallyCompleted, Paused }
internal enum MigrationProjectPhase { Pending, AwaitingConfiguration, AwaitingHistory, AwaitingPaths, AwaitingPassword, AwaitingArchiveChoice, AwaitingBranchChoice, ReconciliationRequired, AwaitingArchiveResolution, Blocked, Verified, Completed, AwaitingCloudConnection }
internal sealed record MigrationSourcePath(string SourceId, string Name, string Path);
internal sealed record MigrationProjectPaths(string BackupRoot, ImmutableArray<MigrationSourcePath> Sources);
internal sealed record MigrationArchiveSelection(string RepresentationId, string Path, long Size, string Sha256, bool ConfirmedLegacy);
internal sealed record MigrationBranchSelection(string SourceId, string? UpdateId);
internal sealed record MigrationArchiveCandidate(string Path, long Size, string Sha256, bool IdentityVerified, bool ArchiveVerified = false, DateTimeOffset? ModifiedAtUtc = null);
internal sealed record MigrationArchiveChoice(string RepresentationId, string SourceName, string FileName, ImmutableArray<MigrationArchiveCandidate> Candidates);
internal sealed record MigrationBranchCandidate(string UpdateId, string Name, string VersionId);
internal sealed record MigrationBranchChoice(string SourceId, string SourceName, bool HasHistory, bool RequiresReconciliation,
    string? SuggestedUpdateId, ImmutableArray<MigrationBranchCandidate> Candidates);

internal sealed record MigrationWorkflowProject(string ConfigId, string Name)
{
    public MigrationProjectPhase Phase { get; init; }
    public MigrationProjectPaths? Paths { get; init; }
    public string ConfigurationSignature { get; init; } = "";
    public string ConfigImportId { get; init; } = "";
    public bool RecoveryVerified { get; init; }
    public string RecoveryEvidence { get; init; } = "";
    public bool ImportPending { get; init; }
    public bool RetainMissingArchives { get; init; }
    public bool BackupSelected { get; init; } = true;
    public bool CloudSelected { get; init; }
    public bool CanConfirmBackup { get; init; }
    public bool CanConfirmCloud { get; init; }
    public bool BackupPermissionGranted { get; init; }
    public bool CloudPermissionGranted { get; init; }
    public bool CloudConfigured { get; init; }
    public bool CloudEnabled { get; init; }
    public int ReadyVersions { get; init; }
    public int TotalVersions { get; init; }
    public int FullCaptureSources { get; init; }
    public ImmutableArray<string> Diagnostics { get; init; } = [];
    public ImmutableArray<MigrationArchiveSelection> ArchiveSelections { get; init; } = [];
    public ImmutableArray<MigrationBranchSelection> BranchSelections { get; init; } = [];
}

internal sealed record MigrationWorkflowDocument(string WorkflowId, ImmutableArray<MigrationWorkflowProject> Projects)
{
    public int FormatVersion { get; init; } = 1;
    public MigrationWorkflowStage Stage { get; init; } = MigrationWorkflowStage.AwaitingConfirmation;
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string PackagePath { get; init; } = "";
    public string PackageSha256 { get; init; } = "";
    public bool IncludeConfiguration { get; init; } = true;
    public bool MetadataOnly { get; init; }
    public bool ImportCommitted { get; init; }
    public bool ResumeAfterActivation { get; init; }
    public string ActivationToken { get; init; } = "";
    public ImmutableArray<string> ImportProjectIds { get; init; } = [];
    public bool IsFinished => Stage is MigrationWorkflowStage.Completed or MigrationWorkflowStage.PartiallyCompleted;
}

internal sealed record MigrationProjectInspection(string ConfigId, string ConfigurationSignature, string ConfigImportId,
    ImmutableArray<MigrationArchiveChoice> Archives, ImmutableArray<MigrationBranchChoice> Branches)
{
    public MigrationProjectPhase? BlockingPhase { get; init; }
    public ImmutableArray<string> Diagnostics { get; init; } = [];
    public bool CloudEnabled { get; init; }
    public object? Context { get; init; }
    public MigrationProjectPaths? CurrentPaths { get; init; }
}
internal sealed record MigrationRecoveryDecision(MigrationProjectPhase? Attention,
    ImmutableArray<MigrationArchiveSelection> Archives, ImmutableArray<MigrationBranchSelection> Branches);
internal sealed record MigrationWorkflowRecovery(int ReadyVersions, int TotalVersions, int FullCaptureSources,
    bool CredentialReady, ImmutableArray<string> Diagnostics, string Evidence = "");
internal sealed record MigrationWorkflowPermission(bool CanBackup, bool CanCloud, bool CloudEnabled,
    bool BackupReleased, bool CloudReleased, ImmutableArray<string> Diagnostics)
{
    internal bool CloudConfigured { get; init; }
}

internal interface IMigrationWorkflowBackend
{
    System.Threading.Tasks.Task<bool> IsRecoveryCurrentAsync(MigrationWorkflowProject project, System.Threading.CancellationToken token);
    System.Threading.Tasks.Task<MigrationProjectInspection> InspectAsync(MigrationWorkflowProject project, System.Threading.CancellationToken token);
    System.Threading.Tasks.Task<MigrationWorkflowRecovery> RecoverAsync(MigrationProjectInspection inspection, MigrationRecoveryDecision decision, System.Threading.CancellationToken token);
    System.Threading.Tasks.Task<MigrationWorkflowPermission> InspectPermissionsAsync(MigrationWorkflowProject project, System.Threading.CancellationToken token);
    System.Threading.Tasks.Task GrantAsync(MigrationWorkflowProject project, System.Threading.CancellationToken token);
}

internal static class MigrationWorkflowPolicy
{
    internal static MigrationRecoveryDecision Decide(MigrationProjectInspection inspection, MigrationWorkflowProject project)
    {
        if (inspection.BlockingPhase is { } blocked) return new(blocked, [], []);
        var archives = ImmutableArray.CreateBuilder<MigrationArchiveSelection>();
        var branches = ImmutableArray.CreateBuilder<MigrationBranchSelection>();
        bool archiveChoice = false, branchChoice = false, reconcile = false;
        var sameEvidence = project.ConfigurationSignature == inspection.ConfigurationSignature && project.ConfigImportId == inspection.ConfigImportId;
        foreach (var group in inspection.Archives)
        {
            var previous = sameEvidence ? project.ArchiveSelections.Find(a => a.RepresentationId == group.RepresentationId) : null;
            var chosen = previous is null ? null : group.Candidates.Find(c => c.Path == previous.Path && c.Size == previous.Size && c.Sha256 == previous.Sha256);
            chosen ??= group.Candidates.Length == 1 ? group.Candidates[0] : null;
            if (chosen is null) { if (group.Candidates.Length > 1) archiveChoice = true; continue; }
            var confirmed = previous is not null && chosen.Path == previous.Path && chosen.Size == previous.Size && chosen.Sha256 == previous.Sha256 && previous.ConfirmedLegacy;
            if (!chosen.IdentityVerified && !confirmed) { archiveChoice = true; continue; }
            archives.Add(new(group.RepresentationId, chosen.Path, chosen.Size, chosen.Sha256, confirmed));
        }
        foreach (var source in inspection.Branches)
        {
            if (!source.HasHistory) { branches.Add(new(source.SourceId, null)); continue; }
            var previous = sameEvidence ? project.BranchSelections.Find(b => b.SourceId == source.SourceId) : null;
            var selected = previous is not null && source.Candidates.Exists(c => c.UpdateId == previous.UpdateId) ? previous.UpdateId : source.SuggestedUpdateId;
            if (selected is null || !source.Candidates.Exists(c => c.UpdateId == selected))
            { reconcile |= source.RequiresReconciliation; branchChoice = true; continue; }
            branches.Add(new(source.SourceId, selected));
        }
        return new(reconcile ? MigrationProjectPhase.ReconciliationRequired : archiveChoice ? MigrationProjectPhase.AwaitingArchiveChoice
            : branchChoice ? MigrationProjectPhase.AwaitingBranchChoice : null, archives.ToImmutable(), branches.ToImmutable());
    }

    internal static MigrationWorkflowStage StageFor(ImmutableArray<MigrationWorkflowProject> projects)
    {
        if (projects.Length > 0 && projects.TrueForAll(p => p.Phase == MigrationProjectPhase.Completed))
            return projects.Exists(p => p.ReadyVersions < p.TotalVersions) ? MigrationWorkflowStage.PartiallyCompleted : MigrationWorkflowStage.Completed;
        return projects.TrueForAll(p => p.Phase is MigrationProjectPhase.Verified or MigrationProjectPhase.Completed)
            ? MigrationWorkflowStage.AwaitingPermissions : MigrationWorkflowStage.NeedsAttention;
    }

    private static T? Find<T>(this ImmutableArray<T> items, Predicate<T> predicate) where T : class
    { foreach (var item in items) if (predicate(item)) return item; return null; }
    private static bool Exists<T>(this ImmutableArray<T> items, Predicate<T> predicate)
    { foreach (var item in items) if (predicate(item)) return true; return false; }
    private static bool TrueForAll<T>(this ImmutableArray<T> items, Predicate<T> predicate)
    { foreach (var item in items) if (!predicate(item)) return false; return true; }
}

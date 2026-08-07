using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Models;

public enum BackupConfigDraftReconciliation
{
    NewConfiguration = 0,
    UpToDate = 1,
    NewResources = 2
}

public sealed class BackupConfigDraftIssue
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool IsBlocking { get; init; }
}

/// <summary>
/// Non-persistent result of selecting discovery resources and applying a preset snapshot.
/// </summary>
public sealed class BackupConfigDraft
{
    public string DraftId { get; init; } = Guid.NewGuid().ToString("N");
    public required DiscoveredGameCandidate Game { get; init; }
    public required BackupSetCandidate BackupSet { get; init; }
    public required BackupPreset AppliedPreset { get; init; }
    public required BackupConfig ProposedConfig { get; init; }
    public BackupConfig? ExistingConfig { get; init; }
    public BackupConfigDraftReconciliation Reconciliation { get; init; }
    public IReadOnlyList<BackupResourceCandidate> SelectedResources { get; init; }
        = Array.Empty<BackupResourceCandidate>();
    public IReadOnlyList<ManagedFolder> FoldersToAdd { get; init; }
        = Array.Empty<ManagedFolder>();
    public IReadOnlyList<BackupManagedFolderUpdate> FolderUpdates { get; init; }
        = Array.Empty<BackupManagedFolderUpdate>();
    public IReadOnlyList<BackupConfigDraftIssue> Issues { get; init; }
        = Array.Empty<BackupConfigDraftIssue>();
    public bool IsSelected { get; set; } = true;
    public bool IsCommittable => IsSelected
                                 && Reconciliation != BackupConfigDraftReconciliation.UpToDate
                                 && Issues.All(issue => !issue.IsBlocking);
}

public sealed class BackupManagedFolderUpdate
{
    public required ManagedFolder ExistingFolder { get; init; }
    public bool SetSelectionToAll { get; init; }
    public IReadOnlyList<string> IncludePatternsToAdd { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ResourceIdsToAdd { get; init; } = Array.Empty<string>();
}

public sealed class BackupConfigDraftCommitResult
{
    public bool Success { get; init; }
    public int AddedConfigurationCount { get; init; }
    public int AddedSourceCount { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
}

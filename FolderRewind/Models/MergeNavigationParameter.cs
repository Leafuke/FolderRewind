using FolderRewind.History.Domain;
using System;

namespace FolderRewind.Models;

public sealed record HistoryReturnContext(string ConfigId, string FolderId, BranchId? SelectedBranch,
    string Search, HistoryPresentationMode Presentation, double ScrollOffset = 0, CheckpointId? FocusCheckpoint = null,
    bool OpenSafetySnapshots = false);
public sealed record MergeNavigationParameter(string ConfigId, string FolderId, BranchId? SourceBranch = null,
    Guid? SessionId = null, HistoryReturnContext? ReturnContext = null);

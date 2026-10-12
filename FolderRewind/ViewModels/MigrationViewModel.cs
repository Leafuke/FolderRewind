using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace FolderRewind.ViewModels;

public sealed class MigrationReviewItem : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Details { get; init; } = "";
    public string Status { get; init; } = "";
    public bool CanSelect { get; init; } = true;
    public bool CanSelectBackup { get; init; }
    public bool CanSelectCloud { get; init; }
    public bool ShowCapabilities { get; init; }
    public bool ShowSelection { get; init; } = true;
    public bool HasStatus => Status.Length > 0;
    internal object? Value { get; init; }
    private bool _selected = true, _backup, _cloud;
    public bool Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public bool BackupSelected { get => _backup; set => SetProperty(ref _backup, value); }
    public bool CloudSelected { get => _cloud; set => SetProperty(ref _cloud, value); }
}

public sealed class MigrationReviewViewModel : ObservableObject
{
    public ObservableCollection<MigrationReviewItem> Items { get; } = [];
    public ObservableCollection<string> ModeLabels { get; } = [];
    public string Description { get; init; } = "";
    private BackupConfig[] _mappingProjects = [];
    private string _oldRoot = "", _newRoot = "", _previewError = "";
    private bool _hasPathPreview;
    public bool ShowPathMapping { get; init; }
    public string OldRoot { get => _oldRoot; set { if (SetProperty(ref _oldRoot, value)) InvalidatePathPreview(); } }
    public string NewRoot { get => _newRoot; set { if (SetProperty(ref _newRoot, value)) InvalidatePathPreview(); } }
    public string PreviewError { get => _previewError; private set => SetProperty(ref _previewError, value); }
    public bool CanSavePaths => _hasPathPreview && SelectedItems.Any();
    private void InvalidatePathPreview() { _hasPathPreview = false; OnPropertyChanged(nameof(CanSavePaths)); }
    internal void SetMappingProjects(BackupConfig[] projects) => _mappingProjects = projects;
    public void PreviewPaths()
    {
        try
        {
            var previews = MigrationProjectSetupService.PreviewMappings(_mappingProjects, OldRoot, NewRoot);
            foreach (var item in Items) item.PropertyChanged -= OnItemChanged;
            Items.Clear();
            foreach (var preview in previews) Add(new()
            {
                Id = preview.Original.Id, Name = preview.Original.Name, Details = preview.Original.Id, Value = preview,
                Status = I18n.Format("Migration_ConfigPaths", preview.Draft.SourceFolders.Count,
                    string.Join(Environment.NewLine, preview.Draft.SourceFolders.Select(f => f.Path)), preview.Draft.DestinationPath)
            });
            PreviewError = previews.Count == 0 ? I18n.GetString("Migration_NoMatchingPaths") : "";
            _hasPathPreview = previews.Count > 0;
            OnPropertyChanged(nameof(CanSavePaths)); OnPropertyChanged(nameof(SelectionSummary));
        }
        catch (Exception error) { PreviewError = CloudCommandSecurity.Redact(error.Message); InvalidatePathPreview(); }
    }
    public bool ExportSelection { get; init; }
    public bool PreviewOnly { get; init; }
    public bool ShowModes => ModeLabels.Count > 0;
    public bool ShowList => !ExportSelection || SelectedMode == 1;
    public bool ShowSelectionActions => ShowList && !PreviewOnly;
    public string SelectionSummary => I18n.Format("Migration_SelectionCount", PreviewOnly ? Items.Count : SelectedItems.Count(), Items.Count);
    public IEnumerable<MigrationReviewItem> SelectedItems => Items.Where(row => row.CanSelect && (ExportSelection && SelectedMode == 0 || row.Selected));
    private int _selectedMode;
    public int SelectedMode
    {
        get => _selectedMode;
        set { if (SetProperty(ref _selectedMode, value)) { OnPropertyChanged(nameof(ShowList)); OnPropertyChanged(nameof(ShowSelectionActions)); OnPropertyChanged(nameof(SelectionSummary)); } }
    }
    public void Add(MigrationReviewItem item)
    {
        Items.Add(item);
        item.PropertyChanged += OnItemChanged;
        OnPropertyChanged(nameof(SelectionSummary));
    }
    private void OnItemChanged(object? sender, PropertyChangedEventArgs args)
    { OnPropertyChanged(nameof(SelectionSummary)); OnPropertyChanged(nameof(CanSavePaths)); }
    public void SelectAll(bool selected) { foreach (var item in Items) if (item.CanSelect) item.Selected = selected; }
}

public sealed class MigrationViewModel : ObservableObject
{
    public ObservableCollection<MigrationReviewItem> Projects { get; } = [];
    private bool _busy, _pendingRestart, _hasPending, _legacy, _canCancel, _hasReport;
    public bool HasReport { get => _hasReport; set { if (SetProperty(ref _hasReport, value)) OnPropertyChanged(nameof(ShowPanel)); } }
    public bool ShowPanel => Busy || PendingRestart || HasPending || Legacy || HasReport;
    public bool CanCancel { get => _canCancel; set => SetProperty(ref _canCancel, value); }
    private string _summary = I18n.GetString("Migration_DefaultSummary");
    private bool _hasPathWork;
    public bool HasPathWork { get => _hasPathWork; set => SetProperty(ref _hasPathWork, value); }
    public bool Busy { get => _busy; set { if (SetProperty(ref _busy, value)) NotifyActions(); } }
    public bool PendingRestart { get => _pendingRestart; set { if (SetProperty(ref _pendingRestart, value)) NotifyActions(); } }
    public bool HasPending { get => _hasPending; set { if (SetProperty(ref _hasPending, value)) NotifyActions(); } }
    public bool Legacy { get => _legacy; set { if (SetProperty(ref _legacy, value)) NotifyActions(); } }
    public string Summary { get => _summary; set => SetProperty(ref _summary, value); }
    public bool CanOperate => !Busy;
    public bool ShowRestart => PendingRestart;
    public bool ShowProgress => Busy;
    public bool ShowSetup => HasPending;
    public bool ShowLegacy => Legacy;
    private void NotifyActions()
    {
        foreach (var name in new[] { nameof(ShowPanel), nameof(CanOperate), nameof(ShowRestart), nameof(ShowProgress), nameof(ShowSetup), nameof(ShowLegacy) }) OnPropertyChanged(name);
    }
    internal void Apply(IReadOnlyList<MigrationProjectAssessment> results)
    {
        results = results.Where(result => result.IsMigrated).ToArray();
        HasPathWork = results.Any(r => r.Backup == MigrationCapabilityStatus.Blocked);
        Projects.Clear();
        foreach (var result in results) Projects.Add(ToItem(result));
        var ready = results.Count(r => r.CanConfirmBackup || r.CanConfirmCloud);
        var blocked = results.Count(r => !r.CanConfirmBackup && !r.CanConfirmCloud && (r.Backup == MigrationCapabilityStatus.Blocked || r.Cloud == MigrationCapabilityStatus.Blocked));
        var released = results.Count - ready - blocked;
        HasPending = ready > 0 || blocked > 0;
        Summary = results.Count == 0 ? I18n.GetString("Migration_DefaultSummary") : I18n.Format("Migration_BatchSummary", Math.Max(0, released), ready, blocked);
    }
    internal static MigrationReviewItem ToItem(MigrationProjectAssessment result) => new()
    {
        Id = result.Snapshot.Id, Name = result.Snapshot.Name, Details = result.Snapshot.Id,
        Status = I18n.Format("Migration_CapabilitySummary", I18n.GetString("Migration_State_" + result.Backup),
            result.BackupReason + (result.Snapshot.Automation.AutoBackupEnabled ? "" : " " + I18n.GetString("Migration_KeptOff")), I18n.GetString("Migration_State_" + result.Cloud), result.CloudReason + (result.Snapshot.Cloud.Enabled ? "" : " " + I18n.GetString("Migration_KeptOff"))),
        Value = result, CanSelect = result.CanConfirmBackup || result.CanConfirmCloud,
        Selected = result.CanConfirmBackup || result.CanConfirmCloud,
        CanSelectBackup = result.CanConfirmBackup, CanSelectCloud = result.CanConfirmCloud,
        BackupSelected = result.CanConfirmBackup, CloudSelected = result.CanConfirmCloud,
        ShowCapabilities = true
    };
}

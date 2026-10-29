using FolderRewind.Models;
using FolderRewind.History.Domain;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace FolderRewind.ViewModels;

public sealed record MigrationWizardResultItem(string Name, string Status, string Content);

public sealed class MigrationWizardSourceItem : ObservableObject
{
    public string Id { get; }
    public string Name { get; }
    public string AutomationId => "MigrationSourcePath_" + Id;
    public string PickerAutomationId => "MigrationSourcePicker_" + Id;
    public string IssueAutomationId => "MigrationIssueSourcePath_" + Id;
    public string IssuePickerAutomationId => "MigrationIssueSourcePicker_" + Id;
    private string _path;
    public string Path { get => _path; set => SetProperty(ref _path, value); }
    internal MigrationWizardSourceItem(MigrationSourcePath source) { Id = source.SourceId; Name = source.Name; _path = source.Path; }
}

public sealed class MigrationWizardProjectItem : ObservableObject
{
    public string Id { get; }
    public string Name { get; }
    public string SelectionAutomationId => "MigrationChooseProject_" + Id;
    public string ExportSelectionAutomationId => "MigrationExportProject_" + Id;
    public string ImportSelectionAutomationId => "MigrationImportProject_" + Id;
    public string OriginalSelectionAutomationId => "MigrationOriginalProject_" + Id;
    public string HandleAutomationId => "MigrationHandleProject_" + Id;
    public string BackupPathAutomationId => "MigrationBackupPath_" + Id;
    public string BackupPickerAutomationId => "MigrationBackupPicker_" + Id;
    public string BackupPermissionAutomationId => "MigrationBackupPermission_" + Id;
    public string CloudPermissionAutomationId => "MigrationCloudPermission_" + Id;
    public ObservableCollection<MigrationWizardSourceItem> Sources { get; } = [];
    private bool _selected = true, _backup = true, _cloud, _autoBackup, _cloudPreference;
    private string _backupRoot;
    public bool Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public bool BackupSelected { get => _backup; set => SetProperty(ref _backup, value); }
    public bool CloudSelected { get => _cloud; set { if (SetProperty(ref _cloud, value)) OnPropertyChanged(nameof(CanSelectCloud)); } }
    public bool AutoBackup { get => _autoBackup; set => SetProperty(ref _autoBackup, value); }
    public bool CloudPreference { get => _cloudPreference; set => SetProperty(ref _cloudPreference, value); }
    public string BackupRoot { get => _backupRoot; set => SetProperty(ref _backupRoot, value); }
    public string Status { get; internal set; } = "";
    public string Summary => State is null ? Status : I18n.GetString("MigrationWizard_PhaseShort_" + State.Phase);
    public string NextActionText => I18n.GetString(State?.Phase switch
    {
        MigrationProjectPhase.AwaitingConfiguration => "MigrationWizard_ActionConfiguration",
        MigrationProjectPhase.AwaitingHistory => "MigrationWizard_ActionHistory",
        MigrationProjectPhase.AwaitingPaths => "MigrationWizard_ActionPaths",
        MigrationProjectPhase.AwaitingPassword => "MigrationWizard_ActionPassword",
        MigrationProjectPhase.AwaitingCloudConnection => "MigrationWizard_ActionCloudConnection",
        MigrationProjectPhase.AwaitingArchiveChoice or MigrationProjectPhase.AwaitingArchiveResolution => "MigrationWizard_ActionArchives",
        MigrationProjectPhase.AwaitingBranchChoice or MigrationProjectPhase.ReconciliationRequired => "MigrationWizard_ActionBranch",
        _ => "MigrationWizard_ActionDetails"
    });
    private readonly bool _originalAutoBackup, _originalCloud;
    public string OriginalOperationsText => I18n.Format("MigrationWizard_OriginalOperations",
        I18n.GetString(_originalAutoBackup ? "MigrationWizard_On" : "MigrationWizard_Off"),
        I18n.GetString(_originalCloud ? "MigrationWizard_On" : "MigrationWizard_Off"));
    public bool ShowPermissions => State?.Phase == MigrationProjectPhase.Verified;
    public bool CanSelectBackup => !BackupPermissionGranted && State?.CanConfirmBackup == true;
    public bool CanSelectCloud => !CloudPermissionGranted && (State?.CanConfirmCloud == true || CloudSelected);
    public bool BackupPermissionGranted => State?.BackupPermissionGranted == true;
    public bool CloudPermissionGranted => State?.CloudPermissionGranted == true;
    public bool NeedsCloudConnection => State is { RecoveryVerified: true, CloudConfigured: true, CanConfirmCloud: false, CloudPermissionGranted: false } state
        && (state.BackupPermissionGranted || state.CanConfirmBackup);
    public string PermissionSummary => State is not { RecoveryVerified: true } state ? "" : I18n.Format("MigrationWizard_PermissionSummary",
        I18n.GetString(state.BackupPermissionGranted ? "MigrationWizard_PermissionGranted" : state.CanConfirmBackup
            ? "MigrationWizard_PermissionAwaitingConfirmation" : "MigrationWizard_PermissionNeedsCheck"),
        I18n.GetString(state.CloudPermissionGranted ? "MigrationWizard_PermissionGranted" : !state.CloudConfigured
            ? "MigrationWizard_CloudNotConfigured" : state.CanConfirmCloud ? "MigrationWizard_PermissionAwaitingConfirmation" : "MigrationWizard_CloudNeedsConnection"));
    public bool CloudEnabled => State?.CloudEnabled == true;
    internal bool AwaitingActivation { get; init; }
    public bool CanHandle => !AwaitingActivation && State is { Phase: not (MigrationProjectPhase.Verified or MigrationProjectPhase.Completed) };
    internal MigrationWorkflowProject? State { get; }
    internal MigrationOriginalSettings? OriginalSettings { get; init; }
    internal MigrationWizardProjectItem(string id, string name, MigrationProjectPaths paths, MigrationWorkflowProject? state = null)
    {
        Id = id; Name = name; _backupRoot = paths.BackupRoot; State = state;
        var config = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(c => new HistoryConfigId(id).Matches(c.Id));
        _originalAutoBackup = config?.Automation.AutoBackupEnabled == true;
        _originalCloud = config?.Cloud.Enabled == true;
        foreach (var source in paths.Sources) Sources.Add(new(source));
        if (state is not null)
        {
            _backup = state.BackupSelected; _cloud = state.CloudSelected;
            Status = I18n.GetString("MigrationWizard_Phase_" + state.Phase);
            if (state.RecoveryVerified) Status += "\n" + I18n.Format("MigrationWizard_VersionCounts", state.ReadyVersions, state.TotalVersions);
            if (state.Phase == MigrationProjectPhase.Completed && state.CloudConfigured && !state.CloudPermissionGranted)
                Status += "\n" + I18n.GetString("MigrationWizard_CloudStillPaused");
            if (state.FullCaptureSources > 0) Status += "\n" + I18n.Format("MigrationWizard_FullNext", state.FullCaptureSources);
        }
    }
    internal MigrationProjectPaths CapturePaths() => new(BackupRoot, [.. Sources.Select(s => new MigrationSourcePath(s.Id, s.Name, s.Path))]);
}

public sealed class MigrationWizardArchiveItem : ObservableObject
{
    public string Id { get; }
    public string Name { get; }
    public string AutomationId => "MigrationArchiveChoice_" + Id;
    public string ConfirmAutomationId => "MigrationConfirmArchive_" + Id;
    public string ChooseAutomationId => "MigrationPickArchive_" + Id;
    public bool NeedsChoice => _candidates.Count != 1 || NeedsLegacyConfirmation;
    public ObservableCollection<string> Options { get; } = [];
    private readonly List<MigrationArchiveCandidate> _candidates = [];
    private int _selectedIndex = -1;
    private bool _confirmed;
    public int SelectedIndex
    {
        get => _selectedIndex;
        set { if (SetProperty(ref _selectedIndex, value)) { ConfirmLegacy = false; OnPropertyChanged(nameof(NeedsLegacyConfirmation)); OnPropertyChanged(nameof(SelectedPath)); OnPropertyChanged(nameof(NeedsChoice)); } }
    }
    public bool ConfirmLegacy { get => _confirmed; set => SetProperty(ref _confirmed, value); }
    public bool NeedsLegacyConfirmation => SelectedIndex >= 0 && SelectedIndex < _candidates.Count && !_candidates[SelectedIndex].IdentityVerified;
    public string SelectedPath => SelectedIndex >= 0 && SelectedIndex < _candidates.Count ? _candidates[SelectedIndex].Path : I18n.GetString("MigrationWizard_ArchiveMissing");
    internal MigrationWizardArchiveItem(MigrationArchiveChoice choice, MigrationWorkflowProject project)
    {
        Id = choice.RepresentationId; Name = choice.SourceName + " · " + choice.FileName;
        foreach (var candidate in choice.Candidates) Add(candidate);
        var previous = project.ArchiveSelections.FirstOrDefault(s => s.RepresentationId == Id);
        _selectedIndex = previous is null ? (_candidates.Count == 1 ? 0 : -1) : _candidates.FindIndex(c => c.Path == previous.Path && c.Sha256 == previous.Sha256 && c.Size == previous.Size);
        _confirmed = previous?.ConfirmedLegacy == true && _selectedIndex >= 0;
    }
    internal void Add(MigrationArchiveCandidate candidate)
    {
        var existing = _candidates.FindIndex(c => c.Path == candidate.Path && c.Sha256 == candidate.Sha256);
        if (existing >= 0) { SelectedIndex = existing; return; }
        _candidates.Add(candidate);
        Options.Add(candidate.Path + " · " + (candidate.Size / 1024d / 1024d).ToString("0.##") + " MB · "
            + I18n.GetString(candidate.IdentityVerified ? "Migration_HashMatched" : candidate.ArchiveVerified ? "MigrationWizard_ArchiveVerified" : "MigrationWizard_ArchiveUnverified")
            + (candidate.ModifiedAtUtc is { } modified ? " · " + modified.LocalDateTime.ToString("g") : ""));
        SelectedIndex = _candidates.Count - 1;
    }
    internal MigrationArchiveSelection? Capture() => SelectedIndex >= 0 && SelectedIndex < _candidates.Count
        ? new(Id, _candidates[SelectedIndex].Path, _candidates[SelectedIndex].Size, _candidates[SelectedIndex].Sha256, ConfirmLegacy) : null;
}

public sealed class MigrationWizardBranchItem : ObservableObject
{
    private readonly MigrationBranchChoice _choice;
    private int _selectedIndex;
    public string Name => _choice.SourceName;
    public string AutomationId => "MigrationBranchChoice_" + _choice.SourceId;
    public ObservableCollection<string> Options { get; } = [];
    public bool ShowSelector => _choice.HasHistory && (_choice.Candidates.Length > 1 || _choice.SuggestedUpdateId is null) && !_choice.RequiresReconciliation;
    public string Summary => !_choice.HasHistory ? I18n.GetString("Migration_NoHistoricalBranch")
        : _choice.RequiresReconciliation && _choice.Candidates.IsEmpty ? I18n.GetString("Migration_BranchReconciliation")
        : _selectedIndex >= 0 ? I18n.Format("MigrationWizard_ContinueBranch", _choice.Candidates[_selectedIndex].Name) : I18n.GetString("MigrationWizard_ChooseBranch");
    public int SelectedIndex { get => _selectedIndex; set { if (SetProperty(ref _selectedIndex, value)) OnPropertyChanged(nameof(Summary)); } }
    internal MigrationWizardBranchItem(MigrationBranchChoice choice, MigrationWorkflowProject project)
    {
        _choice = choice; foreach (var candidate in choice.Candidates) Options.Add(candidate.Name + " · " + candidate.VersionId[..8]);
        var previous = project.BranchSelections.FirstOrDefault(b => b.SourceId == choice.SourceId)?.UpdateId ?? choice.SuggestedUpdateId;
        _selectedIndex = choice.Candidates.ToList().FindIndex(c => c.UpdateId == previous);
    }
    internal MigrationBranchSelection Capture() => new(_choice.SourceId, SelectedIndex >= 0 && SelectedIndex < _choice.Candidates.Length ? _choice.Candidates[SelectedIndex].UpdateId : null);
}

using FolderRewind.History.Domain;
using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed class MigrationWizardViewModel : ObservableObject, IDisposable
{
    private enum WizardStep { Source, Selection, Destination, Recovery, Result }
    private MigrationWizardRequest _request = new(MigrationWizardMode.Import);
    private WizardStep _step;
    private DataMigrationPreview? _preview;
    private MigrationWorkflowDocument? _document;
    private MigrationProjectInspection? _inspection;
    private CancellationTokenSource? _operation;
    private bool _busy, _disposed, _includeConfiguration = true;
    private int _transportIndex, _archiveIndex, _connectionIndex = -1, _snapshotIndex = -1;
    private string _message = "", _packagePath = "", _remoteRoot = "", _oldRoot = "", _newRoot = "", _errorDetails = "";
    private MigrationWizardProjectItem? _selectedProject;
    private bool _wideRecovery = true, _detailOpen;
    private SemanticStatus _messageStatus = SemanticStatus.Info;
    private sealed record ProjectEditor(MigrationProjectInspection Inspection, MigrationWizardArchiveItem[] Archives,
        MigrationWizardBranchItem[] Branches, MigrationProjectPaths Paths, MigrationProjectPhase Phase);
    private readonly Dictionary<string, ProjectEditor> _editors = [];
    private CloudConnectionProfile[] _connections = [];
    private MigrationCloudSnapshot[] _snapshots = [];
    private string[] _legacySnapshots = [];

    public ObservableCollection<MigrationWizardProjectItem> Projects { get; } = [];
    public ObservableCollection<MigrationWizardArchiveItem> Archives { get; } = [];
    public ObservableCollection<MigrationWizardBranchItem> Branches { get; } = [];
    public ObservableCollection<string> Connections { get; } = [];
    public ObservableCollection<string> Snapshots { get; } = [];
    public ObservableCollection<string> TransportOptions { get; } = [I18n.GetString("Settings_DataTransferMode_Local"), I18n.GetString("Settings_DataTransferMode_Cloud")];
    public ObservableCollection<string> ArchiveOptions { get; } = [I18n.GetString("MigrationWizard_ExternalArchives"), I18n.GetString("MigrationWizard_EmbeddedArchives")];
    public bool Busy { get => _busy; private set { if (SetProperty(ref _busy, value)) Notify(); } }
    public bool CanOperate => !Busy;
    public bool IsExport => _request.Mode == MigrationWizardMode.Export;
    public bool IsOriginalSettings => _request.Mode == MigrationWizardMode.OriginalSettings;
    public bool ShowExportProjects => IsExport && _step == WizardStep.Source;
    public bool ShowExportContent => IsExport && _step == WizardStep.Selection;
    public bool ShowArchiveOptions => ShowExportContent && _request.Scope is DataMigrationScope.ConfigurationAndHistory or DataMigrationScope.History;
    public bool ShowTransport => IsExport ? _step == WizardStep.Destination : _step == WizardStep.Source;
    public bool ShowCloud => ShowTransport && TransportIndex == 1;
    public bool ShowCloudSnapshots => ShowCloud && !IsExport;
    public bool ShowLocalPackage => ShowTransport && !IsExport && TransportIndex == 0;
    public bool ShowImportSelection => !IsExport && !IsOriginalSettings && _step == WizardStep.Selection;
    public bool ShowOriginalSelection => IsOriginalSettings && _step == WizardStep.Selection;
    public bool ShowConfigurationChoice => ShowImportSelection && _request.Scope != DataMigrationScope.MetadataOnly && _preview?.ConfigurationUtf8 is not null && _preview.Bundle is not null;
    public bool ShowRecovery => _step == WizardStep.Recovery;
    public bool ShowProjectList => ShowRecovery && (_wideRecovery || !_detailOpen || ShowPendingRestart);
    public bool ShowProjectDetails => ShowRecovery && !ShowPendingRestart && SelectedProject is not null && (_wideRecovery || _detailOpen);
    public bool ShowDetailsBack => ShowProjectDetails && !_wideRecovery;
    public bool WideRecovery => _wideRecovery;
    public void SetRecoveryWidth(double width) { if (_wideRecovery == (width >= 800)) return; _wideRecovery = width >= 800; Notify(); }
    public void ReturnToProjects() { if (Busy) return; _detailOpen = false; Notify(); }
    public SemanticStatus MessageStatus { get => _messageStatus; private set => SetProperty(ref _messageStatus, value); }
    public string RecoverySummary => I18n.Format("MigrationWizard_AttentionCount", Projects.Count, Projects.Count(p => p.CanHandle));
    public string SelectedProjectStatus => SelectedProject?.Status ?? "";
    public string SelectedOperationsText => SelectedProject?.OriginalOperationsText ?? "";
    public bool ShowSelectedPermissions => SelectedProject?.ShowPermissions == true;
    public string SelectedPermissionSummary => SelectedProject?.PermissionSummary ?? "";
    public bool SelectedNeedsCloudConnection => SelectedProject?.NeedsCloudConnection == true;
    public bool SelectedCloudConfigured => SelectedProject?.State?.CloudConfigured == true;
    public bool SelectedCanBackup => SelectedProject?.CanSelectBackup == true;
    public bool SelectedCanCloud => SelectedProject?.CanSelectCloud == true;
    public bool SelectedCloudEnabled => SelectedProject?.CloudEnabled == true;
    public bool SelectedBackup { get => SelectedProject?.BackupPermissionGranted == true || SelectedProject?.BackupSelected == true;
        set { if (SelectedProject is { BackupPermissionGranted: false } project) project.BackupSelected = value; } }
    public bool SelectedCloud { get => SelectedProject?.CloudPermissionGranted == true || SelectedProject?.CloudSelected == true;
        set { if (SelectedProject is { CloudPermissionGranted: false } project) { project.CloudSelected = value; OnPropertyChanged(nameof(SelectedCanCloud)); } } }
    public bool ShowResult => _step == WizardStep.Result;
    public bool HasIncompleteResult => ShowResult && _document?.Stage == MigrationWorkflowStage.PartiallyCompleted;
    public Task ContinueRemainingAsync() => PerformAsync(RunCoreAsync);
    internal MigrationWizardRequest? ResumeRequest => _document is { ImportCommitted: true } document
        ? new(MigrationWizardMode.Continue, WorkflowId: document.WorkflowId) : null;
    public bool ShowPendingRestart => ShowRecovery && _document?.Stage == MigrationWorkflowStage.AwaitingRestart;
    public bool ShowIssue => ShowRecovery && _selectedProject is { CanHandle: true } && !ShowPendingRestart;
    public bool ShowPassword => ShowIssue && _selectedProject?.State?.Phase == MigrationProjectPhase.AwaitingPassword;
    public bool ShowHistoryFile => ShowIssue && _selectedProject?.State?.Phase is MigrationProjectPhase.AwaitingHistory or MigrationProjectPhase.AwaitingConfiguration;
    public bool ShowReconciliation => ShowIssue && _selectedProject?.State?.Phase == MigrationProjectPhase.ReconciliationRequired;
    public bool ShowMissingArchives => ShowIssue && _selectedProject?.State?.Phase == MigrationProjectPhase.AwaitingArchiveResolution;
    public bool ShowIssuePaths => ShowIssue && _selectedProject?.State?.Phase is MigrationProjectPhase.AwaitingPaths or MigrationProjectPhase.AwaitingArchiveResolution or MigrationProjectPhase.Blocked;
    public bool CanGoBack => !Busy && _step is WizardStep.Selection or WizardStep.Destination;
    public string Message { get => _message; private set { if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public string ErrorDetails { get => _errorDetails; private set { if (SetProperty(ref _errorDetails, value)) OnPropertyChanged(nameof(HasErrorDetails)); } }
    public bool HasErrorDetails => ErrorDetails.Length > 0;
    public string PackagePath { get => _packagePath; private set => SetProperty(ref _packagePath, value); }
    public string RemoteRoot { get => _remoteRoot; set { if (SetProperty(ref _remoteRoot, value)) { _snapshots = []; _legacySnapshots = []; Snapshots.Clear(); SnapshotIndex = -1; } } }
    public string OldRoot { get => _oldRoot; set => SetProperty(ref _oldRoot, value); }
    public string NewRoot { get => _newRoot; set => SetProperty(ref _newRoot, value); }
    public int TransportIndex { get => _transportIndex; set { if (SetProperty(ref _transportIndex, value)) { _snapshots = []; _legacySnapshots = []; Snapshots.Clear(); SnapshotIndex = -1; Notify(); } } }
    public int ArchiveIndex { get => _archiveIndex; set { if (SetProperty(ref _archiveIndex, value)) Notify(); } }
    public bool IncludeConfiguration { get => _includeConfiguration; set => SetProperty(ref _includeConfiguration, value); }
    public int ConnectionIndex
    {
        get => _connectionIndex;
        set
        {
            if (!SetProperty(ref _connectionIndex, value)) return;
            if (value >= 0 && value < _connections.Length)
            { var profile = _connections[value]; RemoteRoot = profile.VerificationEvidence.Length > 65 ? profile.VerificationEvidence[65..] : profile.RemoteName + ":FolderRewind"; }
            _snapshots = []; _legacySnapshots = []; Snapshots.Clear(); SnapshotIndex = -1;
        }
    }
    public int SnapshotIndex { get => _snapshotIndex; set => SetProperty(ref _snapshotIndex, value); }
    public MigrationWizardProjectItem? SelectedProject { get => _selectedProject; private set { SetProperty(ref _selectedProject, value); Notify(); } }
    public string SelectedProjectName => SelectedProject?.Name ?? "";
    public ObservableCollection<MigrationWizardSourceItem> SelectedSources => SelectedProject?.Sources ?? [];
    public string SelectedBackupRoot { get => SelectedProject?.BackupRoot ?? ""; set { if (SelectedProject is not null) SelectedProject.BackupRoot = value; } }
    public string Title => I18n.GetString(IsExport ? "Migration_ExportTitle" : IsOriginalSettings ? "Migration_ReadOriginalTitle" : "Migration_ImportTitle");
    public string StepText => ShowResult ? I18n.GetString("MigrationWizard_Result") : ShowRecovery ? I18n.GetString(ShowPendingRestart ? "MigrationWizard_RestartStep" : "MigrationWizard_RecoveryStep")
        : I18n.GetString(IsExport ? "MigrationWizard_ExportStep_" + _step : "MigrationWizard_ImportStep_" + _step);
    public string ScopeText => I18n.GetString("MigrationWizard_Scope_" + _request.Scope);
    public string ContentDescription => I18n.GetString(_request.Scope == DataMigrationScope.MetadataOnly ? "Migration_MetadataOnly"
        : _request.Scope == DataMigrationScope.Configuration ? "MigrationWizard_ConfigurationOnly" : ArchiveIndex == 1 ? "MigrationWizard_EmbeddedDescription" : "MigrationWizard_ExternalDescription");
    public string ExternalArchiveRoots => ShowArchiveOptions && ArchiveIndex == 0
        ? string.Join("\n", Projects.Where(p => p.Selected).Select(p => p.Name + "\n" + p.BackupRoot)) : "";
    public string ImportSummary => _preview is null ? "" : I18n.Format("MigrationWizard_PackageSummary", _preview.Projects.Length,
        _preview.Projects.Sum(p => p.VersionCount), I18n.GetString(_preview.Bundle?.IncludesPayloads == true ? "MigrationWizard_HasArchives" : "MigrationWizard_NoArchives"));
    public string TechnicalDetails => SelectedProject?.State is not { } state ? "" : state.ConfigId + "\n" + state.ConfigurationSignature + "\n" + string.Join("\n", state.Diagnostics.Select(CloudCommandSecurity.Redact));
    public string PrimaryText => ShowResult ? I18n.GetString("MigrationWizard_ReturnSettings") : ShowPendingRestart ? I18n.GetString("MigrationWizard_RestartContinue")
        : ShowRecovery ? Projects.Any(p => p.ShowPermissions)
            ? I18n.Format("MigrationWizard_FinishReadyCount", Projects.Count(p => p.ShowPermissions)) : I18n.GetString("MigrationWizard_ApplyAndRecheck")
        : IsExport && _step == WizardStep.Destination ? I18n.GetString("MigrationWizard_StartExport")
        : !IsExport && _step == WizardStep.Selection ? I18n.GetString(IsOriginalSettings ? "MigrationWizard_ApplyOriginal" : "MigrationWizard_StartImport") : I18n.GetString("Migration_Next");
    internal event Func<IReadOnlyList<string>, Task<string?>>? PickPackage;
    internal event Func<string, Task<string?>>? PickExport;
    internal event Func<Task>? Restart;

    internal async Task InitializeAsync(MigrationWizardRequest request)
    {
        _request = request;
        _connections = ConfigService.CurrentConfig.CloudConnections.ToArray();
        Connections.Clear(); foreach (var connection in _connections) Connections.Add(connection.DisplayName);
        RemoteRoot = ConfigService.GetRecommendedDefaultCloudRemoteBasePath();
        if (IsExport)
        {
            foreach (var config in await MigrationBatchRecoveryService.CaptureAsync())
                Projects.Add(new(new HistoryConfigId(config.Id).Value, config.Name, MigrationWorkflowCoordinator.PathsFor(config)));
        }
        else if (request.Mode == MigrationWizardMode.Continue)
        {
            await PerformAsync(async token =>
            {
                var pending = await MigrationWorkflowCoordinator.PendingAsync(token);
                var document = request.WorkflowId is null ? pending.FirstOrDefault(d => request.ConfigId is null || d.Projects.Any(p => new HistoryConfigId(p.ConfigId).Matches(request.ConfigId)))
                    : MigrationWorkflowCoordinator.Load(request.WorkflowId);
                if (document is null && request.ConfigId is not null) document = await MigrationWorkflowCoordinator.FindRepairTaskAsync(request.ConfigId);
                if (document is null) { _step = WizardStep.Result; Message = I18n.GetString("MigrationWizard_NothingPending"); return; }
                _document = document;
                if (document.Stage == MigrationWorkflowStage.AwaitingConfirmation)
                {
                    _request = _request with { Scope = document.MetadataOnly ? DataMigrationScope.MetadataOnly : document.IncludeConfiguration
                        ? Path.GetExtension(document.PackagePath).Equals(".json", StringComparison.OrdinalIgnoreCase) ? DataMigrationScope.Configuration : DataMigrationScope.ConfigurationAndHistory : DataMigrationScope.History };
                    await ReadPackageAsync(document.PackagePath, token); IncludeConfiguration = document.IncludeConfiguration; _step = WizardStep.Selection;
                }
                else await RunCoreAsync(token);
            });
        }
        Notify();
    }

    public async Task ChoosePackageAsync()
    {
        if (Busy || PickPackage is null) return;
        var extensions = IsOriginalSettings || _request.Scope == DataMigrationScope.Configuration ? new[] { ".json" }
            : _request.Scope is DataMigrationScope.History or DataMigrationScope.MetadataOnly ? [".frhistory"] : new[] { ".json", ".frhistory" };
        await PerformAsync(async token => { var path = await PickPackage(extensions); if (!string.IsNullOrWhiteSpace(path)) await ReadPackageAsync(path, token); });
    }

    private async Task ReadPackageAsync(string path, CancellationToken token)
    {
        _preview = await Task.Run(() => DataMigrationService.PreviewAsync(path, token), token); PackagePath = path;
        Projects.Clear();
        if (IsOriginalSettings)
        {
            foreach (var candidate in await MigrationOriginalSettingsService.PreviewAsync(path))
            {
                var row = new MigrationWizardProjectItem(new HistoryConfigId(candidate.Current.Id).Value, candidate.Current.Name, MigrationWorkflowCoordinator.PathsFor(candidate.Current))
                { OriginalSettings = candidate, AutoBackup = candidate.AutoBackup, CloudPreference = candidate.Cloud };
                Projects.Add(row);
            }
        }
        else
        {
            var paths = await MigrationWorkflowCoordinator.PreviewPathsAsync(_preview);
            foreach (var project in _preview.Projects)
            {
                var draft = _document?.Stage == MigrationWorkflowStage.AwaitingConfirmation ? _document.Projects.FirstOrDefault(p => p.ConfigId == project.ConfigId.Value)?.Paths : null;
                var row = new MigrationWizardProjectItem(project.ConfigId.Value, project.Name, draft ?? paths[project.ConfigId.Value])
                { Status = I18n.Format("MigrationWizard_ProjectContents", project.Sources.Length, project.VersionCount),
                    Selected = _document is null || _document.Projects.Any(p => p.ConfigId == project.ConfigId.Value) };
                Projects.Add(row);
            }
        }
        IncludeConfiguration = _preview.ConfigurationUtf8 is not null && _request.Scope is not (DataMigrationScope.History or DataMigrationScope.MetadataOnly);
        Message = ImportSummary; Notify();
    }

    public async Task LoadSnapshotsAsync()
    {
        await PerformAsync(async token =>
        {
            var connection = Connection(); var snapshots = await CloudSyncService.ListMigrationSnapshotsAsync(connection, token);
            _snapshots = [.. snapshots.Where(s => IsOriginalSettings || _request.Scope == DataMigrationScope.Configuration ? s.FileName.EndsWith(".json", StringComparison.Ordinal)
                : _request.Scope is DataMigrationScope.History or DataMigrationScope.MetadataOnly ? s.FileName.EndsWith(".frhistory", StringComparison.Ordinal) : true)];
            Snapshots.Clear(); foreach (var snapshot in _snapshots) Snapshots.Add(snapshot.CreatedAtUtc.LocalDateTime.ToString("g") + " · " + snapshot.FileName);
            _legacySnapshots = _request.Scope is DataMigrationScope.History or DataMigrationScope.MetadataOnly ? []
                : [.. await CloudSyncService.ListCloudConfigurationExportsAsync(connection, token)];
            foreach (var name in _legacySnapshots) Snapshots.Add(I18n.GetString("MigrationWizard_LegacyConfig") + " · " + name);
            SnapshotIndex = Snapshots.Count > 0 ? 0 : -1;
            Message = Snapshots.Count > 0 ? I18n.GetString("MigrationWizard_SelectSnapshot") : I18n.GetString("Migration_NoCloudSnapshots");
        });
    }

    public async Task PrimaryAsync()
    {
        if (Busy) return;
        await PerformAsync(async token =>
        {
            if (ShowResult) { NavigationService.NavigateTo("Settings"); return; }
            if (IsExport)
            {
                if (!Projects.Any(p => p.Selected)) throw new InvalidOperationException(I18n.GetString("Migration_NoProjects"));
                if (_step == WizardStep.Source) _step = WizardStep.Selection;
                else if (_step == WizardStep.Selection) _step = WizardStep.Destination;
                else await ExportCoreAsync(token);
            }
            else if (_step == WizardStep.Source)
            {
                if (TransportIndex == 1)
                {
                    if (SnapshotIndex < 0 || SnapshotIndex >= _snapshots.Length + _legacySnapshots.Length) throw new InvalidOperationException(I18n.GetString("MigrationWizard_SelectSnapshot"));
                    var snapshot = SnapshotIndex < _snapshots.Length ? _snapshots[SnapshotIndex] : null;
                    var path = Path.Combine(Path.GetTempPath(), "FolderRewind-wizard-" + Guid.NewGuid().ToString("N") + (snapshot is null ? ".json" : Path.GetExtension(snapshot.FileName)));
                    try { if (snapshot is not null) await CloudSyncService.DownloadMigrationSnapshotAsync(Connection(), snapshot, path, token);
                        else await CloudSyncService.DownloadLegacyConfigurationExportAsync(Connection(), _legacySnapshots[SnapshotIndex - _snapshots.Length], path, token);
                        await ReadPackageAsync(path, token);
                        var copy = await MigrationWorkflowCoordinator.CachePreviewAsync(_preview!, token); _preview = copy; PackagePath = copy.Path; }
                    finally { if (File.Exists(path)) File.Delete(path); }
                }
                if (_preview is null) throw new InvalidOperationException(I18n.GetString("MigrationWizard_SelectFileFirst"));
                _step = WizardStep.Selection;
            }
            else if (_step == WizardStep.Selection)
            {
                if (!Projects.Any(p => p.Selected)) throw new InvalidOperationException(I18n.GetString("Migration_NoProjects"));
                if (IsOriginalSettings)
                {
                    var selected = Projects.Where(p => p.Selected && p.OriginalSettings is not null)
                        .Select(p => p.OriginalSettings! with { AutoBackup = p.AutoBackup, Cloud = p.CloudPreference }).ToArray();
                    var errors = await MigrationOriginalSettingsService.ApplyAsync(selected, token);
                    Message = errors.Count > 0 ? I18n.GetString("MigrationWizard_ActionFailed") : I18n.GetString("Migration_OriginalReadDone");
                    ErrorDetails = string.Join("\n", errors.Select(CloudCommandSecurity.Redact));
                    MessageStatus = errors.Count > 0 ? SemanticStatus.Error : SemanticStatus.Success; _step = WizardStep.Result;
                }
                else
                {
                    var selected = Projects.Where(p => p.Selected).ToDictionary(p => p.Id, p => p.CapturePaths());
                    _document = await Task.Run(() => MigrationWorkflowCoordinator.ImportAsync(_preview!, selected, IncludeConfiguration,
                        _request.Scope == DataMigrationScope.MetadataOnly, _document?.WorkflowId, token), token);
                    if (_document.Stage != MigrationWorkflowStage.AwaitingRestart && !_document.IsFinished) await RunCoreAsync(token);
                    else await ApplyDocumentAsync(token);
                }
            }
            else if (ShowPendingRestart)
            {
                MigrationWorkflowCoordinator.RequestRestart(_document!.WorkflowId);
                if (Restart is not null) await Restart();
            }
            else if (ShowRecovery && _document is not null)
            {
                if (Projects.Any(p => p.ShowPermissions))
                {
                    foreach (var item in Projects.Where(p => p.ShowPermissions))
                        _document = await MigrationWorkflowCoordinator.UpdateProjectAsync(_document.WorkflowId,
                            item.State! with { BackupSelected = item.BackupSelected, CloudSelected = item.CloudSelected }, false, token);
                    _document = await MigrationWorkflowCoordinator.ConfirmAsync(_document.WorkflowId, token); await ApplyDocumentAsync(token);
                }
                else { await SaveAllIssueChoicesAsync(token); await RunCoreAsync(token); }
            }
        });
    }

    private async Task ExportCoreAsync(CancellationToken token)
    {
        var extension = _request.Scope == DataMigrationScope.Configuration ? ".json" : ".frhistory";
        string? path = TransportIndex == 1 ? Path.Combine(Path.GetTempPath(), "FolderRewind-wizard-" + Guid.NewGuid().ToString("N") + extension)
            : PickExport is null ? null : await PickExport(extension);
        if (string.IsNullOrWhiteSpace(path)) return;
        var ids = Projects.Where(p => p.Selected).Select(p => new HistoryConfigId(p.Id)).ToArray();
        try
        {
            await Task.Run(() => DataMigrationService.ExportAsync(path, ids, _request.Scope, ArchiveIndex == 1, token), token);
            var destination = path;
            if (TransportIndex == 1) { var snapshot = await CloudSyncService.ExportMigrationSnapshotAsync(Connection(), path, token); destination = RemoteRoot + "/_folderrewind/migration-exports/" + snapshot.FileName; }
            Message = I18n.Format("Migration_ExportDone", ids.Length) + "\n" + I18n.Format("MigrationWizard_SavedTo", destination) + "\n" + ContentDescription;
            MessageStatus = SemanticStatus.Success;
            _step = WizardStep.Result;
        }
        finally { if (TransportIndex == 1 && File.Exists(path)) File.Delete(path); }
    }

    private CloudSettings Connection()
    {
        if (ConnectionIndex < 0 || ConnectionIndex >= _connections.Length) throw new InvalidOperationException(I18n.GetString("CloudConnections_CreateFirst"));
        var profile = _connections[ConnectionIndex];
        return new() { RcloneConfigPath = profile.CredentialReference, RemoteBasePath = RemoteRoot.Trim(), ExecutablePath = profile.ExecutablePath, WorkingDirectory = profile.WorkingDirectory };
    }

    private async Task RunCoreAsync(CancellationToken token)
    {
        if (_document is null) return;
        CaptureProjectEditor(); _inspection = null;
        _step = WizardStep.Recovery; Message = I18n.GetString("MigrationWizard_Checking"); MessageStatus = SemanticStatus.Info; Notify();
        _document = await Task.Run(() => MigrationWorkflowCoordinator.ContinueAsync(_document.WorkflowId, token), token);
        await ApplyDocumentAsync(token);
    }

    private async Task ApplyDocumentAsync(CancellationToken token)
    {
        if (_document is null) return;
        CaptureProjectEditor();
        _step = _document.IsFinished ? WizardStep.Result : WizardStep.Recovery;
        Projects.Clear();
        foreach (var project in _document.Projects)
        {
            var paths = project.Paths ?? new("", []);
            if (_editors.TryGetValue(project.ConfigId, out var editor))
            {
                if (MigrationPresentationPolicy.CanReuseEditor(editor.Phase, editor.Inspection.ConfigurationSignature, project))
                    paths = editor.Paths;
                else _editors.Remove(project.ConfigId);
            }
            Projects.Add(new(project.ConfigId, project.Name, paths, project) { AwaitingActivation = ShowPendingRestart });
        }
        Message = _document.MetadataOnly ? I18n.GetString("Migration_MetadataOnly") : I18n.GetString("MigrationWizard_Stage_" + _document.Stage);
        MessageStatus = MigrationPresentationPolicy.StatusFor(_document.Stage);
        if (_document.Projects.Any(p => p.Phase == MigrationProjectPhase.Completed))
            Message += "\n" + I18n.Format("MigrationWizard_ProjectCounts", _document.Projects.Count(p => p.Phase == MigrationProjectPhase.Completed), _document.Projects.Length);
        Archives.Clear(); Branches.Clear(); _inspection = null; SelectedProject = null;
        var attention = Projects.FirstOrDefault(p => p.CanHandle) ?? Projects.FirstOrDefault(p => p.ShowPermissions) ?? Projects.FirstOrDefault();
        if (attention is not null && !ShowPendingRestart) await SelectProjectCoreAsync(attention, token, openDetails: false);
        Notify();
    }

    public async Task SelectProjectAsync(MigrationWizardProjectItem project)
        => await PerformAsync(token => SelectProjectCoreAsync(project, token));
    private async Task SelectProjectCoreAsync(MigrationWizardProjectItem project, CancellationToken token, bool openDetails = true)
    {
        CaptureProjectEditor();
        ProjectEditor? editor = null;
        if (project.CanHandle && project.State?.Phase != MigrationProjectPhase.AwaitingCloudConnection && !_editors.TryGetValue(project.Id, out editor))
        {
            var inspection = await Task.Run(() => MigrationWorkflowCoordinator.InspectAsync(project.State!, token), token);
            editor = new(inspection, inspection.Archives.Select(a => new MigrationWizardArchiveItem(a, project.State!)).ToArray(),
                inspection.Branches.Select(b => new MigrationWizardBranchItem(b, project.State!)).ToArray(), project.CapturePaths(), project.State!.Phase);
        }
        SelectedProject = project; Archives.Clear(); Branches.Clear();
        _inspection = editor?.Inspection;
        foreach (var archive in editor?.Archives ?? []) Archives.Add(archive);
        foreach (var branch in editor?.Branches ?? []) Branches.Add(branch);
        if (openDetails) _detailOpen = true;
        Notify();
    }

    private async Task SaveIssueChoicesAsync(CancellationToken token)
    {
        if (_document is null || SelectedProject?.State is not { } state) return;
        CaptureProjectEditor();
        if (_editors.TryGetValue(SelectedProject.Id, out var editor)) await SaveProjectChoicesAsync(state, editor, token);
    }

    private void CaptureProjectEditor()
    {
        if (SelectedProject?.State is { } state && _inspection is not null)
            _editors[SelectedProject.Id] = new(_inspection, Archives.ToArray(), Branches.ToArray(), SelectedProject.CapturePaths(), state.Phase);
    }

    private async Task SaveAllIssueChoicesAsync(CancellationToken token)
    {
        CaptureProjectEditor();
        foreach (var project in Projects)
            if (project.State is { } state && _editors.TryGetValue(project.Id, out var editor))
                await SaveProjectChoicesAsync(state, editor, token);
    }

    private async Task SaveProjectChoicesAsync(MigrationWorkflowProject state, ProjectEditor editor, CancellationToken token)
    {
        if (_document is null) return;
        var paths = editor.Paths;
        var pathsChanged = state.Paths is not null && (paths.BackupRoot != state.Paths.BackupRoot || !paths.Sources.SequenceEqual(state.Paths.Sources));
        state = state with { Paths = paths, ConfigurationSignature = editor.Inspection.ConfigurationSignature,
            ConfigImportId = editor.Inspection.ConfigImportId,
            ArchiveSelections = [.. editor.Archives.Select(a => a.Capture()).Where(a => a is not null).Select(a => a!)], BranchSelections = [.. editor.Branches.Select(b => b.Capture())] };
        _document = await MigrationWorkflowCoordinator.UpdateProjectAsync(_document.WorkflowId, state, pathsChanged, token);
    }

    public async Task VerifyPasswordAsync(string password)
    {
        await PerformAsync(async token =>
        {
            if (_document is null || SelectedProject?.State is not { } state || string.IsNullOrEmpty(password)) return;
            await SaveIssueChoicesAsync(token);
            var config = (await MigrationBatchRecoveryService.CaptureAsync()).Single(c => new HistoryConfigId(state.ConfigId).Matches(c.Id));
            var selected = Archives.Select(a => a.Capture()).Where(a => a is not null).Select(a => new FolderRewind.History.Application.HistoryReplicaRecoveryCandidate(
                new(Guid.Parse(a!.RepresentationId)), a.Path, a.Size, a.Sha256, false)).ToArray();
            if (!await Task.Run(() => BackupPasswordPreparationService.VerifyAndStoreAsync(config, password, token, selected), token))
                throw new InvalidOperationException(I18n.GetString("Migration_PasswordVerificationFailed"));
            await RunCoreAsync(token);
        });
    }

    public async Task RetainMissingAsync()
    {
        await PerformAsync(async token =>
        {
            if (_document is null || SelectedProject?.State is not { } state) return;
            _document = await MigrationWorkflowCoordinator.UpdateProjectAsync(_document.WorkflowId, state with { RetainMissingArchives = true }, false, token);
            await RunCoreAsync(token);
        });
    }

    public async Task AddArchiveAsync(MigrationWizardArchiveItem archive)
    {
        if (Busy || PickPackage is null || SelectedProject?.State is not { } state) return;
        await PerformAsync(async token =>
        { var path = await PickPackage(["*"]); if (!string.IsNullOrWhiteSpace(path)) archive.Add(await MigrationWorkflowCoordinator.ReadCandidateAsync(state, archive.Id, path, token)); });
    }

    public async Task AttachPackageAsync()
    {
        if (Busy || PickPackage is null) return;
        await PerformAsync(async token =>
        { var path = await PickPackage(SelectedProject?.State?.Phase == MigrationProjectPhase.AwaitingConfiguration ? [".json"] : [".frhistory"]);
            if (string.IsNullOrWhiteSpace(path)) return; await ReadPackageAsync(path, token); _step = WizardStep.Selection; Notify(); });
    }

    public void ApplyRootMapping()
    {
        try
        {
            foreach (var project in Projects.Where(p => p.Selected))
            {
                project.BackupRoot = MigrationPathRebindPolicy.ReplaceRoot(project.BackupRoot, OldRoot, NewRoot);
                foreach (var source in project.Sources) source.Path = MigrationPathRebindPolicy.ReplaceRoot(source.Path, OldRoot, NewRoot);
            }
            Message = I18n.GetString("MigrationWizard_PathsUpdated"); MessageStatus = SemanticStatus.Success;
        }
        catch (Exception error) { Message = I18n.GetString("MigrationWizard_ActionFailed"); ErrorDetails = CloudCommandSecurity.Redact(error.Message); MessageStatus = SemanticStatus.Error; }
    }

    public void OpenReconciliation()
    {
        if (SelectedProject is null) return;
        var config = ConfigService.CurrentConfig.BackupConfigs.SingleOrDefault(c => new HistoryConfigId(SelectedProject.Id).Matches(c.Id));
        var source = _inspection?.Branches.FirstOrDefault(b => b.RequiresReconciliation)?.SourceId;
        var folder = config?.SourceFolders.FirstOrDefault(f => source is not null && Guid.Parse(f.Id) == Guid.Parse(source));
        if (config is not null && folder is not null) NavigationService.NavigateTo("History", ManagerNavigationParameter.ForFolder(config.Id, folder.Path));
    }

    public void GoBack()
    {
        if (!CanGoBack) return;
        _step = _step == WizardStep.Destination ? WizardStep.Selection : WizardStep.Source; Notify();
    }
    public void Cancel() => _operation?.Cancel();
    internal async Task SaveDraftAsync()
    {
        if (Busy || _preview is null || IsExport || IsOriginalSettings || _step is not (WizardStep.Source or WizardStep.Selection)
            || _document?.ImportCommitted == true || !Projects.Any(p => p.Selected)) return;
        var preview = _preview; var paths = Projects.Where(p => p.Selected).ToDictionary(p => p.Id, p => p.CapturePaths());
        var include = IncludeConfiguration; var metadata = _request.Scope == DataMigrationScope.MetadataOnly; var id = _document?.WorkflowId;
        try
        {
            var document = await Task.Run(() => MigrationWorkflowCoordinator.SaveDraftAsync(preview, paths, include, metadata, id));
            if (!_disposed) _document = document;
        }
        catch (Exception error) { LogService.LogError("Could not retain migration draft.", nameof(MigrationWizardViewModel), error); }
    }
    private async Task PerformAsync(Func<CancellationToken, Task> action)
    {
        if (Busy || _disposed) return;
        if (HasErrorDetails) { Message = ""; MessageStatus = _document is null ? SemanticStatus.Info : MigrationPresentationPolicy.StatusFor(_document.Stage); }
        using var operation = new CancellationTokenSource(); _operation = operation; ErrorDetails = ""; Busy = true;
        try { await action(operation.Token); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { Message = I18n.GetString("MigrationWizard_Paused"); MessageStatus = SemanticStatus.Info; }
        catch (Exception error) { Message = I18n.GetString("MigrationWizard_ActionFailed"); ErrorDetails = CloudCommandSecurity.Redact(error.ToString()); MessageStatus = SemanticStatus.Error;
            LogService.LogError("Migration wizard action failed.", nameof(MigrationWizardViewModel), error); }
        finally { _operation = null; Busy = false; Notify(); }
    }
    private void Notify()
    {
        foreach (var property in new[] { nameof(CanOperate), nameof(IsExport), nameof(IsOriginalSettings), nameof(ShowExportProjects), nameof(ShowExportContent),
            nameof(ShowArchiveOptions), nameof(ShowTransport), nameof(ShowCloud), nameof(ShowCloudSnapshots), nameof(ShowLocalPackage), nameof(ShowImportSelection),
            nameof(ShowOriginalSelection), nameof(ShowConfigurationChoice), nameof(ShowRecovery), nameof(ShowResult), nameof(ShowPendingRestart), nameof(ShowIssue),
            nameof(ShowPassword), nameof(ShowHistoryFile), nameof(ShowReconciliation), nameof(ShowMissingArchives), nameof(ShowIssuePaths), nameof(CanGoBack),
            nameof(Title), nameof(StepText), nameof(ScopeText), nameof(ContentDescription), nameof(ExternalArchiveRoots), nameof(ImportSummary), nameof(TechnicalDetails), nameof(PrimaryText),
            nameof(SelectedProjectName), nameof(SelectedSources), nameof(SelectedBackupRoot), nameof(ShowProjectList), nameof(ShowProjectDetails),
            nameof(ShowDetailsBack), nameof(WideRecovery), nameof(RecoverySummary), nameof(SelectedProjectStatus), nameof(SelectedOperationsText), nameof(HasIncompleteResult),
            nameof(ShowSelectedPermissions), nameof(SelectedCanBackup), nameof(SelectedCanCloud), nameof(SelectedCloudEnabled), nameof(SelectedBackup), nameof(SelectedCloud),
            nameof(SelectedPermissionSummary), nameof(SelectedNeedsCloudConnection), nameof(SelectedCloudConfigured) }) OnPropertyChanged(property);
    }
    public void Dispose() { _disposed = true; _operation?.Cancel(); }
}

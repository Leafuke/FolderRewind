using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;

namespace FolderRewind.ViewModels;

public sealed class BackupSetupViewModel : ViewModelBase
{
    public BackupConfig Draft { get; private set; } = new();
    private readonly List<BackupConfig> _projects = [];
    private readonly Dictionary<string, SetupBackupResult> _results = [];
    private readonly Dictionary<string, string> _errors = [];
    private readonly Dictionary<string, string> _confirmedSignatures = [];
    private bool _created;
    private bool _backupAttempted;
    private BackupSetupScenario _scenario;
    private int _selectedProject;
    public ObservableCollection<string> ProjectNames { get; } = [];
    public bool IsBatch => _projects.Count > 1;
    public int SelectedProjectIndex
    {
        get => _selectedProject;
        set
        {
            if (IsBusy || value < 0 || value >= _projects.Count || !SetProperty(ref _selectedProject, value)) return;
            Draft = _projects[value];
            RefreshSelectedDraft();
        }
    }
    public ObservableCollection<string> SourcePaths { get; } = [];
    public ObservableCollection<SetupSourceRow> SourceRows { get; } = [];
    public ObservableCollection<SetupResultRow> ResultRows { get; } = [];
    public bool IsChoosingScenario => _scenario == BackupSetupScenario.None && IsContent;
    public bool IsFolderContent => IsContent && !IsChoosingScenario;
    public bool IsManualContent => IsContent && _scenario == BackupSetupScenario.Folder;
    public bool IsMinecraftContent => IsContent && _scenario == BackupSetupScenario.Minecraft;
    public bool IsOtherGameContent => IsContent && _scenario == BackupSetupScenario.OtherGame;
    public bool ShowDraftActions => IsContent && HasSavedDraft;
    public bool ShowBack => IsLocation || IsReview;
    public bool CanSaveDraft => !IsChoosingScenario && !IsResult;
    public bool ShowResultSourcePicker => IsResult && HasMultipleSources && HasVersions;
    public bool HasSources => SourceRows.Count > 0;
    public bool HasSavedDraft => BackupSetupSessionStore.HasDraft;
    public bool CanStartBackup => IsResult && !_backupAttempted;
    public bool CanRetry => IsResult && (_errors.Count > 0 || _results.Values.Any(r => r.Sources.Any(s => s.Outcome is SetupSourceOutcome.Failed or SetupSourceOutcome.Canceled or SetupSourceOutcome.NotExecuted)));
    public bool HasVersions => BackupResult?.Sources.Any(s => s.VersionId is not null) == true;
    public string KindDisplay => _selectedKind >= 0 && _selectedKind < KindNames.Count ? KindNames[_selectedKind] : string.Empty;
    public string EncryptionDisplay => I18n.GetString(Draft.IsEncrypted ? "Setup_Encrypted" : "Setup_NotEncrypted");
    public string StepDisplay => IsChoosingScenario ? I18n.GetString("Setup_SelectScenario") : IsResult ? string.Empty : I18n.Format("Setup_StepNumber", (int)Stage + 1);
    public string ResultTitle => I18n.GetString(!_backupAttempted ? "Setup_ResultCreated" : CanRetry ? "Setup_ResultMixed" : "Setup_ResultComplete");
    private BackupSetupStage _stage;
    private bool _busy;
    private string _message = string.Empty;
    public BackupSetupStage Stage { get => _stage; private set { SetProperty(ref _stage, value); Refresh(); } }
    public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); OnPropertyChanged(nameof(CanEdit)); } }
    public bool CanEdit => !IsBusy;
    private SetupMessageSeverity _messageSeverity;
    public SetupMessageSeverity MessageSeverity { get => _messageSeverity; private set => SetProperty(ref _messageSeverity, value); }
    public string Message { get => _message; set { SetProperty(ref _message, value); OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public void ShowError(string message) { MessageSeverity = SetupMessageSeverity.Error; Message = message; }
    public string Name { get => Draft.Name; set { if (Draft.Name == value) return; Draft.Name = value; if (_selectedProject < ProjectNames.Count) ProjectNames[_selectedProject] = value; OnPropertyChanged(); Refresh(); } }
    public string Destination { get => Draft.DestinationPath; set { if (Draft.DestinationPath == value) return; Draft.DestinationPath = value; OnPropertyChanged(); } }
    public IReadOnlyList<string> IconOptions => IconCatalog.ConfigIconGlyphs;
    // SelectedItem must use the collection's string instance. Restored JSON creates a
    // different instance; echoing the same glyph back into x:Bind can recurse in WinUI.
    public string IconGlyph
    {
        get => IconOptions.FirstOrDefault(glyph => string.Equals(glyph, Draft.IconGlyph, StringComparison.Ordinal)) ?? Draft.IconGlyph;
        set { if (value is null || string.Equals(Draft.IconGlyph, value, StringComparison.Ordinal)) return; Draft.IconGlyph = value; OnPropertyChanged(); }
    }
    public bool IsContent => Stage == BackupSetupStage.Content;
    public bool IsLocation => Stage == BackupSetupStage.Location;
    public bool IsReview => Stage == BackupSetupStage.Review;
    public bool IsResult => Stage == BackupSetupStage.Result;
    public bool HasMultipleSources => Draft.SourceFolders.Count > 1;
    public string StageTitle => I18n.GetString(IsChoosingScenario ? "Setup_ChooseTitle" : "Setup_Stage" + Stage);
    public string? CreatedConfigId => _created ? Draft.Id : null;
    public SetupBackupResult? BackupResult => _results.GetValueOrDefault(Draft.Id);
    private string _presetId = string.Empty;
    private GameDiscoveryNavigationParameter? _discoveryReentry;
    private PluginConfigKindOption[] _kinds = [];
    public ObservableCollection<string> KindNames { get; } = [];
    private int _selectedKind = -1;
    public int SelectedKindIndex
    {
        get => _selectedKind;
        set
        {
            if (!SetProperty(ref _selectedKind, value) || value < 0 || value >= _kinds.Length) return;
            PluginService.ApplyConfigKind(Draft, _kinds[value]);
            Refresh();
        }
    }
    public void Initialize(BackupSetupNavigationParameter? input)
    {
        _projects.Clear(); _results.Clear(); _errors.Clear(); _confirmedSignatures.Clear(); _created = false; _selectedProject = 0;
        _projects.AddRange(input?.Drafts is { Count: > 0 } drafts ? drafts : [input?.Draft ?? new BackupConfig { Name = string.Empty }]);
        Draft = _projects[0];
        _backupAttempted = false;
        _scenario = input?.Scenario ?? (input?.Draft is not null || input?.Drafts is { Count: > 0 } ? BackupSetupScenario.Folder : BackupSetupScenario.None);
        Message = string.Empty;
        MessageSeverity = SetupMessageSeverity.Informational;
        ProjectNames.Clear(); foreach (var project in _projects) ProjectNames.Add(project.Name);
        _presetId = input?.PresetShareId ?? string.Empty;
        _discoveryReentry = input?.DiscoveryReentry;
        // 新建不会因模板附带开关而自动注册周期任务或上传。已有项目不经过本入口。
        foreach (var project in _projects) { project.Automation.AutoBackupEnabled = false; project.Cloud.Enabled = false; }
        _kinds = PluginService.GetAllSupportedConfigKinds(includeEncrypted: true).ToArray();
        KindNames.Clear(); foreach (var kind in _kinds) KindNames.Add(kind.DisplayName);
        RefreshSelectedDraft();
        OnPropertyChanged(nameof(IsBatch)); OnPropertyChanged(nameof(SelectedProjectIndex));
        Stage = _projects.All(p => p.SourceFolders.Count > 0) ? BackupSetupStage.Location : BackupSetupStage.Content;
    }
    private void RefreshSelectedDraft()
    {
        SourcePaths.Clear(); foreach (var source in Draft.SourceFolders) SourcePaths.Add(source.Path);
        _selectedKind = Array.FindIndex(_kinds, k => k.Kind.OwnerId == Draft.Kind.OwnerId && k.Kind.KindId == Draft.Kind.KindId && k.IsEncrypted == Draft.IsEncrypted);
        OnPropertyChanged(nameof(SelectedKindIndex)); OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(Destination));
        OnPropertyChanged(nameof(IconGlyph));
        Refresh();
    }
    public void AddSource(string path)
    {
        if (SourcePaths.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        Draft.SourceFolders.Add(new ManagedFolder { Path = path, DisplayName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) });
        SourcePaths.Add(path);
        if (string.IsNullOrWhiteSpace(Name)) Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (string.IsNullOrWhiteSpace(Destination)) Destination = ConfigService.BuildDefaultDestinationPath(Name);
        Refresh();
    }
    public void RemoveSource(string path)
    {
        SourcePaths.Remove(path);
        var source = Draft.SourceFolders.FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
        if (source is not null) Draft.SourceFolders.Remove(source);
        Refresh();
    }
    public void Next()
    {
        if (IsBusy) return;
        if (IsContent && SourcePaths.Count == 0) { ShowError(I18n.GetString("Setup_NoSources")); return; }
        if (IsLocation)
        {
            var error = BackupSetupCoordinator.ValidateNewProjects(_projects);
            if (error.Length != 0) { ShowError(error); return; }
        }
        if (IsContent || IsLocation) Stage++;
        Message = string.Empty;
    }
    public void Back() { if (!IsBusy && Stage is BackupSetupStage.Location or BackupSetupStage.Review) Stage--; }
    public void SelectScenario(BackupSetupScenario scenario) { _scenario = scenario; Refresh(); }
    public void RefreshSavedDraft() => OnPropertyChanged(nameof(ShowDraftActions));

    public async Task SubmitAsync(bool immediatelyBackup, Func<Task<string?>> requestPassword, CancellationToken token = default)
    {
        if (IsBusy || !IsReview || CreatedConfigId is not null) return;
        IsBusy = true;
        var passwords = new Dictionary<string, string>();
        try
        {
            foreach (var project in _projects.Where(p => p.IsEncrypted))
            {
                Message = I18n.Format("Setup_PasswordForProject", project.Name);
                var password = await requestPassword();
                if (password is null) return;
                passwords.Add(project.Id, password);
            }
            token.ThrowIfCancellationRequested();
            await BackupSetupCoordinator.CommitAsync(_projects, passwords, token);
            foreach (var project in _projects) _confirmedSignatures[project.Id] = NativeHistoryConfigLease.Signature(project);
            _created = true;
            Stage = BackupSetupStage.Result;
            MessageSeverity = SetupMessageSeverity.Success;
            Message = string.Empty;
            if (immediatelyBackup) await RunBackupsAsync(retry: false);
        }
        catch (ConfigEditRollbackException) { ShowError(I18n.GetString("Setup_SaveUncertain")); }
        catch (Exception ex) { ShowError((CreatedConfigId is null ? "" : I18n.GetString("Setup_FirstBackupFailed") + Environment.NewLine) + ex.Message); }
        finally { passwords.Clear(); IsBusy = false; Refresh(); }
    }
    public async Task RetryAsync()
    {
        if (IsBusy || CreatedConfigId is null) return;
        IsBusy = true;
        try { await RunBackupsAsync(retry: _backupAttempted); } catch (Exception ex) { ShowError(I18n.GetString("Setup_FirstBackupFailed") + Environment.NewLine + ex.Message); }
        finally { IsBusy = false; Refresh(); }
    }
    private async Task RunBackupsAsync(bool retry)
    {
        _backupAttempted = true;
        MessageSeverity = SetupMessageSeverity.Informational;
        Message = I18n.GetString("Setup_BackupRunning");
        Refresh();
        foreach (var project in _projects)
        {
            var previous = _results.GetValueOrDefault(project.Id);
            var sourceIds = retry ? previous?.Sources.Where(s => s.Outcome is SetupSourceOutcome.Failed or SetupSourceOutcome.Canceled or SetupSourceOutcome.NotExecuted).Select(s => s.SourceId).ToArray() : null;
            if (sourceIds is { Length: 0 }) continue;
            try
            {
                RequireCreatedContextCurrent(project);
                var result = await BackupService.BackupForSetupAsync(project, sourceIds);
                _confirmedSignatures[project.Id] = NativeHistoryConfigLease.Signature(project);
                if (retry && previous is not null) result = previous.MergeRetry(result);
                _results[project.Id] = result; _errors.Remove(project.Id);
            }
            catch (Exception ex) { _errors[project.Id] = ex.Message; }
        }
        MessageSeverity = _results.Values.Any(r => r.HasWarnings || r.RecoveryRequired) || _errors.Count > 0 || CanRetry ? SetupMessageSeverity.Warning : SetupMessageSeverity.Success;
        Message = _results.Values.Any(r => r.RecoveryRequired) ? I18n.GetString("Onboarding_ConsistencyWarning") : string.Empty;
        Refresh();
    }
    public void RequireCreatedContextCurrent() => RequireCreatedContextCurrent(Draft);
    private void RequireCreatedContextCurrent(BackupConfig project)
    {
        if (!_created || !ConfigService.CurrentConfig.BackupConfigs.Contains(project)
            || !_confirmedSignatures.TryGetValue(project.Id, out var expected) || NativeHistoryConfigLease.Signature(project) != expected)
            throw new InvalidOperationException(I18n.GetString("SettingsProject_Stale"));
    }
    public async Task SaveForLaterAsync()
    {
        if (IsBusy || CreatedConfigId is not null) return;
        if (IsBatch || Draft.DiscoveryOrigin is not null)
        {
            if (_discoveryReentry is null) { Message = I18n.GetString("Setup_DraftRequiresReentry"); return; }
            var selections = _projects.Select(p => new BackupSetupDraftSelection
            {
                Identity = new() { ProviderId = p.DiscoveryOrigin?.Identity.ProviderId ?? "", DefinitionId = p.DiscoveryOrigin?.Identity.DefinitionId ?? "", SetId = p.DiscoveryOrigin?.Identity.SetId ?? "" },
                Name = p.Name, DestinationPath = p.DestinationPath, Kind = new() { OwnerId = p.Kind.OwnerId, KindId = p.Kind.KindId }, IsEncrypted = p.IsEncrypted, IconGlyph = p.IconGlyph
            }).ToList();
            await BackupSetupSessionStore.SaveAsync(new() { DiscoveryReentry = _discoveryReentry, Selections = selections });
            Message = I18n.GetString("Setup_DiscoveryDraftSaved");
            return;
        }
        // 有复杂预设的草稿由预设重建；此处只保存用户选择，不存 provider state/秘密。
        await BackupSetupSessionStore.SaveAsync(new() { Name = Name, DestinationPath = Destination,
            Stage = Stage, SourcePaths = SourcePaths.ToList(), PresetShareId = _presetId,
            Kind = new() { OwnerId = Draft.Kind.OwnerId, KindId = Draft.Kind.KindId }, IsEncrypted = Draft.IsEncrypted, IconGlyph = Draft.IconGlyph });
        MessageSeverity = SetupMessageSeverity.Success;
        Message = I18n.GetString("Setup_DraftSavedShort");
        OnPropertyChanged(nameof(HasSavedDraft));
    }
    public void Resume()
    {
        var session = BackupSetupSessionStore.Load();
        if (session is null) return;
        if (session.DiscoveryReentry is { } request)
        {
            NavigationService.NavigateTo("GameDiscovery", new GameDiscoveryNavigationParameter
            {
                Mode = request.Mode, PresetShareId = request.PresetShareId, RequestedConfigName = request.RequestedConfigName,
                PluginId = request.PluginId, ConfigKind = request.ConfigKind, UserRoot = request.UserRoot,
                ReturnDraftToSetup = true, ResumingSelections = session.Selections
            });
            return;
        }
        if (!string.IsNullOrEmpty(session.PresetShareId))
        {
            var preset = BackupPresetService.GetTemplates().SingleOrDefault(p => p.ShareId == session.PresetShareId)
                ?? throw new InvalidOperationException(I18n.GetString("Setup_ResumePreset"));
            var kind = _kinds.SingleOrDefault(k => k.Kind.OwnerId == session.Kind.OwnerId && k.Kind.KindId == session.Kind.KindId && k.IsEncrypted == session.IsEncrypted);
            if (kind is null) throw new InvalidOperationException(I18n.GetString("Setup_PluginUnavailable"));
            var rebuilt = BackupPresetService.CreateConfigFromTemplate(preset, session.Name, kind);
            Draft = rebuilt.Success && rebuilt.Config is not null ? rebuilt.Config : throw new InvalidOperationException(rebuilt.Message);
            Draft.SourceFolders.Clear(); Draft.DestinationPath = session.DestinationPath; Draft.IconGlyph = session.IconGlyph;
        }
        else Draft = new BackupConfig { Name = session.Name, DestinationPath = session.DestinationPath, Kind = session.Kind,
            IsEncrypted = session.IsEncrypted, IconGlyph = session.IconGlyph };
        _presetId = session.PresetShareId;
        Draft.Automation.AutoBackupEnabled = false; Draft.Cloud.Enabled = false;
        _projects.Clear(); _projects.Add(Draft); _selectedProject = 0;
        _scenario = BackupSetupScenario.Folder;
        ProjectNames.Clear(); ProjectNames.Add(Draft.Name);
        SourcePaths.Clear();
        foreach (var path in session.SourcePaths) AddSource(path);
        Stage = BackupSetupStage.Content; // 不信任上次检查/就绪；不重放执行。
        _selectedKind = Array.FindIndex(_kinds, k => k.Kind.OwnerId == Draft.Kind.OwnerId && k.Kind.KindId == Draft.Kind.KindId && k.IsEncrypted == Draft.IsEncrypted);
        OnPropertyChanged(nameof(SelectedKindIndex));
        OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(Destination));
        OnPropertyChanged(nameof(IsBatch)); OnPropertyChanged(nameof(SelectedProjectIndex));
        RefreshSelectedDraft();
    }
    private void Refresh()
    {
        SourceRows.Clear();
        foreach (var source in Draft.SourceFolders) SourceRows.Add(new(source.DisplayName, source.Path));
        ResultRows.Clear();
        foreach (var project in _projects)
            foreach (var source in project.SourceFolders)
            {
                var result = _results.GetValueOrDefault(project.Id)?.Sources.FirstOrDefault(s => s.SourceId == source.Id);
                var outcome = result?.Outcome;
                var status = !_backupAttempted ? "Setup_ResultWaiting" : outcome is null ? "Setup_OutcomeFailed" : "Setup_Outcome" + outcome;
                ResultRows.Add(new(IsBatch ? project.Name + " · " + source.DisplayName : source.DisplayName, source.Path, I18n.GetString(status), result?.IsPartial == true ? I18n.GetString("Setup_PartialResult") : string.Empty));
            }
        foreach (var property in new[] { nameof(IsContent), nameof(IsLocation), nameof(IsReview), nameof(IsResult), nameof(HasMultipleSources), nameof(StageTitle), nameof(IsChoosingScenario), nameof(IsFolderContent), nameof(IsManualContent), nameof(IsMinecraftContent), nameof(IsOtherGameContent), nameof(ShowDraftActions), nameof(ShowBack), nameof(CanSaveDraft), nameof(ShowResultSourcePicker), nameof(HasSources), nameof(HasSavedDraft), nameof(CanStartBackup), nameof(CanRetry), nameof(HasVersions), nameof(KindDisplay), nameof(EncryptionDisplay), nameof(StepDisplay), nameof(ResultTitle) }) OnPropertyChanged(property);
    }
}

public sealed record SetupSourceRow(string Name, string Path);
public sealed record SetupResultRow(string Name, string Path, string Status, string Warning);

public enum SetupMessageSeverity { Informational, Success, Warning, Error }

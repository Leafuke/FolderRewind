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
    private BackupSetupStage _stage;
    private bool _busy;
    private string _message = string.Empty;
    public BackupSetupStage Stage { get => _stage; private set { SetProperty(ref _stage, value); Refresh(); } }
    public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); OnPropertyChanged(nameof(CanEdit)); } }
    public bool CanEdit => !IsBusy;
    public string Message { get => _message; set => SetProperty(ref _message, value); }
    public string Name { get => Draft.Name; set { Draft.Name = value; if (_selectedProject < ProjectNames.Count) ProjectNames[_selectedProject] = value; OnPropertyChanged(); Refresh(); } }
    public string Destination { get => Draft.DestinationPath; set { Draft.DestinationPath = value; OnPropertyChanged(); } }
    public IReadOnlyList<string> IconOptions => IconCatalog.ConfigIconGlyphs;
    public string IconGlyph { get => Draft.IconGlyph; set { Draft.IconGlyph = value; OnPropertyChanged(); } }
    public bool IsContent => Stage == BackupSetupStage.Content;
    public bool IsLocation => Stage == BackupSetupStage.Location;
    public bool IsReview => Stage == BackupSetupStage.Review;
    public bool IsResult => Stage == BackupSetupStage.Result;
    public bool HasMultipleSources => Draft.SourceFolders.Count > 1;
    public string StageTitle => I18n.GetString("Setup_Stage" + Stage);
    public string Review => string.Join("\n\n", _projects.Select(p => p.Name + "\n" + string.Join("\n", p.SourceFolders.Select(s => s.DisplayName + " · " + s.Path + "\n" +
        I18n.Format("Setup_SourceBoundary", s.SourceScope.Mode, string.Join(", ", s.SourceScope.IncludePatterns)))) + "\n→ " + p.DestinationPath));
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
    public string RuleSummary => I18n.Format("Setup_RuleSummary", Draft.Archive.KeepCount, Draft.Archive.Mode,
        Draft.Archive.CpuThreads, Draft.Archive.RunCompressionAtLowPriority, Draft.BackupScope.ScopeId);

    public void Initialize(BackupSetupNavigationParameter? input)
    {
        _projects.Clear(); _results.Clear(); _errors.Clear(); _confirmedSignatures.Clear(); _created = false; _selectedProject = 0;
        _projects.AddRange(input?.Drafts is { Count: > 0 } drafts ? drafts : [input?.Draft ?? new BackupConfig { Name = string.Empty }]);
        Draft = _projects[0];
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
        if (IsContent && SourcePaths.Count == 0) { Message = I18n.GetString("Setup_NoSources"); return; }
        if (IsLocation)
        {
            var error = BackupSetupCoordinator.ValidateNewProjects(_projects);
            if (error.Length != 0) { Message = error; return; }
        }
        if (IsContent || IsLocation) Stage++;
        Message = string.Empty;
    }
    public void Back() { if (!IsBusy && Stage is BackupSetupStage.Location or BackupSetupStage.Review) Stage--; }

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
            Message = I18n.GetString("Setup_Created");
            if (immediatelyBackup) await RunBackupsAsync(retry: false);
        }
        catch (ConfigEditRollbackException) { Message = I18n.GetString("Setup_SaveUncertain"); }
        catch (Exception ex) { Message = (CreatedConfigId is null ? "" : I18n.GetString("Setup_FirstBackupFailed") + Environment.NewLine) + ex.Message; }
        finally { passwords.Clear(); IsBusy = false; Refresh(); }
    }
    public async Task RetryAsync()
    {
        if (IsBusy || CreatedConfigId is null) return;
        IsBusy = true;
        try { await RunBackupsAsync(retry: true); } catch (Exception ex) { Message = I18n.GetString("Setup_FirstBackupFailed") + Environment.NewLine + ex.Message; }
        finally { IsBusy = false; }
    }
    private async Task RunBackupsAsync(bool retry)
    {
        Message = I18n.GetString("Setup_BackupRunning");
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
        Message = I18n.GetString("Setup_SessionResults") + "\n" + string.Join("\n\n", _projects.Select(project => project.Name + "\n" +
            (_results.TryGetValue(project.Id, out var result) ? FormatResult(result) : I18n.GetString("Setup_FirstBackupFailed") + "\n" + _errors.GetValueOrDefault(project.Id))));
    }
    public void RequireCreatedContextCurrent() => RequireCreatedContextCurrent(Draft);
    private void RequireCreatedContextCurrent(BackupConfig project)
    {
        if (!_created || !ConfigService.CurrentConfig.BackupConfigs.Contains(project)
            || !_confirmedSignatures.TryGetValue(project.Id, out var expected) || NativeHistoryConfigLease.Signature(project) != expected)
            throw new InvalidOperationException(I18n.GetString("SettingsProject_Stale"));
    }
    private static string FormatResult(SetupBackupResult result)
    {
        var text = I18n.Format("Setup_BackupSummary", result.CreatedVersionCount, result.UnchangedCount, result.FailedCount, result.CanceledCount)
            + Environment.NewLine + string.Join(Environment.NewLine, result.Sources.Select(s =>
                $"{s.Name}: {I18n.GetString("Setup_Outcome" + s.Outcome)} {s.VersionId}\n{string.Join("; ", s.Diagnostics)}"));
        if (result.HasWarnings) text += Environment.NewLine + I18n.GetString("Onboarding_ConsistencyWarning");
        return text;
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
        Message = I18n.Format("Setup_DraftSaved", BackupSetupSessionStore.DraftPath);
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
        foreach (var property in new[] { nameof(IsContent), nameof(IsLocation), nameof(IsReview), nameof(IsResult), nameof(HasMultipleSources), nameof(StageTitle), nameof(Review), nameof(RuleSummary) }) OnPropertyChanged(property);
    }
}

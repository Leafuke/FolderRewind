using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed record MergeBranchChoice(BranchId Id, string Name);
public sealed record MergeSessionChoice(Guid Id, string Label);
public sealed class MergeChangeItem(MergeConflict conflict, MergeResolution? resolution, bool automatic = false) : ViewModelBase
{
    public bool IsAutomatic { get; } = automatic;
    public bool CanCheck => !IsAutomatic;
    public MergeConflict Conflict { get; } = conflict;
    public MergeResolution? Resolution { get; } = resolution;
    public string Id => Conflict.Id;
    public string Path => Conflict.Subject.Paths.IsEmpty ? I18n.GetString("Merge_WholeSource") : string.Join("\n", Conflict.Subject.Paths);
    public string Description => IsAutomatic ? I18n.GetString("MergeWorkspace_Automatic") : I18n.GetString("Merge_Conflict_" + Conflict.Kind) + " · "
        + (Resolution is null ? I18n.GetString("MergeWorkspace_Unresolved") : I18n.GetString("Merge_" + Resolution.Choice));
    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }
}

public sealed partial class MergePageViewModel : ViewModelBase
{
    internal MergeOperationService? Operations { get; private set; }
    public MergeNavigationParameter? Navigation { get; private set; }
    private bool _active;
    private bool _switchingSession;
    private long _loadGeneration;
    private string? _loadedRevision;
    private string? _localError;
    private bool _showCompleted;
    internal MergeViewState ViewState { get; set; } = new();
    public ObservableCollection<MergeBranchChoice> Branches { get; } = [];
    public ObservableCollection<MergeSessionChoice> Sessions { get; } = [];
    public ObservableCollection<MergeChangeItem> Changes { get; } = [];
    public MergeBranchChoice? SelectedBranch { get; set; }
    public MergeSessionChoice? SelectedSession { get; private set; }
    public string FolderName { get; private set; } = "";
    public string TargetPath { get; private set; } = "";
    private string _targetName = "";
    private bool _hasTarget;
    public MergeOperationSnapshot State => Operations?.Snapshot ?? new();
    public bool IsBusy => _switchingSession || State.IsBusy;
    public bool IsIdle => !IsBusy;
    public bool CanStop => State.CanStop;
    public bool HasSession => State.Session is not null;
    public bool CanAnalyze => IsIdle && State.IsSaved && _hasTarget && SelectedBranch is not null;
    public bool CanResolve => IsIdle && State.IsSaved && State.Session?.State is MergeSessionState.Resolving or MergeSessionState.Ready;
    public bool CanRecompute => IsIdle && State.IsSaved && State.Session?.State is MergeSessionState.Preparing or MergeSessionState.Resolving or MergeSessionState.Ready or MergeSessionState.Stale;
    public bool CanPrepare => CanRecompute && State.Session?.State != MergeSessionState.Stale;
    public bool CanRecover => IsIdle && HasSession && (State.Session?.State is MergeSessionState.Preparing or MergeSessionState.Applying
        || State.Result?.Status is HistoryRestoreStatus.CommittedRecoveryRequired or HistoryRestoreStatus.MutationFailedRecoveryRequired);
    public bool CanAbandon => IsIdle && State.Session?.State is MergeSessionState.Preparing or MergeSessionState.Resolving or MergeSessionState.Ready or MergeSessionState.Stale;
    public bool CanGenerate => IsIdle && State.IsSaved && State.Session?.State == MergeSessionState.Ready;
    public bool CanRetrySave => Operations?.CanRetrySave == true;
    public bool CanChooseSession => IsIdle && State.IsSaved;
    public string ResumeLabel => I18n.GetString(State.Session?.State == MergeSessionState.Applying
        || State.Result?.Status is HistoryRestoreStatus.CommittedRecoveryRequired or HistoryRestoreStatus.MutationFailedRecoveryRequired
        ? "MergeWorkspace_RecoverTransaction" : "MergeWorkspace_ResumePreparation");
    public string Direction => State.Session is { } s ? $"{s.Plan.Theirs.Name} → {s.Plan.Ours.Name}" : $"{SelectedBranch?.Name ?? "—"} → {_targetName}";
    public string OursLabel => State.Session?.Plan.Ours.Name ?? _targetName;
    public string TheirsLabel => State.Session?.Plan.Theirs.Name ?? SelectedBranch?.Name ?? "—";
    public string Status => IsBusy ? I18n.GetString("MergeWorkspace_Stage_" + State.Stage)
        : State.Result is { } result ? I18n.GetString("Merge_Result_" + result.Status)
        : State.Notice is { } notice ? I18n.GetString(notice)
        : State.Session is { } session ? I18n.GetString("Merge_State_" + session.State) : I18n.GetString("MergeWorkspace_ChooseSource");
    public string SaveStatus => HasSession ? I18n.GetString(State.IsSaved ? "MergeWorkspace_Saved" : "MergeWorkspace_Unsaved") : "";
    public string SourceAvailability => !_hasTarget ? I18n.GetString("MergeWorkspace_NoTarget")
        : Branches.Count == 0 ? I18n.GetString("MergeWorkspace_NoSource") : "";
    public string Error => _localError ?? State.Error ?? ((State.Result?.MergeDiagnostic ?? State.Session?.Diagnostic) is { } d
        ? I18n.GetString("Merge_Diagnostic_" + d.Code) + "\n" + I18n.GetString(d.NextActionKey) + "\n" + d.Detail : "");
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public string CountLabel => string.Format(I18n.GetString("MergeWorkspace_Count"), _total, _unresolved);
    public bool ShowCompleted { get => _showCompleted; set { if (SetProperty(ref _showCompleted, value)) RefreshSessions(); } }
    private MergeChangeItem? _selectedChange;
    public MergeChangeItem? SelectedChange { get => _selectedChange; set { if (SetProperty(ref _selectedChange, value)) SelectionChanged(); } }
    public string SelectedPath => SelectedChange?.Path ?? "";
    public string SelectedDescription => SelectedChange?.Description ?? "";
    public bool CanImport => CanResolve && CheckedCount == 0 && SelectedChange is { IsAutomatic: false, Conflict: var c }
        && c.Subject.Paths.Length == 1 && c.Kind is not (MergeConflictKind.PathStructure or MergeConflictKind.SourceRoster or MergeConflictKind.SourceBoundary);
    public event Action? ContentChanged;

    public async Task InitializeAsync(MergeNavigationParameter parameter)
    {
        Navigation = parameter; _active = true;
        var config = ConfigService.CurrentConfig.BackupConfigs.Single(c => c.Id == parameter.ConfigId);
        var folder = config.SourceFolders.Single(f => f.Id == parameter.FolderId);
        FolderName = folder.DisplayName; TargetPath = folder.Path;
        Operations = MergeOperationService.Get(config, new SourceId(Guid.Parse(folder.Id)));
        Operations.Tracker.Changed += OnOperationChanged;
        RefreshSessions(); Notify();
        await Operations.LoadAsync(parameter.SessionId);
        if (!_active) return;
        if (Operations.Runtime is { } runtime)
        {
            var workspace = (await runtime.WorkspaceStore.LoadAsync()).Value;
            var activeId = workspace?.GetSourceState(Operations.SourceId).ActiveBranchId;
            var branches = HistoryBranchProjection.Build(await runtime.Query.GetAllBranchUpdatesAsync());
            if (!_active) return;
            _hasTarget = branches.Any(b => b.BranchId == activeId && b.Tips.Length == 1 && !b.Tips[0].IsDeleted && !b.Tips[0].IsUnborn);
            _targetName = branches.FirstOrDefault(b => b.BranchId == activeId)?.Tips.FirstOrDefault()?.Name ?? I18n.GetString("History_Branch_CurrentNotEstablished");
            Branches.Clear();
            foreach (var branch in branches.Where(b => b.BranchId != activeId && b.Tips.Length == 1
                && b.Tips[0].SourceId == Operations.SourceId && !b.Tips[0].IsDeleted && !b.Tips[0].IsUnborn))
                Branches.Add(new(branch.BranchId, branch.Tips[0].Name));
            SelectedBranch = Branches.FirstOrDefault(b => b.Id == parameter.SourceBranch) ?? Branches.FirstOrDefault();
            if (State.Session is { } session)
                ViewState = await Task.Run(() => MergeViewStateStore.Load(runtime.Repository.Paths.LocalStateRoot, session.Id));
            _search = ViewState.Search; _filter = ViewState.Filter;
        }
        await ReloadChangesAsync(); RefreshSessions(); Notify();
    }
    public void Detach()
    {
        _active = false; ++_loadGeneration;
        _filterDelay?.Cancel(); _filterDelay?.Dispose(); _filterDelay = null;
        if (Operations is not null) Operations.Tracker.Changed -= OnOperationChanged;
        CancelPreview();
        if (Operations?.Runtime is { } runtime && State.Session is { } session)
        {
            var state = ViewState with { SelectedId = SelectedChange?.Id };
            TaskObserver.Observe(Task.Run(() => MergeViewStateStore.Save(runtime.Repository.Paths.LocalStateRoot, session.Id, state)), "Merge view state");
        }
    }
    private void OnOperationChanged() => EnqueueOnUiThread(() =>
    {
        if (!_active) return;
        if (_switchingSession) { Notify(); return; }
        if (IsBusy) { ++_loadGeneration; CancelPreview(); }
        if (State.Session is { } pinned)
            TargetPath = pinned.Plan.Bindings.FirstOrDefault(b => b.SourceId == Operations!.SourceId)?.TargetDirectory ?? TargetPath;
        RefreshSessions(); Notify();
        var revision = State.Session is { } s ? $"{s.Id}:{s.Revision}" : "";
        if (!IsBusy && _loadedRevision != revision) TaskObserver.Observe(ReloadChangesAsync(), "Merge changes");
        else if (!IsBusy && ShowWorkspace) SelectionChanged();
    });
    private void RefreshSessions()
    {
        var current = State.Session?.Id;
        Sessions.Clear();
        foreach (var s in Operations?.Sessions ?? [])
            if (ShowCompleted || s.Id == current || s.State is not (MergeSessionState.Committed or MergeSessionState.Abandoned))
                Sessions.Add(new(s.Id, $"{s.Plan.Theirs.Name} → {s.Plan.Ours.Name} · {I18n.GetString("Merge_State_" + s.State)}"));
        SelectedSession = Sessions.FirstOrDefault(s => s.Id == current);
    }
    public async Task ReloadChangesAsync()
    {
        var generation = ++_loadGeneration;
        var session = State.Session;
        try
        {
            var search = Search; var filter = Filter;
            var selectedId = SelectedChange?.Id ?? ViewState.SelectedId;
            var page = Operations is { Runtime: not null } operations && session is not null && session.State is not (MergeSessionState.Committed or MergeSessionState.Abandoned)
                ? await Task.Run(() => operations.QueryChanges(session, search, filter, 0, Math.Max(100, Changes.Count), selectedId)) : null;
            if (!_active || generation != _loadGeneration) return;
            var selected = SelectedChange?.Id ?? ViewState.SelectedId;
            var scrollOffset = ViewState.ScrollOffset;
            var checkedIds = Changes.Where(c => c.IsChecked).Select(c => c.Id).ToHashSet();
            ContentChanging?.Invoke();
            foreach (var row in Changes) row.PropertyChanged -= OnRowChanged;
            Changes.Clear();
            foreach (var row in page?.Rows ?? []) AddRow(row.Conflict, row.Resolution, row.Automatic, checkedIds.Contains(row.Conflict.Id));
            _total = page?.Total ?? 0; _unresolved = page?.Unresolved ?? 0; _matching = page?.Matching ?? 0;
            _loadedRevision = session is null ? "" : $"{session.Id}:{session.Revision}";
            SelectedChange = Changes.FirstOrDefault(c => c.Id == selected) ?? Changes.FirstOrDefault();
            ViewState = ViewState with { ScrollOffset = scrollOffset };
            Notify(); ContentChanged?.Invoke();
        }
        catch (Exception ex) { if (_active && generation == _loadGeneration) ReportError(ex); }
    }
    public event Action? ContentChanging;
    public void Notify() => OnPropertyChanged(string.Empty);
    public void ReportError(Exception ex) { _localError = ex.Message; Notify(); }
    public async Task ExecuteAsync(Func<Task> action) { _localError = null; try { await action(); } catch (Exception ex) { ReportError(ex); } Notify(); }
    public async Task AnalyzeAsync()
    {
        if (!CanAnalyze || SelectedBranch is not { } branch || Operations is null) return;
        var previous = State.Session?.Id;
        await ExecuteAsync(() => Operations.StartAsync(branch.Id));
        if (State.Session is { } session && session.Id != previous) _choosingSource = false;
        Notify();
    }
    public async Task SelectSessionAsync(Guid id)
    {
        if (State.Session?.Id == id) { CancelNewMerge(); return; }
        if (Operations is null || _switchingSession || State.IsBusy) return;
        await ExecuteAsync(async () =>
        {
            _switchingSession = true; Notify();
            try
            {
                if (Operations.Runtime is { } oldRuntime && State.Session is { } previous)
                {
                    var presentation = ViewState with { SelectedId = SelectedChange?.Id };
                    await Task.Run(() => MergeViewStateStore.Save(oldRuntime.Repository.Paths.LocalStateRoot, previous.Id, presentation));
                }
                await Operations.LoadAsync(id);
                _choosingSource = false;
                if (Operations.Runtime is { } runtime && State.Session?.Id == id)
                {
                    ViewState = await Task.Run(() => MergeViewStateStore.Load(runtime.Repository.Paths.LocalStateRoot, id));
                    _search = ViewState.Search; _filter = ViewState.Filter; _selectedChange = null;
                }
            }
            finally { _switchingSession = false; }
            await ReloadChangesAsync(); RefreshSessions();
        });
    }
    public Task AdoptAsync(MergeResolutionChoice choice)
    {
        if (!CanResolve || Operations is null || State.Session is not { } session) return Task.CompletedTask;
        var selected = SelectedDecisions();
        return ExecuteAsync(() => Operations.ResolveAsync(selected.Select(c => new MergeResolution(session.Plan.Revision, c.Id, c.Conflict.InputSignature, choice)).ToArray()));
    }
    partial void SelectionChanged();
    partial void CancelPreview();
}

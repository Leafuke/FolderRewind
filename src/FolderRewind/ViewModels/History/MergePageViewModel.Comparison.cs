using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Services;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed record MergeDiffLine(string Number, string Marker, string Text);
public sealed partial class MergePageViewModel
{
    private CancellationTokenSource? _preview;
    private CancellationTokenSource? _filterDelay;
    private int _total, _unresolved, _matching;
    private bool _loadingMore;
    private string _search = "", _filter = "All";
    public string Search { get => _search; set { if (SetProperty(ref _search, value ?? "")) FilterChanged(); } }
    public string Filter { get => _filter; set { if (SetProperty(ref _filter, value)) FilterChanged(); } }
    public bool ShowBase { get => ViewState.ShowBase; set { ViewState = ViewState with { ShowBase = value }; Notify(); } }
    public bool CanPreviewBase => SelectedChange is { IsAutomatic: false, Conflict.SemanticSubject: null };
    public string PreviewNotice { get; private set; } = "";
    public string OursInfo { get; private set; } = "";
    public string TheirsInfo { get; private set; } = "";
    public string BaseText { get; private set; } = "";
    public string DecisionText => SelectedChange?.Description ?? "";
    public ObservableCollection<MergeDiffLine> OursLines { get; } = [];
    public ObservableCollection<MergeDiffLine> TheirsLines { get; } = [];
    public string SelectionLabel => string.Format(I18n.GetString("MergeWorkspace_SelectedCount"), SelectionCount);
    public bool CanAdopt => CanResolve && SelectionCount > 0 && _selectionRevision == CurrentSelectionRevision && _selection.Values.All(c => c.CanCheck);
    private static bool Allows(MergeChangeItem row, MergeResolutionChoice choice) => row.Conflict.SemanticSubject is { } subject
        ? MergeSessionStore.SemanticChoiceAllowed(subject, choice)
        : choice is MergeResolutionChoice.Ours or MergeResolutionChoice.Theirs || !row.Conflict.Subject.Paths.IsEmpty && choice is MergeResolutionChoice.Base or MergeResolutionChoice.Delete;
    private bool CanChoose(MergeResolutionChoice choice) => CanAdopt && SelectedDecisions().All(c => Allows(c, choice));
    public bool CanAdoptOurs => CanChoose(MergeResolutionChoice.Ours);
    public bool CanAdoptTheirs => CanChoose(MergeResolutionChoice.Theirs);
    public bool CanAdoptBase => CanChoose(MergeResolutionChoice.Base);
    public bool CanDelete => CanChoose(MergeResolutionChoice.Delete);
    public bool CanClear => CanAdopt && SelectedDecisions().Any(c => c.Resolution is not null);
    public bool CanUndo => CanResolve && Operations?.CanUndo == true;
    public bool CanNext => IsListAvailable && _unresolved > 0;
    public string RecomputeSummary => Operations?.RecomputeSummary ?? "";
    public string AdoptOursLabel => I18n.GetString("MergeWorkspace_Adopt") + " " + OursLabel;
    public string AdoptTheirsLabel => SelectedChange is { IsAutomatic: false, Conflict: var c }
        && c.SemanticSubject is null && c.Theirs.IsEmpty && c.TheirsDirectories.IsEmpty
        ? I18n.GetString("MergeWorkspace_AdoptDeletion") : I18n.GetString("MergeWorkspace_Adopt") + " " + TheirsLabel;
    private MergeChangeItem[] SelectedDecisions() => _selection.Values.Where(c => c.CanCheck).ToArray();
    private void AddRow(MergeConflict conflict, MergeResolution? resolution, bool automatic)
        => Changes.Add(new(conflict, resolution, automatic));
    private void FilterChanged()
    {
        ++_loadGeneration; _filterPending = true; IsLoadingChanges = false;
        _loadedRevision = null;
        ClearSelection();
        ViewState = ViewState with { Search = Search, Filter = Filter };
        if (!_active) return;
        // Reload is generation-checked so earlier searches never replace newer results.
        _filterDelay?.Cancel(); _filterDelay?.Dispose(); _filterDelay = new();
        var token = _filterDelay.Token;
        TaskObserver.Observe(DelayedReloadAsync(token), "Merge filter");
    }
    private async Task DelayedReloadAsync(CancellationToken token)
    {
        try { await Task.Delay(180, token); if (_active) await ReloadChangesAsync(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public async Task LoadMoreAsync()
    {
        if (_loadingMore || !_active || !IsListAvailable || Changes.Count >= _matching || State.Session is not { } session || Operations is null) return;
        _loadingMore = true; var generation = _loadGeneration; var offset = Changes.Count;
        var search = Search; var filter = Filter;
        try
        {
            var page = await Task.Run(() => Operations.QueryChanges(session, search, filter, offset, 100));
            if (!_active || generation != _loadGeneration || session.Revision != State.Session?.Revision) return;
            foreach (var row in page.Rows) AddRow(row.Conflict, row.Resolution, row.Automatic);
            _matching = page.Matching; Notify(); SelectionUpdated?.Invoke();
        }
        catch (Exception ex) { ReportError(ex); }
        finally { _loadingMore = false; }
    }
    public Task ClearAsync()
    {
        if (!CanClear || Operations is null) return Task.CompletedTask;
        var ids = SelectedDecisions().Select(c => c.Id).ToArray();
        return SaveDecisionsAsync(() => Operations.ClearAsync(ids));
    }
    public async Task NextAsync()
    {
        if (!CanNext || Operations?.Runtime is not { } runtime || State.Session is not { } session) return;
        var revision = CurrentSelectionRevision;
        var search = Search; var selectedId = SelectedChange?.Id;
        var currentPath = SelectedChange?.Path ?? "";
        var next = await Task.Run(() =>
        {
            if (session.Plan.SemanticProvider is not null) return runtime.MergeSessions.NextSemanticConflict(session, search, selectedId);
            var candidates = runtime.MergeSessions.ConflictIndex(session, search).Where(c => !c.Resolved)
                .OrderBy(c => c.Path, StringComparer.Ordinal).ToArray();
            return candidates.FirstOrDefault(c => StringComparer.Ordinal.Compare(c.Path, currentPath) > 0).Id ?? candidates.FirstOrDefault().Id;
        });
        if (next is null || !_active || revision != CurrentSelectionRevision || search != Search) return;
        ClearSelection(); _filter = "Unresolved";
        ViewState = ViewState with { Filter = _filter };
        await ReloadChangesAsync();
        while (_active && IsListAvailable && revision == CurrentSelectionRevision && Changes.All(c => c.Id != next) && Changes.Count < _matching)
        {
            var previousCount = Changes.Count;
            await LoadMoreAsync();
            if (Changes.Count == previousCount) break;
        }
        if (!_active || revision != CurrentSelectionRevision || search != Search || Filter != "Unresolved") return;
        SelectOnly(Changes.FirstOrDefault(c => c.Id == next)); ContentChanged?.Invoke(); Notify();
    }
    partial void CancelPreview() { _preview?.Cancel(); _preview?.Dispose(); _preview = null; }
    partial void SelectionChanged()
    {
        CancelPreview(); Notify();
        if (!_active || !IsListAvailable || SelectedChange is null) return;
        _preview = new(); var token = _preview.Token; var row = SelectedChange;
        TaskObserver.Observe(LoadPreviewAsync(row, token), "Merge preview");
    }
    private async Task LoadPreviewAsync(MergeChangeItem row, CancellationToken token)
    {
        try
        {
            OursLines.Clear(); TheirsLines.Clear(); BaseText = ""; OursInfo = ""; TheirsInfo = "";
            PreviewNotice = I18n.GetString("MergeWorkspace_PreviewLoading"); Notify();
            var conflict = row.Conflict;
            if (conflict.SemanticSubject is { } subject)
            {
                PreviewNotice = I18n.PickBest(subject.Description.Translations, subject.Description.Default) ?? subject.Description.Default;
                Notify(); return;
            }
            if (!conflict.CanImportFile)
            {
                static IEnumerable<string> Entries(IReadOnlyDictionary<string, MergeFileValue> files, IEnumerable<string> directories)
                    => files.Keys.Concat(directories.Select(path => path + "/")).Order(StringComparer.Ordinal);
                foreach (var path in Entries(conflict.Ours, conflict.OursDirectories)) OursLines.Add(new("", "", path));
                foreach (var path in Entries(conflict.Theirs, conflict.TheirsDirectories)) TheirsLines.Add(new("", "", path));
                BaseText = conflict.Kind is MergeConflictKind.SourceBoundary or MergeConflictKind.SourceRoster
                    ? I18n.GetString("MergeWorkspace_GroupBaseUnavailable") : string.Join("\n", Entries(conflict.Base, conflict.BaseDirectories));
                OursInfo = string.Format(I18n.GetString("MergeWorkspace_GroupCount"), conflict.Ours.Count, conflict.OursDirectories.Length);
                TheirsInfo = string.Format(I18n.GetString("MergeWorkspace_GroupCount"), conflict.Theirs.Count, conflict.TheirsDirectories.Length);
                PreviewNotice = I18n.GetString(row.IsAutomatic ? "MergeWorkspace_AutomaticNotice" : "MergeWorkspace_GroupDecision"); Notify(); return;
            }
            var pathKey = conflict.Subject.Paths[0];
            var previews = await Task.WhenAll(new[] { conflict.Ours, conflict.Theirs, conflict.Base }
                .Select(side => MergeFilePreview.ReadAsync(side.GetValueOrDefault(pathKey), token)));
            token.ThrowIfCancellationRequested();
            var left = previews[0]; var right = previews[1];
            static string Info(MergeFilePreview p) => I18n.GetString("MergeWorkspace_Preview_" + p.Kind) + $" · {p.Length:N0} B"
                + (string.IsNullOrEmpty(p.EncodingName) ? "" : " · " + p.EncodingName);
            OursInfo = Info(left); TheirsInfo = Info(right); BaseText = Info(previews[2]) + "\n" + previews[2].Text;
            PreviewNotice = I18n.GetString(row.IsAutomatic ? "MergeWorkspace_AutomaticNotice" : right.Kind == MergePreviewKind.Missing || left.Kind == MergePreviewKind.Missing
                ? "MergeWorkspace_DeletionNotice" : "MergeWorkspace_WholeFileNotice");
            if (left.CanCompare && right.CanCompare && (left.Text.Length > 0 || right.Text.Length > 0))
            {
                var diff = await Task.Run(() => new SideBySideDiffBuilder(new DiffPlex.Differ()).BuildDiffModel(left.Text, right.Text, false), token);
                token.ThrowIfCancellationRequested();
                static MergeDiffLine Line(DiffPiece p) => new(p.Position?.ToString() ?? "", p.Type switch
                { ChangeType.Inserted => "+", ChangeType.Deleted => "−", ChangeType.Modified => "~", _ => "" }, p.Text ?? "");
                foreach (var line in diff.OldText.Lines) OursLines.Add(Line(line));
                foreach (var line in diff.NewText.Lines) TheirsLines.Add(Line(line));
            }
            else
            {
                if (left.Text.Length > 0)
                {
                    var i = 0; foreach (var line in left.Text.Split('\n')) OursLines.Add(new((++i).ToString(), "", line));
                }
                if (right.Text.Length > 0)
                {
                    var i = 0; foreach (var line in right.Text.Split('\n')) TheirsLines.Add(new((++i).ToString(), "", line));
                }
            }
            Notify();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) ReportError(ex); }
    }
}

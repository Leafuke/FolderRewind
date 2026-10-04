using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Services;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed record MergeDiffLine(string Number, string Marker, string Text);
public sealed partial class MergePageViewModel
{
    private CancellationTokenSource? _preview;
    private int _total, _unresolved, _matching;
    private bool _loadingMore;
    private string _search = "", _filter = "All";
    public string Search { get => _search; set { if (SetProperty(ref _search, value ?? "")) FilterChanged(); } }
    public string Filter { get => _filter; set { if (SetProperty(ref _filter, value)) FilterChanged(); } }
    public bool ShowBase { get => ViewState.ShowBase; set { ViewState = ViewState with { ShowBase = value }; Notify(); } }
    public string PreviewNotice { get; private set; } = "";
    public string OursInfo { get; private set; } = "";
    public string TheirsInfo { get; private set; } = "";
    public string BaseText { get; private set; } = "";
    public string DecisionText => SelectedChange?.Description ?? "";
    public ObservableCollection<MergeDiffLine> OursLines { get; } = [];
    public ObservableCollection<MergeDiffLine> TheirsLines { get; } = [];
    public int CheckedCount => Changes.Count(c => c.IsChecked && !c.IsAutomatic);
    public string SelectionLabel => string.Format(I18n.GetString("MergeWorkspace_SelectedCount"), CheckedCount);
    public bool CanAdopt => CanResolve && (CheckedCount > 0 || SelectedChange is { IsAutomatic: false });
    public bool CanClear => CanAdopt && SelectedDecisions().Any(c => c.Resolution is not null);
    public bool CanUndo => CanResolve && Operations?.CanUndo == true;
    public bool CanNext => !IsBusy && _unresolved > 0;
    public string RecomputeSummary => Operations?.RecomputeSummary ?? "";
    public string AdoptOursLabel => I18n.GetString("MergeWorkspace_Adopt") + " " + OursLabel;
    public string AdoptTheirsLabel => CheckedCount == 0 && SelectedChange is { IsAutomatic: false, Conflict.Theirs.Count: 0 }
        ? I18n.GetString("MergeWorkspace_AdoptDeletion") : I18n.GetString("MergeWorkspace_Adopt") + " " + TheirsLabel;
    private MergeChangeItem[] SelectedDecisions()
    {
        var rows = Changes.Where(c => c.IsChecked && !c.IsAutomatic).ToArray();
        return rows.Length == 0 && SelectedChange is { IsAutomatic: false } row ? [row] : rows;
    }
    private void AddRow(MergeConflict conflict, MergeResolution? resolution, bool automatic, bool isChecked = false)
    {
        var row = new MergeChangeItem(conflict, resolution, automatic) { IsChecked = isChecked };
        row.PropertyChanged += OnRowChanged; Changes.Add(row);
    }
    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MergeChangeItem.IsChecked)) Notify(); }
    private void FilterChanged()
    {
        ViewState = ViewState with { Search = Search, Filter = Filter };
        if (!_active) return;
        // Reload is generation-checked so earlier searches never replace newer results.
        TaskObserver.Observe(ReloadChangesAsync(), "Merge filter");
    }
    public async Task LoadMoreAsync()
    {
        if (_loadingMore || !_active || IsBusy || Changes.Count >= _matching || State.Session is not { } session || Operations is null) return;
        _loadingMore = true; var generation = _loadGeneration; var offset = Changes.Count;
        var search = Search; var filter = Filter;
        try
        {
            var page = await Task.Run(() => Operations.QueryChanges(session, search, filter, offset, 100));
            if (!_active || generation != _loadGeneration || session.Revision != State.Session?.Revision) return;
            foreach (var row in page.Rows) AddRow(row.Conflict, row.Resolution, row.Automatic);
            _matching = page.Matching; Notify();
        }
        catch (Exception ex) { ReportError(ex); }
        finally { _loadingMore = false; }
    }
    public Task ClearAsync() => Operations is null ? Task.CompletedTask : ExecuteAsync(() => Operations.ClearAsync(SelectedDecisions().Select(c => c.Id).ToArray()));
    public async Task NextAsync()
    {
        if (Operations?.Runtime is not { } runtime || State.Session is not { } session) return;
        var currentPath = SelectedChange?.Path ?? "";
        var next = await Task.Run(() =>
        {
            var candidates = runtime.MergeSessions.ConflictIndex(session).Where(c => !c.Resolved && c.Path.Contains(Search, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Path, StringComparer.Ordinal).ToArray();
            return candidates.FirstOrDefault(c => StringComparer.Ordinal.Compare(c.Path, currentPath) > 0).Id ?? candidates.FirstOrDefault().Id;
        });
        if (next is null || !_active) return;
        _filter = "Unresolved"; await ReloadChangesAsync();
        while (_active && Changes.All(c => c.Id != next) && Changes.Count < _matching) await LoadMoreAsync();
        SelectedChange = Changes.FirstOrDefault(c => c.Id == next); ContentChanged?.Invoke(); Notify();
    }
    partial void CancelPreview() { _preview?.Cancel(); _preview?.Dispose(); _preview = null; }
    partial void SelectionChanged()
    {
        CancelPreview(); Notify();
        if (!_active || SelectedChange is null) return;
        _preview = new(); var token = _preview.Token; var row = SelectedChange;
        TaskObserver.Observe(LoadPreviewAsync(row, token), "Merge preview");
    }
    private async Task LoadPreviewAsync(MergeChangeItem row, CancellationToken token)
    {
        try
        {
            OursLines.Clear(); TheirsLines.Clear(); BaseText = ""; PreviewNotice = I18n.GetString("MergeWorkspace_PreviewLoading"); Notify();
            var conflict = row.Conflict;
            if (conflict.Subject.Paths.Length != 1 || conflict.Kind is MergeConflictKind.PathStructure or MergeConflictKind.SourceBoundary or MergeConflictKind.SourceRoster)
            {
                foreach (var path in conflict.Ours.Keys) OursLines.Add(new("", "", path));
                foreach (var path in conflict.Theirs.Keys) TheirsLines.Add(new("", "", path));
                BaseText = string.Join("\n", conflict.Base.Keys);
                PreviewNotice = I18n.GetString("MergeWorkspace_GroupDecision"); Notify(); return;
            }
            var pathKey = conflict.Subject.Paths[0];
            var previews = await Task.WhenAll(new[] { conflict.Ours, conflict.Theirs, conflict.Base }
                .Select(side => MergeFilePreview.ReadAsync(side.GetValueOrDefault(pathKey), token)));
            token.ThrowIfCancellationRequested();
            var left = previews[0]; var right = previews[1];
            static string Info(MergeFilePreview p) => I18n.GetString("MergeWorkspace_Preview_" + p.Kind) + $" · {p.Length:N0} B · {p.EncodingName}";
            OursInfo = Info(left); TheirsInfo = Info(right); BaseText = Info(previews[2]) + "\n" + previews[2].Text;
            PreviewNotice = I18n.GetString(right.Kind == MergePreviewKind.Missing || left.Kind == MergePreviewKind.Missing
                ? "MergeWorkspace_DeletionNotice" : "MergeWorkspace_WholeFileNotice");
            if (left.CanCompare && right.CanCompare)
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
                var i = 0; foreach (var line in left.Text.Split('\n')) OursLines.Add(new((++i).ToString(), "", line));
                i = 0; foreach (var line in right.Text.Split('\n')) TheirsLines.Add(new((++i).ToString(), "", line));
            }
            Notify();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) ReportError(ex); }
    }
}

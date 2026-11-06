using FolderRewind.History.LocalState;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed partial class MergePageViewModel
{
    // Includes selected results outside the incrementally loaded presentation collection.
    private readonly Dictionary<string, MergeChangeItem> _selection = new(StringComparer.Ordinal);
    private CancellationTokenSource? _selectAllCancellation;
    private string? _selectionRevision;
    private bool _restoreSelection = true, _filterPending, _savingDecisions;
    private string? CurrentSelectionRevision => State.Session is { } s ? $"{s.Id}:{s.Plan.Revision}:{s.Revision}" : null;
    public int SelectionCount => _selection.Count;
    public bool IsSelectingAll { get; private set; }
    public bool IsLoadingChanges { get; private set; }
    public bool IsListAvailable => IsIdle && !_savingDecisions && !IsSelectingAll && !IsLoadingChanges && !_filterPending
        && State.Session is { } session && _loadedRevision == $"{session.Id}:{session.Revision}";
    public bool CanSelectAll => IsListAvailable && ShowWorkspace && _matching > 0;
    public bool CanClearSelection => IsIdle && (SelectionCount > 0 || IsSelectingAll);
    public bool IsSelected(string id) => _selection.ContainsKey(id);
    public string AdoptBaseLabel => I18n.GetString("Merge_AdoptBase.Content");
    public string DeleteChoiceLabel => I18n.GetString("Merge_DeleteChoice.Content");
    public string ClearChoiceLabel => I18n.GetString("MergeWorkspace_ClearButton.Content");
    public event Action? SelectionUpdated;

    public void UpdateSelection(IEnumerable<MergeChangeItem> visibleSelection,
        IEnumerable<MergeChangeItem> added, IEnumerable<MergeChangeItem> removed, bool additive)
    {
        if (!IsListAvailable) return;
        _restoreSelection = false;
        if (additive)
        {
            foreach (var row in removed) _selection.Remove(row.Id);
            foreach (var row in added) _selection[row.Id] = row;
        }
        else
        {
            _selection.Clear();
            foreach (var row in visibleSelection) _selection[row.Id] = row;
        }
        PublishSelection();
    }

    public void SelectOnly(MergeChangeItem? row)
    {
        _selection.Clear(); _restoreSelection = false;
        if (row is not null) _selection[row.Id] = row;
        PublishSelection();
    }

    public void ClearSelection()
    {
        CancelSelectAll();
        SelectOnly(null);
    }

    private void PublishSelection()
    {
        // Read-only rows may be inspected alone, but never enter a batch decision.
        if (_selection.Count > 1)
            foreach (var id in _selection.Values.Where(c => !c.CanCheck).Select(c => c.Id).ToArray()) _selection.Remove(id);
        _selectionRevision = CurrentSelectionRevision;
        SelectionChanged();
        SelectionUpdated?.Invoke();
    }

    private void CancelSelectAll()
    {
        _selectAllCancellation?.Cancel(); _selectAllCancellation?.Dispose(); _selectAllCancellation = null;
        IsSelectingAll = false;
    }

    public async Task SelectAllAsync()
    {
        if (!CanSelectAll || Operations is not { } operations || State.Session is not { } session) return;
        CancelSelectAll();
        var cancellation = _selectAllCancellation = new();
        var token = cancellation.Token;
        var revision = CurrentSelectionRevision;
        var search = Search; var filter = Filter;
        IsSelectingAll = true; _localError = null; CancelPreview(); Notify();
        try
        {
            var rows = await operations.QueryAllChangesAsync(session, search, filter, token);
            if (!_active || token.IsCancellationRequested || revision != CurrentSelectionRevision || search != Search || filter != Filter || IsBusy) return;
            _selection.Clear(); _restoreSelection = false;
            foreach (var value in rows)
            {
                var row = new MergeChangeItem(value.Conflict, value.Resolution, value.Automatic);
                if (row.CanCheck) _selection[row.Id] = row;
            }
            PublishSelection();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (_active && !token.IsCancellationRequested && revision == CurrentSelectionRevision) ReportError(ex); }
        finally
        {
            if (ReferenceEquals(_selectAllCancellation, cancellation))
            {
                CancelSelectAll(); SelectionChanged();
            }
        }
    }

    private async Task SaveDecisionsAsync(Func<Task> save)
    {
        var sessionId = State.Session?.Id;
        var revision = State.Session?.Revision;
        _savingDecisions = true;
        try
        {
            await ExecuteAsync(save);
            // The operation tracker reports failures in its snapshot rather than throwing.
            if (_active && State.Session?.Id == sessionId && State.Session?.Revision != revision
                && State.IsSaved && State.Error is null && State.Notice is null && _localError is null)
                ClearSelection();
        }
        finally { _savingDecisions = false; }
        if (_active) await ReloadChangesAsync();
    }

    public Task RetryDecisionSaveAsync() => Operations is { CanRetrySave: true } operations
        ? SaveDecisionsAsync(operations.RetrySaveAsync) : Task.CompletedTask;

    public string BatchUnavailableActions
    {
        get
        {
            if (!ShowBatchActions) return "";
            var unavailable = new List<string>();
            foreach (var (choice, label) in new[]
            {
                (MergeResolutionChoice.Ours, AdoptOursLabel), (MergeResolutionChoice.Theirs, AdoptTheirsLabel),
                (MergeResolutionChoice.Base, AdoptBaseLabel), (MergeResolutionChoice.Delete, DeleteChoiceLabel)
            })
                if (!_selection.Values.All(row => Allows(row, choice))) unavailable.Add(label);
            var reason = unavailable.Count == 0 ? "" : string.Format(I18n.GetString("MergeWorkspace_UnsupportedChoices"), string.Join(" / ", unavailable));
            if (!_selection.Values.Any(row => row.Resolution is not null))
                reason += (reason.Length == 0 ? "" : "\n") + I18n.GetString("MergeWorkspace_NoDecisionsToClear");
            return reason;
        }
    }
    public bool HasBatchUnavailableActions => BatchUnavailableActions.Length > 0;
}

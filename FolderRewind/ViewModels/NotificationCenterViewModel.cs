using CommunityToolkit.Mvvm.Input;
using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Linq;

namespace FolderRewind.ViewModels;

public sealed class NotificationItemViewModel(NotificationRecord record) : ViewModelBase
{
    public NotificationRecord Record { get; private set; } = record;
    public string Id => Record.Id;
    public string Title => NotificationContentFormatter.Title(Record.Content, Record.Title, I18n.GetString);
    public string Message => NotificationContentFormatter.Summary(Record.Content, Record.Message, I18n.GetString);
    public string Project => Record.Content?.ProjectName ?? "";
    public bool HasProject => !string.IsNullOrEmpty(Project);
    public string Timestamp => Record.UpdatedUtc.LocalDateTime.Date == DateTime.Today
        ? Record.UpdatedUtc.LocalDateTime.ToString("t") : Record.UpdatedUtc.LocalDateTime.ToString("g");
    public string FullTimestamp => UserDisplayFormatter.LongDateTime(Record.UpdatedUtc.LocalDateTime);
    public string ReadState => I18n.GetString(Record.IsRead ? "NotificationCenter_Read" : "NotificationCenter_Unread");
    public bool Unread => !Record.IsRead;
    public NotificationSeverity Severity => Record.Severity;
    public bool HasTarget => NotificationNavigationService.IsTargetAvailable(Record.Target);
    public string ActionText => I18n.GetString(NotificationContentFormatter.ActionResource(Record.Target));
    public string AutomationName => Title + ". " + Message + ". " + ReadState + ". " + FullTimestamp;
    public void Update(NotificationRecord next) { Record = next; OnPropertyChanged(string.Empty); }
}

public sealed partial class NotificationCenterViewModel : ViewModelBase, IDisposable
{
    private readonly NotificationReadingSession _reading = new();
    private bool _active, _interactionProtected;
    private NotificationRecord? _selected;
    private string _navigationMessage = "";
    public BatchObservableCollection<NotificationItemViewModel> Items { get; } = new();
    public event Action? Navigated;
    public event Action? ResetScrollRequested;
    public int BadgeCount => NotificationService.IsNotificationEnabled ? NotificationCenterService.ImportantUnreadCount : 0;
    public string BadgeText => BadgeCount > 99 ? "99+" : BadgeCount.ToString();
    public string BellName => I18n.Format("NotificationUI_BellName", BadgeCount);
    public bool HasBadge => BadgeCount > 0;
    public bool IsEmpty => Items.Count == 0;
    public bool ImportantOnly { get => _reading.ImportantOnly; set { _reading.ImportantOnly = value; SyncItems(); } }
    public bool UnreadOnly { get => _reading.UnreadOnly; set { _reading.UnreadOnly = value; SyncItems(); } }
    public bool HasSelection => _selected is not null;
    public bool ShowList => !HasSelection;
    public bool HasTarget => NotificationNavigationService.IsTargetAvailable(_selected?.Target);
    public bool HasLogs => _selected?.Target?.OperationId is not null;
    public bool HasNewResults => _reading.PendingCount > 0;
    public string NewResultsText => I18n.Format("NotificationUI_NewResults", _reading.PendingCount);
    public string DetailTitle => _selected is null ? "" : NotificationContentFormatter.Title(_selected.Content, _selected.Title, I18n.GetString);
    public string DetailProject => _selected?.Content?.ProjectName ?? "";
    public string DetailTimestamp => _selected is null ? "" : UserDisplayFormatter.LongDateTime(_selected.UpdatedUtc.LocalDateTime);
    public string DetailText => _selected is null ? "" : NotificationContentFormatter.Summary(_selected.Content, _selected.Message, I18n.GetString, true)
        + Environment.NewLine + Environment.NewLine + _selected.Details;
    public string TargetText => I18n.GetString(NotificationContentFormatter.ActionResource(_selected?.Target));
    public string NavigationMessage => _navigationMessage.Length > 0 ? _navigationMessage
        : _selected?.Target is { Kind: not NotificationTargetKind.None } && !HasTarget ? I18n.GetString("NotificationCenter_TargetUnavailable") : "";
    public bool HasNavigationMessage => NavigationMessage.Length > 0;
    public bool CanClear => NotificationCenterService.HasRead;
    public bool CanMarkAllRead => NotificationCenterService.UnreadCount > 0;
    public bool CanUndo => NotificationCenterService.UndoCount > 0;
    public string UndoText => I18n.Format("NotificationUI_Cleared", NotificationCenterService.UndoCount);

    public void Activate()
    {
        if (_active) return;
        _active = true; NotificationCenterService.Changed += OnChanged; NotificationService.SettingsChanged += OnChanged;
        Refresh();
    }
    public void Open()
    {
        _selected = null; _navigationMessage = ""; _interactionProtected = false;
        _reading.Open(NotificationCenterService.Snapshot()); SyncItems();
    }
    public void Close() => _reading.Close();
    public void SetReadingProtection(bool value)
    {
        _interactionProtected = value;
        _reading.SetProtection(value || HasSelection); SyncItems();
    }
    private void OnChanged() => EnqueueOnUiThread(() => { if (_active) Refresh(); });
    private void Refresh(bool force = false)
    { _reading.Observe(NotificationCenterService.Snapshot(), force); SyncItems(); }
    private void SyncItems()
    {
        var existing = Items.ToDictionary(i => i.Id);
        Items.Synchronize(_reading.Visible.Select(r =>
        {
            if (!existing.TryGetValue(r.Id, out var item)) item = new(r);
            else if (item.Record != r) item.Update(r);
            return item;
        }).ToArray());
        OnPropertyChanged(string.Empty);
    }
    public void RefreshUndo() { OnPropertyChanged(nameof(CanUndo)); OnPropertyChanged(nameof(UndoText)); }
    public void ShowDetails(string id)
    {
        _selected = NotificationCenterService.Find(id) ?? Items.FirstOrDefault(i => i.Id == id)?.Record;
        _navigationMessage = ""; _reading.SetProtection(true);
        if (_selected is { } selected) NotificationCenterService.MarkRead(selected.Id, selected.Revision);
        OnPropertyChanged(string.Empty);
    }
    public void OpenItemTarget(string id) { ShowDetails(id); OpenTarget(); }
    [RelayCommand] private void MarkAllRead() { NotificationCenterService.MarkAllRead(); Refresh(true); }
    [RelayCommand] private void ClearRead()
    {
        NotificationCenterService.ClearRead();
        if (_selected is not null && NotificationCenterService.Find(_selected.Id) is null) { _selected = null; _navigationMessage = ""; }
        Refresh(true);
    }
    [RelayCommand] private void UndoClear() { NotificationCenterService.UndoClear(); Refresh(true); }
    [RelayCommand] private void ApplyNewResults()
    {
        _selected = null; _reading.ApplyPending(); _reading.SetProtection(_interactionProtected);
        SyncItems(); ResetScrollRequested?.Invoke();
    }
    [RelayCommand] private void OpenTarget()
    {
        if (_selected is not { Target: { } target } selected) return;
        NotificationCenterService.MarkRead(selected.Id, selected.Revision);
        if (NotificationNavigationService.Open(target)) Navigated?.Invoke();
        else { _navigationMessage = I18n.GetString(NotificationNavigationService.IsTargetAvailable(target)
            ? "NotificationUI_NavigationCanceled" : "NotificationCenter_TargetUnavailable"); OnPropertyChanged(string.Empty); }
    }
    [RelayCommand] private void OpenLogs()
    {
        if (_selected?.Target?.OperationId is not { } operationId) return;
        NotificationCenterService.MarkRead(_selected.Id, _selected.Revision);
        if (NotificationNavigationService.Open(new(NotificationTargetKind.Logs, operationId))) Navigated?.Invoke();
        else { _navigationMessage = I18n.GetString("NotificationUI_NavigationCanceled"); OnPropertyChanged(string.Empty); }
    }
    public void CloseDetails()
    { _selected = null; _navigationMessage = ""; _reading.SetProtection(_interactionProtected); SyncItems(); }
    public void Dispose()
    { _active = false; NotificationCenterService.Changed -= OnChanged; NotificationService.SettingsChanged -= OnChanged; }
}

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
    public string Title => Record.Title;
    public string Message => Record.Message;
    public string Timestamp => UserDisplayFormatter.LongDateTime(Record.UpdatedUtc.LocalDateTime);
    public string ReadState => I18n.GetString(Record.IsRead ? "NotificationCenter_Read" : "NotificationCenter_Unread");
    public string SeverityText => I18n.GetString(Record.Severity switch
    {
        NotificationSeverity.Error => "Notification_Error_Title", NotificationSeverity.Warning => "Notification_Warning_Title",
        NotificationSeverity.Success => "Notification_Success_Title", _ => "Notification_Info_Title"
    });
    public void Update(NotificationRecord record)
    {
        if (Record == record) return;
        Record = record;
        OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Message)); OnPropertyChanged(nameof(Timestamp));
        OnPropertyChanged(nameof(ReadState)); OnPropertyChanged(nameof(SeverityText));
    }
}

public sealed partial class NotificationCenterViewModel : ViewModelBase, IDisposable
{
    private bool _active, _unreadOnly;
    private NotificationRecord? _selected;
    public BatchObservableCollection<NotificationItemViewModel> Items { get; } = new();
    public event Action? Navigated;
    public int BadgeCount => NotificationService.IsNotificationEnabled ? NotificationCenterService.UnreadCount : 0;
    public bool HasBadge => BadgeCount > 0;
    public bool IsEmpty => Items.Count == 0;
    public bool UnreadOnly { get => _unreadOnly; set { if (SetProperty(ref _unreadOnly, value)) Refresh(); } }
    public bool HasSelection => _selected is not null;
    public bool HasTarget => _selected?.Target is { Kind: not NotificationTargetKind.None };
    public bool HasLogs => _selected?.Target?.OperationId is not null;
    public string DetailTitle => _selected?.Title ?? "";
    public string DetailText => _selected?.Details ?? "";

    public void Activate()
    {
        if (_active) return;
        _active = true;
        NotificationCenterService.Changed += OnChanged;
        NotificationService.SettingsChanged += OnChanged;
        Refresh();
    }

    private void OnChanged() => EnqueueOnUiThread(() => { if (_active) Refresh(); });

    private void Refresh()
    {
        var snapshot = NotificationCenterService.Snapshot();
        var existing = Items.ToDictionary(i => i.Id);
        var items = snapshot.Where(r => !UnreadOnly || !r.IsRead).Select(r =>
        {
            if (!existing.TryGetValue(r.Id, out var item)) item = new(r);
            else item.Update(r);
            return item;
        }).ToArray();
        Items.Synchronize(items);
        if (_selected is not null) _selected = snapshot.FirstOrDefault(r => r.Id == _selected.Id) ?? _selected;
        OnPropertyChanged(nameof(BadgeCount)); OnPropertyChanged(nameof(HasBadge)); OnPropertyChanged(nameof(IsEmpty));
        RefreshDetails();
    }

    public void ShowDetails(string id)
    {
        _selected = NotificationCenterService.Find(id);
        if (_selected is not null) NotificationCenterService.MarkRead(id);
        RefreshDetails();
    }

    [RelayCommand]
    private void MarkAllRead() => NotificationCenterService.MarkAllRead();

    [RelayCommand]
    private void OpenTarget()
    {
        if (_selected?.Target is not { } target) return;
        NotificationCenterService.MarkRead(_selected.Id);
        if (NotificationNavigationService.Open(target)) Navigated?.Invoke();
    }

    [RelayCommand]
    private void OpenLogs()
    {
        if (_selected?.Target?.OperationId is not { } operationId) return;
        NotificationCenterService.MarkRead(_selected.Id);
        if (NotificationNavigationService.Open(new(NotificationTargetKind.Logs, operationId))) Navigated?.Invoke();
    }

    [RelayCommand]
    private void CloseDetails() { _selected = null; RefreshDetails(); }

    private void RefreshDetails()
    {
        OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(HasTarget)); OnPropertyChanged(nameof(HasLogs));
        OnPropertyChanged(nameof(DetailTitle)); OnPropertyChanged(nameof(DetailText));
    }

    public void Dispose()
    {
        _active = false;
        NotificationCenterService.Changed -= OnChanged;
        NotificationService.SettingsChanged -= OnChanged;
    }
}

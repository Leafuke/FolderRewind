using CommunityToolkit.Mvvm.Input;
using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed class AnnouncementItemViewModel : ViewModelBase
{
    internal AnnouncementItem Item { get; private set; }
    internal void Update(AnnouncementItem item) { Item = item; OnPropertyChanged(string.Empty); }
    internal AnnouncementItemViewModel(AnnouncementItem item) => Item = item;
    public string Id => Item.Id;
    public string Title => NoticeService.Text(Item).Title;
    public string Summary => NoticeService.Text(Item).Summary;
    public string StateText => I18n.GetString(ProductMessageService.State(Item.Key).Archived ? "Product_Archived"
        : ProductMessageService.State(Item.Key).IsRead ? "NotificationCenter_Read" : "NotificationCenter_Unread");
    public string Published => UserDisplayFormatter.LongDateTime(Item.PublishedAt.LocalDateTime);
    public string AutomationName => Title + ". " + StateText + ". " + Published;
}

public sealed partial class ProductMessagesViewModel : ViewModelBase
{
    private bool _active;
    private AnnouncementItem? _selected;
    private string _noticeTitle = "", _noticeBody = "", _noticeSummary = "", _actionMessage = "";
    private bool _showArchived;
    public BatchObservableCollection<AnnouncementItemViewModel> Notices { get; } = new();
    public bool Busy => ProductMessageService.IsRefreshing || AppUpdateService.Status == AppUpdateService.CheckStatus.Checking;
    public bool CanCheck => !Busy;
    public bool HasUpdate => AppUpdateService.Latest is not null;
    public bool NoNotices => Notices.Count == 0;
    public bool HasSelectedNotice => _selected is not null;
    public string NoticeTitle => _noticeTitle;
    public string NoticeBody => _noticeBody;
    public string NoticeSummary => _noticeSummary;
    public string NoticePublished => _selected is null ? "" : UserDisplayFormatter.LongDateTime(_selected.PublishedAt.LocalDateTime);
    public string ActionMessage => _actionMessage;
    public string PersistenceMessage => ProductMessageService.PersistenceError;
    public string NoticeStatus => NoticeService.LastCheckFailed ? I18n.GetString("Product_NoticeCheckFailed") : "";
    public string CurrentVersion => I18n.Format("Product_CurrentVersion", AppRuntimeInfo.GetApplicationVersion()?.ToString() ?? "?",
        I18n.GetString("Product_Channel_" + AppDistributionService.GetCurrentChannel()));
    public string UpdateStatus => I18n.GetString("Product_UpdateStatus_" + AppUpdateService.Status);
    public string LastCheck => AppUpdateService.CheckedUtc is { } at
        ? I18n.Format("Product_LastCheck", UserDisplayFormatter.LongDateTime(at.LocalDateTime)) : "";
    public string UpdateSummary => AppUpdateService.Latest is { } update
        ? I18n.Format("Product_UpdateSummary", update.CurrentVersion, update.LatestVersion) : "";
    public string UpdateHint => AppUpdateService.Latest is { } update ? I18n.GetString(update.InstallChannel switch
    { InstallChannel.Store => "Update_Dialog_StoreDelayHint", InstallChannel.Setup => "Update_Dialog_SetupExitHint", _ => "Update_Dialog_ManualMigrationHint" }) : "";
    public string UpdateBody => AppUpdateService.Latest is { } update
        ? AppUpdatePresentation.CreateContent(update.Target, update.ReleaseNotes, update.OfficialMetadata, I18n.GetString) : "";
    public string UpdateAction => AppUpdateService.Latest is { } update
        ? I18n.GetString(AppUpdatePresentation.PrimaryButtonResource(update.Target)) : "";
    public string UpdateChoice => ChoiceText(ProductMessageService.State(ProductMessageService.UpdateKey));
    public string NoticeChoice => _selected is null ? "" : ChoiceText(ProductMessageService.State(_selected.Key));
    public bool ShowArchived
    {
        get => _showArchived;
        set { if (SetProperty(ref _showArchived, value)) Refresh(); }
    }
    private static string ChoiceText(ProductMessageState state) => state.Ignored ? I18n.GetString("Product_Ignored")
        : state.Archived ? I18n.GetString("Product_Archived")
        : state.SnoozedUntil is { } at ? I18n.Format("Product_SnoozedUntil", UserDisplayFormatter.LongDateTime(at.LocalDateTime)) : "";

    public void Activate()
    {
        if (_active) return;
        _active = true;
        ProductMessageService.Changed += OnChanged; AppUpdateService.Changed += OnChanged;
        TaskObserver.Observe(InitializeAsync(), nameof(ProductMessagesViewModel)); Refresh();
    }
    private async Task InitializeAsync() { await ProductMessageService.InitializeAsync(); if (_active) Refresh(); }
    public void Deactivate()
    { _active = false; ProductMessageService.Changed -= OnChanged; AppUpdateService.Changed -= OnChanged; }
    private void OnChanged() => EnqueueOnUiThread(() => { if (_active) Refresh(); });
    private void Refresh()
    {
        var existing = Notices.ToDictionary(n => n.Id);
        Notices.Synchronize(ProductMessageService.Notices.Where(n => ShowArchived || !ProductMessageService.State(n.Key).Archived)
            .Select(n => { if (!existing.TryGetValue(n.Id, out var row)) return new AnnouncementItemViewModel(n); row.Update(n); return row; }).ToArray());
        OnPropertyChanged(string.Empty);
    }
    public void SelectNotice(string id)
    {
        _selected = ProductMessageService.Notices.FirstOrDefault(n => n.Id == id);
        if (_selected is null) { _noticeTitle = I18n.GetString("Product_NoticeUnavailable"); _noticeBody = _noticeSummary = ""; }
        else
        {
            var text = NoticeService.Text(_selected);
            _noticeTitle = text.Title; _noticeBody = text.Body; _noticeSummary = text.Summary;
            TaskObserver.Observe(ProductMessageService.SetChoiceAsync(_selected.Key, "read"), nameof(ProductMessagesViewModel));
        }
        OnPropertyChanged(string.Empty);
    }
    public void ReadUpdate() => TaskObserver.Observe(ProductMessageService.SetChoiceAsync(ProductMessageService.UpdateKey, "read"), nameof(ProductMessagesViewModel));
    [RelayCommand] private async Task CheckUpdateAsync()
    {
        _actionMessage = "";
        await ProductMessageService.RefreshAsync(false, true, true);
        ReadUpdate();
    }
    [RelayCommand] private Task RefreshNoticesAsync() => ProductMessageService.RefreshAsync(true, false, true);
    [RelayCommand] private async Task OpenUpdateAsync()
    {
        _actionMessage = I18n.GetString(await AppUpdateService.OpenAsync() ? "Product_ExternalOpened" : "Product_ExternalFailed");
        if (_active) OnPropertyChanged(nameof(ActionMessage));
    }
    [RelayCommand] private async Task OpenFallbackAsync()
    {
        _actionMessage = I18n.GetString(await AppUpdateService.OpenAsync(true) ? "Product_ExternalOpened" : "Product_ExternalFailed");
        if (_active) OnPropertyChanged(nameof(ActionMessage));
    }
    [RelayCommand] private Task UpdateChoiceAsync(string choice) => ProductMessageService.SetChoiceAsync(ProductMessageService.UpdateKey, choice);
    [RelayCommand] private Task NoticeChoiceAsync(string choice) => _selected is null ? Task.CompletedTask : ProductMessageService.SetChoiceAsync(_selected.Key, choice);
}

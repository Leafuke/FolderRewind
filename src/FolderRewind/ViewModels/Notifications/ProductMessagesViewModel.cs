using CommunityToolkit.Mvvm.Input;
using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed partial class AnnouncementItemViewModel : ViewModelBase
{
    internal AnnouncementItem Item { get; private set; }
    private bool _expanded, _bodyCreated, _available = true;
    internal AnnouncementItemViewModel(AnnouncementItem item) => Item = item;
    internal void Update(AnnouncementItem item) { Item = item; _available = true; OnPropertyChanged(string.Empty); }
    internal void Invalidate() { _available = false; _expanded = false; _bodyCreated = false; OnPropertyChanged(string.Empty); }
    public string Id => Item.Id;
    public string AutomationId => "ProductNotice_" + Id;
    public string BodyAutomationId => "ProductNoticeBody_" + Id;
    public string OptionsAutomationId => "ProductNoticeOptions_" + Id;
    public string Title => NoticeService.Text(Item).Title;
    public string Summary => NoticeService.Text(Item).Summary;
    public string Body => _available ? NoticeService.Text(Item).Body : "";
    public bool IsCritical => Item.Kind == "criticalCompatibility";
    public string CriticalLabel => I18n.GetString("Product_CriticalNotice");
    public string ApplicabilityLabel => I18n.GetString("Product_AppliesToCurrentVersion");
    public string ImportanceSummary => I18n.Format("Product_ImportantNoticeSummary", CriticalLabel, ApplicabilityLabel);
    public bool IsBodyCreated => _bodyCreated;
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (!SetProperty(ref _expanded, value)) return;
            if (value)
            {
                _bodyCreated = true;
                TaskObserver.Observe(ProductMessageService.SetChoiceAsync(Item.Key, "read"), nameof(AnnouncementItemViewModel));
            }
            OnPropertyChanged(string.Empty);
        }
    }
    private ProductMessageState State => ProductMessageService.State(Item.Key);
    public bool CanSnooze => !State.Archived && State.SnoozedUntil is null;
    public bool CanCancelSnooze => State.SnoozedUntil is not null;
    public bool CanArchive => !State.Archived;
    public bool CanRestore => State.Archived;
    public string Choice => ProductMessagesViewModel.ChoiceText(State);
    public string StateText => I18n.GetString(State.Archived ? "Product_Archived"
        : State.IsRead ? "NotificationCenter_Read" : "NotificationCenter_Unread");
    public string Published => I18n.Format("Product_NoticeMetadata", UserDisplayFormatter.Date(Item.PublishedAt.LocalDateTime), StateText);
    public string FullPublished => UserDisplayFormatter.LongDateTime(Item.PublishedAt.LocalDateTime);
    public string DetailLabel => I18n.GetString(IsExpanded ? "Product_CollapseNotice" : IsCritical ? "Product_ViewSteps" : "Product_ViewDetails");
    public string AutomationName => string.Join(". ", new[] { Title, IsCritical ? I18n.GetString("Product_CriticalNotice") + ". " + I18n.GetString("Product_AppliesToCurrentVersion") : "", StateText, FullPublished, DetailLabel }.Where(s => s.Length > 0));
    [RelayCommand] private Task ChooseAsync(string choice) => ProductMessageService.SetChoiceAsync(Item.Key, choice);
}

public sealed partial class ProductMessagesViewModel : ViewModelBase
{
    private bool _active, _initialized, _showArchived;
    private string _actionMessage = "", _navigationMessage = "";
    private readonly Dictionary<string, AnnouncementItemViewModel> _rows = new(StringComparer.Ordinal);
    internal event Action<IReadOnlySet<string>>? NoticesChanging;
    public BatchObservableCollection<AnnouncementItemViewModel> Notices { get; } = new();
    public bool IsCheckingUpdate => AppUpdateService.Status == AppUpdateService.CheckStatus.Checking;
    public bool IsRefreshingNotices => NoticeService.Status == NoticeService.CheckStatus.Checking;
    public bool Busy => ProductMessageService.IsRefreshing || IsCheckingUpdate || IsRefreshingNotices;
    public bool CanCheck => !Busy;
    public string CheckHint => IsRefreshingNotices ? I18n.GetString("Product_WaitForNotices")
        : Busy ? I18n.GetString("Product_WaitForUpdates") : "";
    public bool HasUpdate => AppUpdateService.Latest is not null;
    public bool CanSnoozeUpdate => !UpdateState.Ignored && UpdateState.SnoozedUntil is null;
    public bool CanCancelUpdateSnooze => UpdateState.SnoozedUntil is not null;
    public bool CanIgnoreUpdate => !UpdateState.Ignored;
    public bool CanRestoreUpdate => UpdateState.Ignored;
    private ProductMessageState UpdateState => ProductMessageService.State(ProductMessageService.UpdateKey);
    public string ActionMessage => _actionMessage;
    public string NavigationMessage => _navigationMessage;
    public string PersistenceMessage => ProductMessageService.PersistenceError;
    public string CurrentVersion => I18n.Format("Product_VersionLabel", DisplayVersion(AppRuntimeInfo.GetApplicationVersion()));
    public string VersionSummary => I18n.Format("Product_VersionSummary", DisplayVersion(AppRuntimeInfo.GetApplicationVersion()),
        AppUpdateService.Latest is { } update && AppUpdateService.Status is AppUpdateService.CheckStatus.Available or AppUpdateService.CheckStatus.NotChecked
            ? I18n.Format(AppUpdateService.Status == AppUpdateService.CheckStatus.NotChecked ? "Product_CachedUpdateTarget" : "Product_UpdateTarget", DisplayVersion(update.Target.LatestVersion))
            : I18n.GetString("Product_UpdateShort_" + AppUpdateService.Status));
    public string UpdateAutomationName => I18n.GetString("Product_UpdateSectionName") + ". " + VersionSummary;
    public string InstallationMethod => AppDistributionService.GetCurrentChannel() == InstallChannel.Unknown
        ? I18n.GetString("Product_UnknownInstallationMethod")
        : I18n.Format("Product_InstallationMethod", I18n.GetString("Product_Channel_" + AppDistributionService.GetCurrentChannel()));
    public string InstallationHint => AppDistributionService.GetCurrentChannel() == InstallChannel.Unknown ? I18n.GetString("Product_UnknownInstallationHint") : "";
    public string UpdateStatus => I18n.GetString("Product_UpdateStatus_" + AppUpdateService.Status);
    public string LastCheck => AppUpdateService.CheckedUtc is { } at
        ? I18n.Format("Product_LastCheck", UserDisplayFormatter.ShortDateTime(at.LocalDateTime)) : "";
    public string UpdateSummary => AppUpdateService.Latest is { } update
        ? I18n.Format("Product_UpdateTarget", DisplayVersion(update.Target.LatestVersion)) : "";
    public string UpdateHint => AppUpdateService.Latest is { } update ? I18n.GetString(update.InstallChannel switch
    { InstallChannel.Store => "Update_Dialog_StoreDelayHint", InstallChannel.Setup => "Update_Dialog_SetupExitHint", _ => "Update_Dialog_ManualMigrationHint" }) : "";
    public string UpdateMetadataHint
    {
        get
        {
            if (AppUpdateService.Latest is not { } update) return "";
            var hint = update.FallbackReason switch
            {
                UpdateDecisionReason.UnsafeReleaseUrl or UpdateDecisionReason.UnsafeAssetUrl => "Update_Dialog_UnsafeMetadataHint",
                UpdateDecisionReason.MissingAsset or UpdateDecisionReason.AmbiguousAsset => "Update_Dialog_PackageUnavailableHint",
                UpdateDecisionReason.UnsupportedArchitecture => "Update_Dialog_UnsupportedArchitectureHint",
                _ => ""
            };
            return string.Join(Environment.NewLine + Environment.NewLine, new[]
            { update.OfficialMetadata ? "" : I18n.GetString("Update_Dialog_MirrorMetadataHint"), hint.Length == 0 ? "" : I18n.GetString(hint) }.Where(s => s.Length > 0));
        }
    }
    public string UpdateBody => string.IsNullOrWhiteSpace(AppUpdateService.ReleaseNotes) ? I18n.GetString("Update_Dialog_EmptyNotes") : AppUpdateService.ReleaseNotes;
    public string UpdateAction => AppUpdateService.Latest is { } update
        ? I18n.GetString(AppUpdatePresentation.PrimaryButtonResource(update.Target)) : "";
    public string UpdateChoice => ChoiceText(UpdateState);
    public string NoticeCount => I18n.Format("Product_NoticeCount", Notices.Count, Notices.Count(n => n.IsCritical));
    private int ArchivedNoticeCount => _rows.Values.Count(n => ProductMessageService.State(n.Item.Key).Archived);
    public bool HasImportantNotices => _rows.Values.Any(n => n.IsCritical && !ProductMessageService.State(n.Item.Key).Archived);
    public string NoticeHeaderSummary
    {
        get
        {
            if (IsRefreshingNotices) return I18n.GetString("Product_NoticesLoading");
            if (NoticeService.LastCheckFailed) return I18n.GetString(NoticeService.HasValidCache ? "Product_NoticesHeaderFailedCache" : "Product_NoticesHeaderFailed");
            if (!_initialized || !NoticeService.HasValidCache) return I18n.GetString("Product_NoticesHeaderDefault");
            var current = _rows.Values.Where(n => !ProductMessageService.State(n.Item.Key).Archived).ToArray();
            var important = current.Count(n => n.IsCritical);
            return important > 0 ? I18n.Format("Product_NoticeCount", current.Length, important)
                : current.Length > 0 ? I18n.Format("Product_ApplicableNoticeCount", current.Length)
                : ArchivedNoticeCount > 0 ? I18n.Format("Product_ArchivedNoticeCount", ArchivedNoticeCount)
                : I18n.GetString("Product_NoApplicableNotices");
        }
    }
    public string NoticeAutomationName => I18n.GetString("Product_NoticesSectionName") + ". " + NoticeHeaderSummary;
    public string ArchiveActionLabel => ShowArchived ? I18n.GetString("Product_BackToCurrentNotices") : I18n.Format("Product_ViewArchivedNotices", ArchivedNoticeCount);
    public string NoticeLastCheck => NoticeService.HasValidCache
        ? I18n.Format("Product_NoticesLastCheck", UserDisplayFormatter.ShortDateTime(NoticeService.FetchedUtc.LocalDateTime)) : "";
    public string NoticeStatus => IsRefreshingNotices ? I18n.GetString("Product_NoticesLoading")
        : NoticeService.LastCheckFailed ? I18n.GetString(NoticeService.HasValidCache ? "Product_NoticeCheckFailed" : "Product_NoticesFailedNoCache")
        : !_initialized ? I18n.GetString("Product_NoticesLoading")
        : NoticeService.Status == NoticeService.CheckStatus.NotChecked ? I18n.GetString(NoticeService.HasValidCache ? "Product_NoticesCached" : "Product_NoticesNotChecked") : "";
    public string NoticeEmpty => !_initialized || IsRefreshingNotices || !NoticeService.HasValidCache ? ""
        : Notices.Count > 0 ? ""
        : ShowArchived ? I18n.GetString("Product_NoArchivedNotices")
        : _rows.Values.Any(n => ProductMessageService.State(n.Item.Key).Archived) ? I18n.GetString("Product_AllNoticesArchived")
        : I18n.GetString(NoticeService.Status == NoticeService.CheckStatus.Succeeded ? "Product_NoApplicableNotices" : "Product_NoApplicableCachedNotices");
    public bool ShowArchived
    {
        get => _showArchived;
        set { if (SetProperty(ref _showArchived, value)) { _navigationMessage = ""; Refresh(); } }
    }
    internal static string ChoiceText(ProductMessageState state) => state.Ignored ? I18n.GetString("Product_Ignored")
        : state.Archived ? I18n.GetString("Product_Archived")
        : state.SnoozedUntil is { } at ? I18n.Format("Product_SnoozedUntil", UserDisplayFormatter.ShortDateTime(at.LocalDateTime)) : "";
    private static string DisplayVersion(Version? version) => version?.ToString(version.Revision > 0 ? 4 : 3) ?? "?";

    public void Activate()
    {
        if (_active) return;
        _active = true;
        ProductMessageService.Changed += OnChanged;
        AppUpdateService.Changed += OnChanged;
        NoticeService.Changed += OnChanged;
        TaskObserver.Observe(InitializeAsync(), nameof(ProductMessagesViewModel));
        Refresh();
    }
    private async Task InitializeAsync()
    {
        await ProductMessageService.InitializeAsync();
        if (_active) { _initialized = true; Refresh(); }
    }
    public void Deactivate()
    {
        _active = false;
        ProductMessageService.Changed -= OnChanged;
        AppUpdateService.Changed -= OnChanged;
        NoticeService.Changed -= OnChanged;
    }
    private void OnChanged() => EnqueueOnUiThread(() => { if (_active) Refresh(); });
    private void Refresh()
    {
        var applicable = ProductMessageService.Notices;
        var ids = applicable.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var row in _rows.Values.Where(n => !ids.Contains(n.Id)).ToArray())
        {
            if (row.IsExpanded) _navigationMessage = I18n.GetString("Product_NoticeUnavailable");
            row.Invalidate();
            _rows.Remove(row.Id);
        }
        foreach (var item in applicable)
        {
            if (_rows.TryGetValue(item.Id, out var row)) row.Update(item);
            else _rows.Add(item.Id, new(item));
        }
        var visible = _rows.Values.Where(n => ProductMessageService.State(n.Item.Key).Archived == ShowArchived)
            .OrderByDescending(n => n.IsCritical).ThenByDescending(n => n.Item.PublishedAt).ToArray();
        NoticesChanging?.Invoke(visible.Select(n => n.Id).ToHashSet(StringComparer.Ordinal));
        Notices.Synchronize(visible);
        OnPropertyChanged(string.Empty);
    }
    public bool SelectNotice(string id)
    {
        Refresh();
        if (!_rows.TryGetValue(id, out var row))
        {
            _navigationMessage = I18n.GetString("Product_NoticeUnavailable");
            OnPropertyChanged(nameof(NavigationMessage));
            return false;
        }
        ShowArchived = ProductMessageService.State(row.Item.Key).Archived;
        _navigationMessage = "";
        row.IsExpanded = true;
        OnPropertyChanged(nameof(NavigationMessage));
        return true;
    }
    public void ReadUpdate() => TaskObserver.Observe(ProductMessageService.SetChoiceAsync(ProductMessageService.UpdateKey, "read"), nameof(ProductMessagesViewModel));
    [RelayCommand] private async Task CheckUpdateAsync()
    {
        _actionMessage = "";
        await ProductMessageService.RefreshAsync(false, true, true);
        ReadUpdate();
    }
    [RelayCommand] private Task RefreshNoticesAsync() => ProductMessageService.RefreshAsync(true, false, true);
    [RelayCommand] private void ToggleArchive() => ShowArchived = !ShowArchived;
    [RelayCommand] private async Task OpenUpdateAsync()
    {
        ReadUpdate();
        await ReportOpenAsync(AppUpdateService.OpenAsync());
    }
    [RelayCommand] private Task OpenFallbackAsync() => ReportOpenAsync(AppUpdateService.OpenAsync(true));
    [RelayCommand] private Task OpenReleaseNotesAsync() => ReportOpenAsync(AppUpdateService.OpenReleaseNotesAsync(), true);
    private async Task ReportOpenAsync(Task<bool> operation, bool releaseNotes = false)
    {
        var opened = await operation;
        _actionMessage = releaseNotes && opened ? "" : I18n.GetString(releaseNotes
            ? "Product_ReleaseNotesOpenFailed"
            : opened ? "Product_ExternalOpened" : "Product_ExternalFailed");
        if (_active) OnPropertyChanged(nameof(ActionMessage));
    }
    [RelayCommand] private Task UpdateChoiceAsync(string choice) => ProductMessageService.SetChoiceAsync(ProductMessageService.UpdateKey, choice);
}

using CommunityToolkit.Mvvm.ComponentModel;
using FolderRewind.History.Domain;
using FolderRewind.Services;

namespace FolderRewind.ViewModels;

public sealed class BackupSizeViewModel(BackupSizeStatistics? statistics = null, HistoryBackupType backupType = HistoryBackupType.Unknown) : ObservableObject
{
    public BackupSizeStatistics Statistics { get; private set; } = statistics ?? BackupSizeStatistics.Unknown;
    public HistoryBackupType BackupType { get; private set; } = backupType;
    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set { if (SetProperty(ref _isLoading, value)) { OnPropertyChanged(nameof(ContentDisplay)); OnPropertyChanged(nameof(DependencyDisplay)); } }
    }
    public string ListDisplay => AddedDisplay;
    public string AddedDisplay => Display(Statistics.AddedStoredBytes);
    public string ContentDisplay => IsLoading && Statistics.LogicalContentBytes is null ? I18n.GetString("History_Size_Loading") : Display(Statistics.LogicalContentBytes);
    public string DependencyDisplay => IsLoading && Statistics.RestoreDependencyBytes is null ? I18n.GetString("History_Size_Loading") : Display(Statistics.RestoreDependencyBytes);
    public string BackupTypeLabel => I18n.GetString("History_Size_BackupTypeLabel");
    public string BackupTypeDisplay => I18n.GetString("History_BackupType_" + BackupType);
    public string AddedLabel => I18n.GetString("History_Size_AddedLabel");
    public string ContentLabel => I18n.GetString("History_Size_ContentLabel");
    public string DependencyLabel => I18n.GetString("History_Size_DependencyLabel");

    public void UpdateBackupType(HistoryBackupType type)
    {
        if (type == HistoryBackupType.Unknown || type == BackupType) return;
        BackupType = type;
        OnPropertyChanged(nameof(BackupTypeDisplay));
    }

    public void Update(BackupSizeStatistics statistics)
    {
        Statistics = statistics;
        OnPropertyChanged(nameof(ListDisplay));
        OnPropertyChanged(nameof(AddedDisplay));
        OnPropertyChanged(nameof(ContentDisplay));
        OnPropertyChanged(nameof(DependencyDisplay));
    }

    private static string Display(long? bytes)
    {
        if (bytes is not { } value) return I18n.GetString("History_Size_Unknown");
        if (value < 1024) return $"{UserDisplayFormatter.Number(value)} B";
        if (value < 1024L * 1024) return $"{UserDisplayFormatter.Number(value / 1024d, 1)} KB";
        if (value < 1024L * 1024 * 1024) return $"{UserDisplayFormatter.Number(value / 1024d / 1024d, 1)} MB";
        return $"{UserDisplayFormatter.Number(value / 1024d / 1024d / 1024d, 2)} GB";
    }
}

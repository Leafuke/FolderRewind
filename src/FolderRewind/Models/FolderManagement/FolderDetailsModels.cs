using System.Collections.ObjectModel;

namespace FolderRewind.Models;

public sealed class FolderStatisticsSnapshot
{
    public long TotalBytes { get; init; }

    public int FileCount { get; init; }

    public int DirectoryCount { get; init; }
}

public sealed class FolderDetailsItem : ObservableObject
{
    private string _value = string.Empty;
    private string _description = string.Empty;
    private bool _isLoading;
    private bool _isError;

    public string Label { get; init; } = string.Empty;

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value ?? string.Empty);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value ?? string.Empty);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public bool IsError
    {
        get => _isError;
        set => SetProperty(ref _isError, value);
    }
}

public sealed class FolderDetailsSection
{
    public string Title { get; init; } = string.Empty;

    public ObservableCollection<FolderDetailsItem> Items { get; init; } = new();
}

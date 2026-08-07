using System.Collections.ObjectModel;

namespace FolderRewind.Models;

public sealed class GameDiscoverySettings : ObservableObject
{
    private ObservableCollection<GameLibraryRootSetting> _libraryRoots = new();
    private string _secondaryManifestPath = string.Empty;
    private string _overridePath = string.Empty;

    public ObservableCollection<GameLibraryRootSetting> LibraryRoots
    {
        get => _libraryRoots;
        set => SetProperty(ref _libraryRoots, value ?? new ObservableCollection<GameLibraryRootSetting>());
    }

    public string SecondaryManifestPath
    {
        get => _secondaryManifestPath;
        set => SetProperty(ref _secondaryManifestPath, value?.Trim() ?? string.Empty);
    }

    public string OverridePath
    {
        get => _overridePath;
        set => SetProperty(ref _overridePath, value?.Trim() ?? string.Empty);
    }
}

public sealed class GameLibraryRootSetting : ObservableObject
{
    private GameStore _store;
    private string _path = string.Empty;
    private bool _isEnabled = true;
    private bool _isAutoDetected;

    public GameStore Store { get => _store; set => SetProperty(ref _store, value); }
    public string Path { get => _path; set => SetProperty(ref _path, value?.Trim() ?? string.Empty); }
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    public bool IsAutoDetected { get => _isAutoDetected; set => SetProperty(ref _isAutoDetected, value); }
}

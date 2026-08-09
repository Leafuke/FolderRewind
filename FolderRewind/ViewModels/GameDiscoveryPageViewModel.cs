using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Discovery;
using FolderRewind.Services.Plugins;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed class GameDiscoveryPageViewModel : ViewModelBase, IDisposable
{
    public static readonly Uri PrimaryManifestUri = new(
        "https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml");

    private readonly LudusaviManifestCacheService _cacheService;
    private readonly HttpClient _httpClient;
    private CancellationTokenSource? _operationCts;
    private bool _initialized;
    private bool _isBusy;
    private bool _hasCache;
    private string _cacheStatus = string.Empty;
    private string _progressText = string.Empty;
    private string _resultSummary = string.Empty;
    private string _searchText = string.Empty;
    private GameStore? _storeFilter;
    private DiscoveryCandidateStatus? _statusFilter;
    private GameDiscoveryCandidateItem? _selectedGame;
    private string _manifestRevision = string.Empty;

    public GameDiscoveryPageViewModel()
        : this(
            new LudusaviManifestCacheService(Path.Combine(ConfigService.ConfigDirectory, "cache", "ludusavi")),
            new HttpClient())
    {
    }

    internal GameDiscoveryPageViewModel(LudusaviManifestCacheService cacheService, HttpClient httpClient)
    {
        _cacheService = cacheService;
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("FolderRewind/GameDiscovery");
    }

    public ObservableCollection<GameDiscoveryCandidateItem> Games { get; } = new();
    public ObservableCollection<GameDiscoveryCandidateItem> VisibleGames { get; } = new();
    public ObservableCollection<GameDiscoveryDraftItem> Drafts { get; } = new();
    public GameDiscoverySettings Settings => ConfigService.CurrentConfig.GlobalSettings.GameDiscovery;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanCancel));
        }
    }

    public bool CanStart => !IsBusy;
    public bool CanCancel => IsBusy;
    public bool HasCache { get => _hasCache; private set => SetProperty(ref _hasCache, value); }
    public string CacheStatus { get => _cacheStatus; private set => SetProperty(ref _cacheStatus, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }
    public string ResultSummary { get => _resultSummary; private set => SetProperty(ref _resultSummary, value); }
    public Visibility HasResultsVisibility => VisibleGames.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasDraftsVisibility => Drafts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public GameDiscoveryCandidateItem? SelectedGame
    {
        get => _selectedGame;
        set => SetProperty(ref _selectedGame, value);
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        RefreshDetectedLibraryRoots();
        await RefreshCacheStatusAsync(CancellationToken.None);
    }

    public async Task DownloadAndScanAsync()
    {
        await RunOperationAsync(async token =>
        {
            var update = await _cacheService.DownloadAndCompileAsync(
                _httpClient,
                PrimaryManifestUri,
                EmptyToNull(Settings.SecondaryManifestPath),
                EmptyToNull(Settings.OverridePath),
                CreateProgress(),
                token);
            ApplyCacheMetadata(update.Metadata);
            ProgressText = update.Status == LudusaviManifestUpdateStatus.NotModified
                ? I18n.GetString("GameDiscovery_Status_NotModified")
                : I18n.GetString("GameDiscovery_Status_Downloaded");
            await ScanCoreAsync(token);
        });
    }

    public async Task ImportAndScanAsync(string manifestPath)
    {
        await RunOperationAsync(async token =>
        {
            var update = await _cacheService.ImportAndCompileAsync(
                manifestPath,
                EmptyToNull(Settings.SecondaryManifestPath),
                EmptyToNull(Settings.OverridePath),
                CreateProgress(),
                token);
            ApplyCacheMetadata(update.Metadata);
            await ScanCoreAsync(token);
        });
    }

    public Task ScanAsync() => RunOperationAsync(ScanCoreAsync);

    public void Cancel() => _operationCts?.Cancel();

    public void SetFilters(string? searchText, GameStore? store, DiscoveryCandidateStatus? status)
    {
        _searchText = searchText ?? string.Empty;
        _storeFilter = store;
        _statusFilter = status;
        RefreshVisibleGames();
    }

    public void AddLibraryRoot(GameStore store, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var existing = Settings.LibraryRoots.FirstOrDefault(root =>
            root.Store == store && string.Equals(root.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.IsEnabled = true;
            return;
        }
        Settings.LibraryRoots.Add(new GameLibraryRootSetting
        {
            Store = store,
            Path = fullPath,
            IsEnabled = true,
            IsAutoDetected = false
        });
    }

    public bool SaveSettings(out string errorMessage)
    {
        var result = ConfigService.SaveWithResult();
        errorMessage = result.ErrorMessage;
        return result.Success;
    }

    public void BuildDrafts()
    {
        Drafts.Clear();
        var presets = BackupPresetService.GetTemplates();
        foreach (var gameItem in Games.Where(item => item.IsSelected))
        {
            foreach (var setItem in gameItem.BackupSets.Where(item => item.IsSelected))
            {
                var selectedResourceIds = setItem.Resources
                    .Where(item => item.IsSelected && item.CanSelect)
                    .Select(item => item.Candidate.ResourceId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var draft = DiscoveryDraftService.CreateDraft(
                    gameItem.Candidate,
                    setItem.Candidate,
                    presets,
                    ConfigService.CurrentConfig.BackupConfigs,
                    _manifestRevision,
                    selectedResourceIds,
                    setItem.SelectedPreset?.Preset);
                Drafts.Add(new GameDiscoveryDraftItem(draft));
            }
        }
        OnPropertyChanged(nameof(HasDraftsVisibility));
    }

    public IReadOnlyList<BackupResourceCandidate> GetSelectedBroadRootResources() => Games
        .Where(game => game.IsSelected)
        .SelectMany(game => game.BackupSets.Where(set => set.IsSelected))
        .SelectMany(set => set.Resources)
        .Where(resource => resource.IsSelected
                           && resource.CanSelect
                           && resource.Candidate.RequiresExplicitConfirmation)
        .Select(resource => resource.Candidate)
        .ToList();

    public BackupConfigDraftCommitResult CommitDrafts()
    {
        foreach (var item in Drafts)
        {
            item.Draft.IsSelected = item.IsSelected;
            item.Draft.ProposedConfig.Name = item.ConfigName.Trim();
            item.Draft.ProposedConfig.DestinationPath = item.DestinationPath.Trim();
        }
        var result = DiscoveryDraftService.Commit(Drafts.Select(item => item.Draft));
        if (result.Success)
        {
            Drafts.Clear();
            OnPropertyChanged(nameof(HasDraftsVisibility));
            RefreshStatuses();
        }
        return result;
    }

    public void Dispose()
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _httpClient.Dispose();
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (IsBusy) return;
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            await operation(_operationCts.Token);
        }
        catch (OperationCanceledException) when (_operationCts.IsCancellationRequested)
        {
            ProgressText = I18n.GetString("GameDiscovery_Status_Cancelled");
        }
        catch (Exception ex)
        {
            ProgressText = I18n.Format("GameDiscovery_Status_Failed", ex.Message);
            LogService.LogError($"Game discovery failed: {ex.Message}", "GameDiscovery", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ScanCoreAsync(CancellationToken token)
    {
        var current = await _cacheService.EnsureCurrentAsync(
            EmptyToNull(Settings.SecondaryManifestPath),
            EmptyToNull(Settings.OverridePath),
            CreateProgress(),
            token);
        if (current == null)
        {
            HasCache = false;
            CacheStatus = I18n.GetString("GameDiscovery_Cache_Missing");
            ProgressText = I18n.GetString("GameDiscovery_Status_NoCache");
            return;
        }
        ApplyCacheMetadata(current.Value.Metadata);
        PluginService.Initialize();
        var providers = new List<IFolderRewindDiscoveryProvider>
        {
            new LudusaviDiscoveryProvider(_cacheService)
        };
        providers.AddRange(PluginService.GetDiscoveryProviders());
        var discoveryService = new GameDiscoveryService(providers);
        var stopwatch = Stopwatch.StartNew();
        var result = await discoveryService.DiscoverAsync(BuildRequest(), CreateProgress(), token);
        Games.Clear();
        foreach (var game in result.Candidates.OrderBy(candidate => candidate.Definition.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            Games.Add(new GameDiscoveryCandidateItem(
                game,
                FindMatchingPresets(game),
                ConfigService.CurrentConfig.BackupConfigs.Select(config => config.DiscoveryOrigin)));
        }
        RefreshVisibleGames();
        ResultSummary = I18n.Format(
            "GameDiscovery_ResultSummary",
            Games.Count,
            Games.Sum(game => game.BackupSets.Sum(set => set.Resources.Count)),
            result.Diagnostics.Count(diagnostic => diagnostic.Severity == DiscoveryDiagnosticSeverity.Warning),
            stopwatch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.CurrentCulture));
        ProgressText = result.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == DiscoveryDiagnosticSeverity.Error)?.Message
            ?? I18n.GetString("GameDiscovery_Status_Complete");
        LogService.LogInfo(
            $"Discovery revision={_manifestRevision}, games={Games.Count}, resources={Games.Sum(game => game.BackupSets.Sum(set => set.Resources.Count))}, diagnostics={result.Diagnostics.Count}, elapsedMs={stopwatch.ElapsedMilliseconds}",
            "GameDiscovery");
    }

    private DiscoveryRequest BuildRequest()
    {
        var enabled = Settings.LibraryRoots.Where(root => root.IsEnabled).ToList();
        return new DiscoveryRequest
        {
            Mode = DiscoveryRequestMode.FullMachine,
            StoreRoots = enabled
                .Where(root => !root.IsAutoDetected)
                .GroupBy(root => root.Store)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(root => root.Path).ToList()),
            DisabledAutoRoots = Settings.LibraryRoots
                .Where(root => root.IsAutoDetected && !root.IsEnabled)
                .Select(root => root.Path)
                .ToList()
        };
    }

    private IProgress<DiscoveryProgress> CreateProgress()
    {
        return new Progress<DiscoveryProgress>(progress =>
        {
            var total = progress.Total is > 0 ? $" ({progress.Completed}/{progress.Total})" : string.Empty;
            ProgressText = $"{progress.Message}{total}";
        });
    }

    private IEnumerable<BackupPreset> FindMatchingPresets(DiscoveredGameCandidate game)
    {
        return BackupPresetService.GetTemplates().Where(preset => preset.DiscoverySources.Any(source =>
            source.Kind == BackupPresetDiscoverySourceKind.ProviderReference
            && string.Equals(source.ProviderId, game.Definition.ProviderId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(source.DefinitionId, game.Definition.DefinitionId, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task RefreshCacheStatusAsync(CancellationToken token)
    {
        try
        {
            var current = await _cacheService.EnsureCurrentAsync(
                EmptyToNull(Settings.SecondaryManifestPath),
                EmptyToNull(Settings.OverridePath),
                progress: null,
                cancellationToken: token);
            if (current == null)
            {
                HasCache = false;
                CacheStatus = I18n.GetString("GameDiscovery_Cache_Missing");
                return;
            }
            ApplyCacheMetadata(current.Value.Metadata);
        }
        catch (Exception ex)
        {
            HasCache = false;
            CacheStatus = I18n.Format("GameDiscovery_Cache_Invalid", ex.Message);
            LogService.LogWarning($"Ludusavi cache validation failed: {ex.Message}", "GameDiscovery");
        }
    }

    private void RefreshDetectedLibraryRoots()
    {
        foreach (var pair in GameLibraryRootDetector.DetectDefaults())
        {
            foreach (var path in pair.Value.Where(Directory.Exists))
            {
                var fullPath = Path.GetFullPath(path);
                if (Settings.LibraryRoots.Any(root =>
                        root.Store == pair.Key
                        && string.Equals(
                            DiscoveryResourcePlanner.NormalizePath(root.Path),
                            DiscoveryResourcePlanner.NormalizePath(fullPath),
                            StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                Settings.LibraryRoots.Add(new GameLibraryRootSetting
                {
                    Store = pair.Key,
                    Path = fullPath,
                    IsEnabled = true,
                    IsAutoDetected = true
                });
            }
        }
    }

    private void ApplyCacheMetadata(LudusaviManifestCacheMetadata metadata)
    {
        HasCache = true;
        _manifestRevision = metadata.SourceSha256;
        CacheStatus = I18n.Format(
            "GameDiscovery_Cache_Ready",
            metadata.UpdatedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            ShortRevision(metadata.SourceSha256),
            metadata.SourceKind);
        if (metadata.Warnings.Count > 0)
        {
            CacheStatus += Environment.NewLine + string.Join(Environment.NewLine, metadata.Warnings);
        }
    }

    private void RefreshStatuses()
    {
        foreach (var game in Games)
        {
            game.RefreshStatus(ConfigService.CurrentConfig.BackupConfigs.Select(config => config.DiscoveryOrigin));
        }
        RefreshVisibleGames();
    }

    private void RefreshVisibleGames()
    {
        VisibleGames.Clear();
        foreach (var item in Games.Where(item => DiscoveryPresentationService.Matches(
                     item.Candidate,
                     item.Status,
                     _searchText,
                     _storeFilter,
                     _statusFilter)))
        {
            VisibleGames.Add(item);
        }
        if (SelectedGame == null || !VisibleGames.Contains(SelectedGame))
        {
            SelectedGame = VisibleGames.FirstOrDefault();
        }
        OnPropertyChanged(nameof(HasResultsVisibility));
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string ShortRevision(string revision) => revision.Length <= 12 ? revision : revision[..12];
}

public sealed class GameDiscoveryCandidateItem : FolderRewind.Models.ObservableObject
{
    private bool _isSelected = true;
    private DiscoveryCandidateStatus _status;

    public GameDiscoveryCandidateItem(
        DiscoveredGameCandidate candidate,
        IEnumerable<BackupPreset> presets,
        IEnumerable<DiscoveryOrigin?> existingOrigins)
    {
        Candidate = candidate;
        _status = DiscoveryPresentationService.GetStatus(candidate, existingOrigins);
        var presetItems = new[] { BackupPresetService.CreateStandardGamePreset() }
            .Concat(presets)
            .GroupBy(preset => preset.ShareId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new GameDiscoveryPresetItem(group.First()))
            .ToList();
        foreach (var set in candidate.BackupSets)
        {
            BackupSets.Add(new GameDiscoveryBackupSetItem(set, presetItems));
        }
    }

    public DiscoveredGameCandidate Candidate { get; }
    public string Name => Candidate.Definition.DisplayName;
    public string Stores => string.Join(", ", Candidate.Installations.Select(item => item.Store).Distinct());
    public string StatusText => Status switch
    {
        DiscoveryCandidateStatus.UpToDate => I18n.GetString("GameDiscovery_Status_UpToDate"),
        DiscoveryCandidateStatus.NewResources => I18n.GetString("GameDiscovery_Status_NewResources"),
        _ => I18n.GetString("GameDiscovery_Status_New")
    };
    public string InstallationSummary => string.Join(Environment.NewLine, Candidate.Installations.Select(installation =>
        $"{installation.Store}: {installation.BasePath}"));
    public string Notes => string.Join(Environment.NewLine, Candidate.Definition.Notes);
    public string NativeCloud => string.Join(Environment.NewLine, Candidate.Definition.NativeCloud.Select(item => $"{item.Key}: {item.Value}"));
    public ObservableCollection<GameDiscoveryBackupSetItem> BackupSets { get; } = new();
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public DiscoveryCandidateStatus Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); } }

    public void RefreshStatus(IEnumerable<DiscoveryOrigin?> origins) => Status = DiscoveryPresentationService.GetStatus(Candidate, origins);
}

public sealed class GameDiscoveryBackupSetItem : FolderRewind.Models.ObservableObject
{
    private bool _isSelected = true;
    private GameDiscoveryPresetItem? _selectedPreset;

    public GameDiscoveryBackupSetItem(BackupSetCandidate candidate, IReadOnlyList<GameDiscoveryPresetItem> presets)
    {
        Candidate = candidate;
        foreach (var resource in candidate.Resources)
        {
            Resources.Add(new GameDiscoveryResourceItem(resource));
        }
        foreach (var preset in presets)
        {
            Presets.Add(preset);
        }
        SelectedPreset = Presets.Count(item => item.Preset.IsRecommended) == 1
            ? Presets.Single(item => item.Preset.IsRecommended)
            : Presets.FirstOrDefault();
        _isSelected = Resources.Any(item => item.IsSelected);
    }

    public BackupSetCandidate Candidate { get; }
    public string Name => Candidate.DisplayName;
    public ObservableCollection<GameDiscoveryResourceItem> Resources { get; } = new();
    public ObservableCollection<GameDiscoveryPresetItem> Presets { get; } = new();
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public GameDiscoveryPresetItem? SelectedPreset { get => _selectedPreset; set => SetProperty(ref _selectedPreset, value); }
}

public sealed class GameDiscoveryResourceItem : FolderRewind.Models.ObservableObject
{
    private bool _isSelected;

    public GameDiscoveryResourceItem(BackupResourceCandidate candidate)
    {
        Candidate = candidate;
        _isSelected = DiscoveryPresentationService.IsSelectedByDefault(candidate);
    }

    public BackupResourceCandidate Candidate { get; }
    public bool CanSelect => DiscoveryPresentationService.CanSelect(Candidate);
    public bool IsSelected { get => _isSelected; set { if (CanSelect) SetProperty(ref _isSelected, value); } }
    public string Name => Candidate.DisplayName;
    public string Expression => Candidate.OriginalExpression;
    public string Tags => string.Join(", ", Candidate.Tags);
    public string Evidence => string.Join("; ", Candidate.Evidence.Select(item => $"{item.Confidence}: {item.Description}"));
    public string PathSummary => Candidate.Kind == BackupResourceKind.Registry
        ? string.Empty
        : I18n.GetString(Candidate.FixedRootExists
            ? "GameDiscovery_ResourcePathExists"
            : "GameDiscovery_ResourcePathMissing");
    public string SupportText => Candidate.SupportState switch
    {
        BackupResourceSupportState.UnsupportedRegistry => I18n.GetString("GameDiscovery_Resource_RegistryUnsupported"),
        BackupResourceSupportState.UnsupportedConstraint => I18n.GetString("GameDiscovery_Resource_ConstraintUnsupported"),
        BackupResourceSupportState.InvalidPath => I18n.GetString("GameDiscovery_Resource_InvalidPath"),
        BackupResourceSupportState.UnsafeRoot => I18n.GetString("GameDiscovery_Resource_UnsafeRoot"),
        _ when Candidate.IsSuppressed => I18n.Format("GameDiscovery_Resource_Suppressed", Candidate.SuppressedByProviderId, Candidate.SuppressionReason),
        _ when !string.IsNullOrWhiteSpace(Candidate.ConflictWarning) => Candidate.ConflictWarning,
        _ when Candidate.RequiresExplicitConfirmation => Candidate.SafetyWarning,
        _ => string.Empty
    };

}

public sealed class GameDiscoveryPresetItem
{
    public GameDiscoveryPresetItem(BackupPreset preset) => Preset = preset;
    public BackupPreset Preset { get; }
    public string Name => Preset.IsRecommended
        ? I18n.Format("GameDiscovery_Preset_Recommended", Preset.Name)
        : Preset.Name;
}

public sealed class GameDiscoveryDraftItem : FolderRewind.Models.ObservableObject
{
    private bool _isSelected;
    private string _configName;
    private string _destinationPath;

    public GameDiscoveryDraftItem(BackupConfigDraft draft)
    {
        Draft = draft;
        _isSelected = draft.IsSelected;
        _configName = draft.ProposedConfig.Name;
        _destinationPath = draft.ProposedConfig.DestinationPath;
    }

    public BackupConfigDraft Draft { get; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public string ConfigName { get => _configName; set => SetProperty(ref _configName, value ?? string.Empty); }
    public string DestinationPath { get => _destinationPath; set => SetProperty(ref _destinationPath, value ?? string.Empty); }
    public string Reconciliation => Draft.Reconciliation switch
    {
        BackupConfigDraftReconciliation.UpToDate => I18n.GetString("GameDiscovery_Status_UpToDate"),
        BackupConfigDraftReconciliation.NewResources => I18n.GetString("GameDiscovery_Status_NewResources"),
        _ => I18n.GetString("GameDiscovery_Status_New")
    };
    public string Issues => string.Join(Environment.NewLine, Draft.Issues.Select(issue => issue.Message));
}

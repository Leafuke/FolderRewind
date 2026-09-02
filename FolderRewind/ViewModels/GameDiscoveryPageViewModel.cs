using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Discovery;
using FolderRewind.Services.Plugins;
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
    private readonly GameDiscoverySettings _settings;
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
    private GameDiscoveryNavigationMode _navigationMode;
    private BackupPreset? _targetedPreset;
    private string _targetedConfigName = string.Empty;
    private string _pluginBatchPluginId = string.Empty;
    private string _pluginBatchPluginName = string.Empty;
    private string _pluginBatchKindName = string.Empty;
    private string _pluginBatchRoot = string.Empty;
    private string _pluginBatchSummary = string.Empty;
    private string _pluginBatchSkippedSummary = string.Empty;
    private string _pluginBatchFatalMessage = string.Empty;
    private string _pluginBatchBroadRootSummary = string.Empty;
    private bool _isPluginBatchBroadRootConfirmed;
    private ConfigKindReference? _pluginBatchKind;
    private PluginBatchCreationPlan? _pluginBatchPlan;

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
        _settings = CloneSettings(ConfigService.CurrentConfig.GlobalSettings.GameDiscovery);
        _settings.PropertyChanged += OnSettingsPropertyChanged;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("FolderRewind/GameDiscovery");
    }

    public ObservableCollection<GameDiscoveryCandidateItem> Games { get; } = new();
    public ObservableCollection<GameDiscoveryCandidateItem> VisibleGames { get; } = new();
    public ObservableCollection<GameDiscoveryDraftItem> Drafts { get; } = new();
    public ObservableCollection<PluginBatchCreationSummaryItem> PluginBatchItems { get; } = new();
    public GameDiscoverySettings Settings => _settings;
    public string SecondaryManifestPath { get => Settings.SecondaryManifestPath; set => Settings.SecondaryManifestPath = value; }
    public string OverridePath { get => Settings.OverridePath; set => Settings.OverridePath = value; }
    public ObservableCollection<GameLibraryRootSetting> LibraryRoots => Settings.LibraryRoots;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanCommitPluginBatch));
        }
    }

    public bool CanStart => !IsBusy;
    public bool CanCancel => IsBusy;
    public bool HasCache { get => _hasCache; private set => SetProperty(ref _hasCache, value); }
    public string CacheStatus { get => _cacheStatus; private set => SetProperty(ref _cacheStatus, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }
    public string ResultSummary { get => _resultSummary; private set => SetProperty(ref _resultSummary, value); }
    public bool HasResults => VisibleGames.Count > 0;
    public bool HasDrafts => Drafts.Count > 0;
    public bool IsTargetedMode => _navigationMode == GameDiscoveryNavigationMode.PresetTargeted;
    public bool IsPluginBatchMode => _navigationMode == GameDiscoveryNavigationMode.PluginBatch;
    public bool IsStandardDiscoveryMode => !IsPluginBatchMode;
    public bool IsFullMachineMode => _navigationMode == GameDiscoveryNavigationMode.FullMachine;
    public string PluginBatchPluginName => _pluginBatchPluginName;
    public string PluginBatchKindName => _pluginBatchKindName;
    public string PluginBatchRoot => _pluginBatchRoot;
    public string PluginBatchSummary { get => _pluginBatchSummary; private set => SetProperty(ref _pluginBatchSummary, value); }
    public string PluginBatchSkippedSummary { get => _pluginBatchSkippedSummary; private set => SetProperty(ref _pluginBatchSkippedSummary, value); }
    public string PluginBatchFatalMessage { get => _pluginBatchFatalMessage; private set { if (SetProperty(ref _pluginBatchFatalMessage, value)) OnPropertyChanged(nameof(HasPluginBatchFatalMessage)); } }
    public bool HasPluginBatchFatalMessage => !string.IsNullOrWhiteSpace(PluginBatchFatalMessage);
    public string PluginBatchBroadRootSummary { get => _pluginBatchBroadRootSummary; private set => SetProperty(ref _pluginBatchBroadRootSummary, value); }
    public bool RequiresPluginBatchBroadRootConfirmation => GetSelectedPluginBatchBroadRootResources().Count > 0;
    public bool IsPluginBatchBroadRootConfirmed
    {
        get => _isPluginBatchBroadRootConfirmed;
        set
        {
            if (!SetProperty(ref _isPluginBatchBroadRootConfirmed, value)) return;
            OnPropertyChanged(nameof(CanCommitPluginBatch));
        }
    }
    public bool CanCommitPluginBatch => IsPluginBatchMode
        && !IsBusy
        && _pluginBatchPlan != null
        && PluginBatchItems.Any(item => item.IsSelected)
        && string.IsNullOrWhiteSpace(PluginBatchFatalMessage)
        && (!RequiresPluginBatchBroadRootConfirmation || IsPluginBatchBroadRootConfirmed);
    public int PluginBatchSkippedCount => (_pluginBatchPlan?.ExistingCount ?? 0)
        + (_pluginBatchPlan?.UnavailableCount ?? 0);
    public int HiddenSelectedCount => Games.Count(item => item.IsSelected && !VisibleGames.Contains(item));
    public string HiddenSelectionSummary => HiddenSelectedCount == 0
        ? string.Empty
        : I18n.Format("GameDiscovery_HiddenSelection", HiddenSelectedCount);

    public GameDiscoveryCandidateItem? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (!SetProperty(ref _selectedGame, value)) return;
            OnPropertyChanged(nameof(SelectedGameName));
            OnPropertyChanged(nameof(SelectedGameInstallationSummary));
            OnPropertyChanged(nameof(SelectedGameNotes));
            OnPropertyChanged(nameof(SelectedGameNativeCloud));
            OnPropertyChanged(nameof(SelectedGameBackupSets));
        }
    }

    public string SelectedGameName => SelectedGame?.Name ?? string.Empty;
    public string SelectedGameInstallationSummary => SelectedGame?.InstallationSummary ?? string.Empty;
    public string SelectedGameNotes => SelectedGame?.Notes ?? string.Empty;
    public string SelectedGameNativeCloud => SelectedGame?.NativeCloud ?? string.Empty;
    public IReadOnlyList<GameDiscoveryBackupSetItem> SelectedGameBackupSets =>
        SelectedGame is { } selectedGame
            ? selectedGame.BackupSets
            : Array.Empty<GameDiscoveryBackupSetItem>();

    public async Task InitializeAsync(GameDiscoveryNavigationParameter? parameter = null)
    {
        var requestedMode = parameter?.Mode ?? GameDiscoveryNavigationMode.FullMachine;
        if (!_initialized)
        {
            _initialized = true;
            RefreshDetectedLibraryRoots();
            if (requestedMode == GameDiscoveryNavigationMode.FullMachine)
            {
                await RefreshCacheStatusAsync(CancellationToken.None);
            }
        }

        _navigationMode = requestedMode;
        _targetedPreset = requestedMode == GameDiscoveryNavigationMode.PresetTargeted
            ? BackupPresetService.GetTemplates().FirstOrDefault(preset =>
                string.Equals(preset.ShareId, parameter?.PresetShareId, StringComparison.OrdinalIgnoreCase))
            : null;
        _targetedConfigName = _targetedPreset == null ? string.Empty : parameter?.RequestedConfigName ?? string.Empty;
        ResetPluginBatchState();
        if (requestedMode == GameDiscoveryNavigationMode.PluginBatch)
        {
            _pluginBatchPluginId = parameter?.PluginId?.Trim() ?? string.Empty;
            var requestedKind = parameter?.ConfigKind;
            _pluginBatchKind = requestedKind == null
                ? null
                : new ConfigKindReference
                {
                    OwnerId = requestedKind.OwnerId,
                    KindId = requestedKind.KindId
                };
            _pluginBatchRoot = parameter?.UserRoot?.Trim() ?? string.Empty;
            var availability = GameDiscoveryProviderFactory.GetPluginBatchAvailability(_pluginBatchPluginId);
            _pluginBatchPluginName = availability.PluginDisplayName;
            _pluginBatchKindName = PluginService.GetAllSupportedConfigKinds(includeEncrypted: true)
                .FirstOrDefault(kind => _pluginBatchKind != null
                    && string.Equals(kind.Kind.OwnerId, _pluginBatchKind.OwnerId, StringComparison.Ordinal)
                    && string.Equals(kind.Kind.KindId, _pluginBatchKind.KindId, StringComparison.Ordinal))
                ?.DisplayName ?? _pluginBatchKind?.KindId ?? string.Empty;
        }
        OnPropertyChanged(nameof(IsTargetedMode));
        OnPropertyChanged(nameof(IsPluginBatchMode));
        OnPropertyChanged(nameof(IsStandardDiscoveryMode));
        OnPropertyChanged(nameof(IsFullMachineMode));
        OnPropertyChanged(nameof(PluginBatchPluginName));
        OnPropertyChanged(nameof(PluginBatchKindName));
        OnPropertyChanged(nameof(PluginBatchRoot));
        OnPropertyChanged(nameof(CanCommitPluginBatch));
        if (requestedMode == GameDiscoveryNavigationMode.PresetTargeted)
        {
            if (_targetedPreset == null)
            {
                ProgressText = I18n.GetString("GameDiscovery_TargetedPresetUnavailable");
                Games.Clear();
                VisibleGames.Clear();
                Drafts.Clear();
                return;
            }
            await RunOperationAsync(ScanTargetedCoreAsync);
        }
        else if (requestedMode == GameDiscoveryNavigationMode.PluginBatch)
        {
            if (_pluginBatchKind == null
                || string.IsNullOrWhiteSpace(_pluginBatchPluginId)
                || string.IsNullOrWhiteSpace(_pluginBatchRoot)
                || !Directory.Exists(_pluginBatchRoot))
            {
                PluginBatchFatalMessage = I18n.GetString("GameDiscovery_PluginBatch_InvalidRequest");
                ProgressText = PluginBatchFatalMessage;
                return;
            }
            await RunOperationAsync(ScanPluginBatchCoreAsync);
        }
    }

    public async Task DownloadAndScanAsync()
    {
        if (!IsFullMachineMode)
        {
            await ScanAsync();
            return;
        }
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

    public Task ScanAsync() => RunOperationAsync(_navigationMode switch
    {
        GameDiscoveryNavigationMode.PresetTargeted => ScanTargetedCoreAsync,
        GameDiscoveryNavigationMode.PluginBatch => ScanPluginBatchCoreAsync,
        _ => ScanCoreAsync
    });

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
        if (IsBusy) return;
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
        if (IsBusy)
        {
            errorMessage = I18n.GetString("GameDiscovery_OperationRunning");
            return false;
        }
        var previous = ConfigService.CurrentConfig.GlobalSettings.GameDiscovery;
        ConfigService.CurrentConfig.GlobalSettings.GameDiscovery = CloneSettings(Settings);
        var result = ConfigService.SaveWithResult();
        if (!result.Success)
        {
            ConfigService.CurrentConfig.GlobalSettings.GameDiscovery = previous;
        }
        errorMessage = result.ErrorMessage;
        return result.Success;
    }

    public void BuildDrafts()
    {
        if (IsBusy || IsPluginBatchMode) return;
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
                    string.Empty,
                    selectedResourceIds,
                    setItem.SelectedPreset?.Preset);
                Drafts.Add(new GameDiscoveryDraftItem(draft));
            }
        }
        OnPropertyChanged(nameof(HasDrafts));
        if (_targetedPreset != null
            && Drafts.Count == 1
            && !string.IsNullOrWhiteSpace(_targetedConfigName))
        {
            Drafts[0].ConfigName = _targetedConfigName;
        }
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
        if (IsBusy)
        {
            return new BackupConfigDraftCommitResult
            {
                ErrorMessage = I18n.GetString("GameDiscovery_OperationRunning")
            };
        }
        if (Drafts.Count == 0 || Drafts.All(item => !item.IsSelected))
        {
            return new BackupConfigDraftCommitResult
            {
                ErrorMessage = I18n.GetString("GameDiscovery_SelectDraft")
            };
        }
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
            OnPropertyChanged(nameof(HasDrafts));
            RefreshStatuses();
        }
        return result;
    }

    public BackupConfigDraftCommitResult CommitPluginBatch()
    {
        if (!CanCommitPluginBatch || _pluginBatchPlan == null)
        {
            return new BackupConfigDraftCommitResult
            {
                ErrorMessage = I18n.GetString("GameDiscovery_PluginBatch_NothingToCommit")
            };
        }

        var result = DiscoveryDraftService.Commit(_pluginBatchPlan.Drafts);
        if (result.Success)
        {
            _pluginBatchPlan = null;
            OnPropertyChanged(nameof(RequiresPluginBatchBroadRootConfirmation));
            OnPropertyChanged(nameof(CanCommitPluginBatch));
        }
        return result;
    }

    public void Dispose()
    {
        _settings.PropertyChanged -= OnSettingsPropertyChanged;
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _httpClient.Dispose();
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.PropertyName))
            OnPropertyChanged(e.PropertyName);
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
        }
        else
        {
            ApplyCacheMetadata(current.Value.Metadata);
        }
        var composition = GameDiscoveryProviderFactory.Create(_cacheService);
        var discoveryService = new GameDiscoveryService(composition.Providers, composition.Diagnostics);
        var stopwatch = Stopwatch.StartNew();
        var result = await discoveryService.DiscoverAsync(BuildRequest(), CreateProgress(), token);
        ApplyResult(result, stopwatch, lockedPreset: null);
    }

    private async Task ScanTargetedCoreAsync(CancellationToken token)
    {
        var preset = _targetedPreset
            ?? throw new InvalidOperationException("The targeted preset is unavailable.");
        var composition = GameDiscoveryProviderFactory.Create(_cacheService);
        var discoveryService = new GameDiscoveryService(composition.Providers, composition.Diagnostics);
        var stopwatch = Stopwatch.StartNew();
        var result = await DiscoveryDraftService.DiscoverPresetTargetsAsync(
            preset,
            discoveryService,
            CreateProgress(),
            token);
        ApplyResult(result, stopwatch, preset);
    }

    private async Task ScanPluginBatchCoreAsync(CancellationToken token)
    {
        if (_pluginBatchKind == null)
        {
            throw new InvalidOperationException(I18n.GetString("GameDiscovery_PluginBatch_InvalidRequest"));
        }

        PluginBatchItems.Clear();
        PluginBatchSummary = string.Empty;
        PluginBatchSkippedSummary = string.Empty;
        PluginBatchFatalMessage = string.Empty;
        PluginBatchBroadRootSummary = string.Empty;
        IsPluginBatchBroadRootConfirmed = false;
        _pluginBatchPlan = null;
        OnPropertyChanged(nameof(RequiresPluginBatchBroadRootConfirmation));
        OnPropertyChanged(nameof(CanCommitPluginBatch));

        var composition = GameDiscoveryProviderFactory.CreateForPlugin(_pluginBatchPluginId);
        var discoveryService = new GameDiscoveryService(composition.Providers, composition.Diagnostics);
        var result = await discoveryService.DiscoverAsync(
            new DiscoveryRequest
            {
                Mode = DiscoveryRequestMode.UserRoots,
                UserRoots = [_pluginBatchRoot]
            },
            CreateProgress(),
            token);
        token.ThrowIfCancellationRequested();

        _pluginBatchPlan = PluginBatchCreationPlanner.Build(
            result,
            _pluginBatchPluginId,
            _pluginBatchKind,
            BackupPresetService.GetTemplates(),
            ConfigService.CurrentConfig.BackupConfigs);
        foreach (var item in _pluginBatchPlan.Items)
        {
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(PluginBatchCreationSummaryItem.IsSelected))
                {
                    RefreshPluginBatchSelectionState();
                }
            };
            PluginBatchItems.Add(item);
        }

        PluginBatchFatalMessage = _pluginBatchPlan.FatalMessage;
        PluginBatchSummary = I18n.Format(
            "GameDiscovery_PluginBatch_Summary",
            _pluginBatchPlan.Drafts.Count,
            _pluginBatchPlan.ExistingCount,
            _pluginBatchPlan.UnavailableCount);
        PluginBatchSkippedSummary = string.Join(Environment.NewLine, _pluginBatchPlan.SkippedMessages);
        RefreshPluginBatchSelectionState();
        ProgressText = !string.IsNullOrWhiteSpace(PluginBatchFatalMessage)
            ? PluginBatchFatalMessage
            : I18n.GetString("GameDiscovery_Status_Complete");
        OnPropertyChanged(nameof(RequiresPluginBatchBroadRootConfirmation));
        OnPropertyChanged(nameof(PluginBatchSkippedCount));
        OnPropertyChanged(nameof(CanCommitPluginBatch));
    }

    private IReadOnlyList<BackupResourceCandidate> GetSelectedPluginBatchBroadRootResources() =>
        PluginBatchItems
            .Where(item => item.IsSelected)
            .SelectMany(item => item.Draft.SelectedResources)
            .Where(resource => resource.RequiresExplicitConfirmation)
            .GroupBy(resource => resource.FixedRoot, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

    private void RefreshPluginBatchSelectionState()
    {
        var broadRootSummary = string.Join(
            Environment.NewLine,
            GetSelectedPluginBatchBroadRootResources()
                .Select(resource => resource.FixedRoot)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => $"- {path}"));
        if (!string.Equals(PluginBatchBroadRootSummary, broadRootSummary, StringComparison.Ordinal))
        {
            IsPluginBatchBroadRootConfirmed = false;
        }
        PluginBatchBroadRootSummary = broadRootSummary;
        OnPropertyChanged(nameof(RequiresPluginBatchBroadRootConfirmation));
        OnPropertyChanged(nameof(CanCommitPluginBatch));
    }

    private void ApplyResult(
        GameDiscoveryResult result,
        Stopwatch stopwatch,
        BackupPreset? lockedPreset)
    {
        Games.Clear();
        foreach (var game in result.Candidates.OrderBy(candidate => candidate.Definition.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new GameDiscoveryCandidateItem(
                game,
                lockedPreset == null
                    ? BackupPresetDiscoveryMatcher.FindMatches(game.Definition, BackupPresetService.GetTemplates())
                    : [lockedPreset],
                ConfigService.CurrentConfig.BackupConfigs,
                lockedPreset);
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(GameDiscoveryCandidateItem.IsSelected))
                {
                    OnPropertyChanged(nameof(HiddenSelectedCount));
                    OnPropertyChanged(nameof(HiddenSelectionSummary));
                }
            };
            Games.Add(item);
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
        var revisions = result.Candidates
            .SelectMany(candidate => candidate.BackupSets)
            .Select(set => set.DiscoveryRevision)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        LogService.LogInfo(
            $"Discovery revisions=[{string.Join(",", revisions)}], games={Games.Count}, resources={Games.Sum(game => game.BackupSets.Sum(set => set.Resources.Count))}, diagnostics={result.Diagnostics.Count}, elapsedMs={stopwatch.ElapsedMilliseconds}",
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
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HiddenSelectedCount));
        OnPropertyChanged(nameof(HiddenSelectionSummary));
    }

    private void ResetPluginBatchState()
    {
        Games.Clear();
        VisibleGames.Clear();
        Drafts.Clear();
        PluginBatchItems.Clear();
        SelectedGame = null;
        _pluginBatchPluginId = string.Empty;
        _pluginBatchPluginName = string.Empty;
        _pluginBatchKindName = string.Empty;
        _pluginBatchRoot = string.Empty;
        _pluginBatchKind = null;
        _pluginBatchPlan = null;
        PluginBatchSummary = string.Empty;
        PluginBatchSkippedSummary = string.Empty;
        PluginBatchFatalMessage = string.Empty;
        PluginBatchBroadRootSummary = string.Empty;
        IsPluginBatchBroadRootConfirmed = false;
        ResultSummary = string.Empty;
        ProgressText = string.Empty;
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasDrafts));
        OnPropertyChanged(nameof(RequiresPluginBatchBroadRootConfirmation));
        OnPropertyChanged(nameof(PluginBatchSkippedCount));
        OnPropertyChanged(nameof(CanCommitPluginBatch));
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string ShortRevision(string revision) => revision.Length <= 12 ? revision : revision[..12];

    private static GameDiscoverySettings CloneSettings(GameDiscoverySettings? source) => new()
    {
        SecondaryManifestPath = source?.SecondaryManifestPath ?? string.Empty,
        OverridePath = source?.OverridePath ?? string.Empty,
        LibraryRoots = new ObservableCollection<GameLibraryRootSetting>(
            (source?.LibraryRoots ?? new ObservableCollection<GameLibraryRootSetting>()).Select(root =>
                new GameLibraryRootSetting
                {
                    Store = root.Store,
                    Path = root.Path,
                    IsEnabled = root.IsEnabled,
                    IsAutoDetected = root.IsAutoDetected
                }))
    };
}

public sealed class GameDiscoveryCandidateItem : FolderRewind.Models.ObservableObject
{
    private bool _isSelected = true;
    private DiscoveryCandidateStatus _status;

    public GameDiscoveryCandidateItem(
        DiscoveredGameCandidate candidate,
        IEnumerable<BackupPreset> presets,
        IEnumerable<BackupConfig> existingConfigs,
        BackupPreset? lockedPreset = null)
    {
        Candidate = candidate;
        var configs = existingConfigs.ToList();
        _status = DiscoveryPresentationService.GetStatus(candidate, configs.Select(config => config.DiscoveryOrigin));
        var presetItems = (lockedPreset == null
                ? new[] { BackupPresetService.CreateStandardGamePreset() }.Concat(presets)
                : presets)
            .GroupBy(preset => preset.ShareId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new GameDiscoveryPresetItem(group.First()))
            .ToList();
        foreach (var set in candidate.BackupSets)
        {
            BackupSets.Add(new GameDiscoveryBackupSetItem(
                set,
                presetItems,
                DiscoverySetIdentityMatcher.FindUnique(set.Identity, configs, config => config.DiscoveryOrigin) != null,
                lockedPreset != null));
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
    private readonly bool _isPresetLocked;

    public GameDiscoveryBackupSetItem(
        BackupSetCandidate candidate,
        IReadOnlyList<GameDiscoveryPresetItem> presets,
        bool hasExistingConfiguration,
        bool isPresetLocked = false)
    {
        Candidate = candidate;
        HasExistingConfiguration = hasExistingConfiguration;
        _isPresetLocked = isPresetLocked;
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
        _isSelected = hasExistingConfiguration || Resources.Any(item => item.IsSelected);
    }

    public BackupSetCandidate Candidate { get; }
    public bool HasExistingConfiguration { get; }
    public bool CanChoosePreset => !HasExistingConfiguration && !_isPresetLocked;
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
    public bool CanEditConfiguration => Draft.ExistingConfig == null;
    public bool IsExistingConfiguration => Draft.ExistingConfig != null;
    public IReadOnlyList<DiscoverySourceChange> Changes => Draft.DiscoveryChanges;
    public string Reconciliation => Draft.Reconciliation switch
    {
        BackupConfigDraftReconciliation.UpToDate => I18n.GetString("GameDiscovery_Status_UpToDate"),
        BackupConfigDraftReconciliation.NewResources => I18n.GetString("GameDiscovery_Status_NewResources"),
        _ => I18n.GetString("GameDiscovery_Status_New")
    };
    public string Issues => string.Join(Environment.NewLine, Draft.Issues.Select(issue => issue.Message));
}

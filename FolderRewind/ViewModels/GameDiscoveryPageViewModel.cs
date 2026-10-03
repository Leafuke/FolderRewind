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
    private readonly CancellationTokenSource _sessionCts = new();
    private readonly List<SessionProgress> _progressSinks = new();
    private bool _disposed;
    private bool _resourcesDisposed;
    private int _initializing;
    private long _selectionRevision;
    private long _draftRevision;
    public bool IsSessionActive => !_disposed && !_sessionCts.IsCancellationRequested;
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
    private bool _pluginBatchIncludeKnownLocations;
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
            OnPropertyChanged(nameof(CanReview));
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
    public bool ShowResults => !HasDrafts && !IsPluginBatchMode;
    public int SelectedResourceCount => Games.Sum(game => game.BackupSets.Sum(set => set.SelectedResourceCount));
    public string SelectionSummary => I18n.Format("GameDiscovery_TotalSelection",
        Games.Sum(game => game.BackupSets.Count(set => set.IsSelected)), SelectedResourceCount);
    public bool CanReview => IsSessionActive && !IsBusy && SelectedResourceCount > 0;
    public string CommitLabel => I18n.GetString(ReturnDraftToSetup ? "GameDiscovery_ContinueSetup" : "GameDiscoveryPage_Commit.Content");
    public void ReturnToResults() { Drafts.Clear(); NotifyReview(); }
    private void NotifyReview()
    { OnPropertyChanged(nameof(HasDrafts)); OnPropertyChanged(nameof(ShowResults)); OnPropertyChanged(nameof(CanReview)); }
    private void NotifySelectionSummary()
    {
        OnPropertyChanged(nameof(SelectionSummary)); OnPropertyChanged(nameof(CanReview));
        OnPropertyChanged(nameof(HiddenSelectedCount)); OnPropertyChanged(nameof(HiddenSelectionSummary));
    }
    private void OnCandidateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(GameDiscoveryCandidateItem.SelectionState) or nameof(GameDiscoveryBackupSetItem.SelectedPreset))) return;
        _selectionRevision++;
        if (HasDrafts) ReturnToResults();
        NotifySelectionSummary();
    }
    private void ClearGames()
    {
        foreach (var game in Games) { game.PropertyChanged -= OnCandidateChanged; game.Dispose(); }
        Games.Clear();
        SelectedGame = null;
        NotifySelectionSummary();
    }
    public bool IsTargetedMode => _navigationMode == GameDiscoveryNavigationMode.PresetTargeted;
    public bool IsPluginBatchMode => _navigationMode == GameDiscoveryNavigationMode.PluginBatch;
    public bool IsStandardDiscoveryMode => !IsPluginBatchMode;
    public bool IsFullMachineMode => _navigationMode == GameDiscoveryNavigationMode.FullMachine;
    public string PluginBatchPluginName => _pluginBatchPluginName;
    public string PluginBatchKindName => _pluginBatchKindName;
    public string PluginBatchCommitLabel => ReturnDraftToSetup
        ? I18n.GetString("GameDiscovery_ContinueSetup")
        : I18n.GetString("GameDiscovery_PluginBatch_Commit.Content");
    public string PluginBatchRoot => _pluginBatchIncludeKnownLocations
        ? I18n.GetString("GameDiscovery_PluginBatch_KnownLocations")
        : _pluginBatchRoot;
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
        if (!IsSessionActive) return;
        _initializing++;
        try { await InitializeCoreAsync(parameter); }
        finally { _initializing--; ReleaseCompletedResources(); }
    }

    private async Task InitializeCoreAsync(GameDiscoveryNavigationParameter? parameter)
    {
        if (!IsSessionActive) return;
        ReturnDraftToSetup = parameter?.ReturnDraftToSetup == true;
        SetupReentry = ReturnDraftToSetup ? parameter : null;
        var requestedMode = parameter?.Mode ?? GameDiscoveryNavigationMode.FullMachine;
        if (!_initialized)
        {
            _initialized = true;
            RefreshDetectedLibraryRoots();
            if (requestedMode == GameDiscoveryNavigationMode.FullMachine)
            {
                await RefreshCacheStatusAsync(_sessionCts.Token);
                if (!IsSessionActive) return;
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
            _pluginBatchIncludeKnownLocations = parameter?.IncludeKnownLocations == true;
            var availability = GameDiscoveryProviderFactory.GetPluginBatchAvailability(_pluginBatchPluginId);
            _pluginBatchPluginName = availability.PluginDisplayName;
            _pluginBatchKindName = PluginService.GetAllSupportedConfigKinds(includeEncrypted: true)
                .FirstOrDefault(kind => _pluginBatchKind != null
                    && string.Equals(kind.Kind.OwnerId, _pluginBatchKind.OwnerId, StringComparison.Ordinal)
                    && string.Equals(kind.Kind.KindId, _pluginBatchKind.KindId, StringComparison.Ordinal))
                ?.DisplayName ?? _pluginBatchKind?.KindId ?? I18n.GetString("GameDiscovery_PluginBatch_AutomaticKind");
        }
        OnPropertyChanged(nameof(IsTargetedMode));
        OnPropertyChanged(nameof(IsPluginBatchMode));
        OnPropertyChanged(nameof(IsStandardDiscoveryMode));
        OnPropertyChanged(nameof(IsFullMachineMode));
        OnPropertyChanged(nameof(PluginBatchPluginName));
        OnPropertyChanged(nameof(PluginBatchKindName));
        OnPropertyChanged(nameof(PluginBatchCommitLabel));
        OnPropertyChanged(nameof(PluginBatchRoot));
        OnPropertyChanged(nameof(CanCommitPluginBatch));
        if (requestedMode == GameDiscoveryNavigationMode.PresetTargeted)
        {
            if (_targetedPreset == null)
            {
                ProgressText = I18n.GetString("GameDiscovery_TargetedPresetUnavailable");
                ClearGames();
                VisibleGames.Clear();
                Drafts.Clear();
                return;
            }
            await RunOperationAsync(ScanTargetedCoreAsync);
        }
        else if (requestedMode == GameDiscoveryNavigationMode.PluginBatch)
        {
            if (string.IsNullOrWhiteSpace(_pluginBatchPluginId)
                || (!_pluginBatchIncludeKnownLocations
                    && (string.IsNullOrWhiteSpace(_pluginBatchRoot) || !Directory.Exists(_pluginBatchRoot))))
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
            var update = await _cacheService.DownloadGenerationAsync(
                _httpClient,
                PrimaryManifestUri,
                EmptyToNull(Settings.SecondaryManifestPath),
                EmptyToNull(Settings.OverridePath),
                CreateProgress(),
                token);
            ApplyCacheMetadata(update.Generation.Metadata);
            ProgressText = update.Status == LudusaviManifestUpdateStatus.NotModified
                ? I18n.GetString("GameDiscovery_Status_NotModified")
                : I18n.GetString("GameDiscovery_Status_Downloaded");
            await using var generation = update.Generation;
            token.ThrowIfCancellationRequested();
            await ScanPreparedCoreAsync(token, generation);
        });
    }

    public async Task ImportAndScanAsync(string manifestPath)
    {
        await RunOperationAsync(async token =>
        {
            var update = await _cacheService.ImportGenerationAsync(
                manifestPath,
                EmptyToNull(Settings.SecondaryManifestPath),
                EmptyToNull(Settings.OverridePath),
                CreateProgress(),
                token);
            ApplyCacheMetadata(update.Generation.Metadata);
            await using var generation = update.Generation;
            token.ThrowIfCancellationRequested();
            await ScanPreparedCoreAsync(token, generation);
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

    public async Task<ConfigSaveResult> SaveSettingsAsync()
    {
        if (IsBusy) return new() { ErrorMessage = I18n.GetString("GameDiscovery_OperationRunning") };
        ReturnToResults();
        IsBusy = true;
        try
        {
            var previous = ConfigService.CurrentConfig.GlobalSettings.GameDiscovery;
            var updated = CloneSettings(Settings);
            updated.PluginRoots = previous.PluginRoots.ToDictionary(pair => pair.Key,
                pair => new List<string>(pair.Value ?? new List<string>()), StringComparer.OrdinalIgnoreCase);
            await ConfigEditTransaction.ApplyAsync(
                () => ConfigService.CurrentConfig.GlobalSettings.GameDiscovery = updated,
                () => ConfigService.CurrentConfig.GlobalSettings.GameDiscovery = previous,
                () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"));
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            return new() { ErrorMessage = ex is ConfigEditRollbackException
                ? I18n.Format("Config_CompensationFailed", ex.Message) : ex.Message, Exception = ex };
        }
        finally { IsBusy = false; }
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
        _draftRevision = _selectionRevision;
        NotifyReview();
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
        if (_draftRevision != _selectionRevision || Drafts.Count == 0 || Drafts.All(item => !item.IsSelected))
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
            NotifyReview();
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

    public bool ReturnDraftToSetup { get; private set; }
    public GameDiscoveryNavigationParameter? SetupReentry { get; private set; }
    private BackupConfig PrepareSetupDraft(BackupConfigDraft draft)
    {
        var config = DiscoveryDraftService.PrepareNewSetupDraft(draft);
        var saved = SetupReentry?.ResumingSelections.SingleOrDefault(s => s.Identity.HasSameStableIdentity(config.DiscoveryOrigin?.Identity));
        if (saved is not null)
        {
            config.Name = saved.Name; config.DestinationPath = saved.DestinationPath;
            config.IconGlyph = saved.IconGlyph; config.Kind = new() { OwnerId = saved.Kind.OwnerId, KindId = saved.Kind.KindId };
            config.IsEncrypted = saved.IsEncrypted;
        }
        return config;
    }
    public IReadOnlyList<BackupConfig> TakeSelectedSetupDrafts()
    {
        if (!IsPluginBatchMode)
        {
            if (_draftRevision != _selectionRevision) throw new InvalidOperationException(I18n.GetString("GameDiscovery_SelectDraft"));
            var drafts = Drafts.Where(item => item.IsSelected).ToArray();
            if (drafts.Length == 0) throw new InvalidOperationException(I18n.GetString("GameDiscovery_SelectDraft"));
            foreach (var item in drafts)
            {
                item.Draft.ProposedConfig.Name = item.ConfigName.Trim();
                item.Draft.ProposedConfig.DestinationPath = item.DestinationPath.Trim();
            }
            return drafts.Select(item => PrepareSetupDraft(item.Draft)).ToArray();
        }
        var selected = PluginBatchItems.Where(i => i.IsSelected).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException(I18n.GetString("GameDiscovery_SelectDraft"));
        return selected.Select(item => PrepareSetupDraft(item.Draft)).ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sessionCts.Cancel();
        _settings.PropertyChanged -= OnSettingsPropertyChanged;
        _operationCts?.Cancel();
        foreach (var sink in _progressSinks) sink.Detach();
        _progressSinks.Clear();
        SelectedGame = null;
        ClearGames();
        VisibleGames.Clear();
        Drafts.Clear();
        ResetPluginBatchState();
        SetupReentry = null;
        _targetedPreset = null;
        ReleaseCompletedResources();
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasDrafts));
    }

    private void ReleaseCompletedResources()
    {
        if (!_disposed || IsBusy || _initializing != 0 || _resourcesDisposed) return;
        _resourcesDisposed = true;
        _httpClient.Dispose();
        _sessionCts.Dispose();
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.PropertyName))
            OnPropertyChanged(e.PropertyName);
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (IsBusy || !IsSessionActive) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
        _operationCts = request;
        ReturnToResults();
        IsBusy = true;
        try
        {
            await operation(request.Token);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            if (IsSessionActive) ProgressText = I18n.GetString("GameDiscovery_Status_Cancelled");
        }
        catch (Exception ex)
        {
            if (IsSessionActive) ProgressText = I18n.Format("GameDiscovery_Status_Failed", ex.Message);
            LogService.LogError($"Game discovery failed: {ex.Message}", "GameDiscovery", ex);
        }
        finally
        {
            foreach (var sink in _progressSinks) sink.Detach();
            _progressSinks.Clear();
            if (ReferenceEquals(_operationCts, request)) _operationCts = null;
            IsBusy = false;
            ReleaseCompletedResources();
        }
    }

    private async Task ScanCoreAsync(CancellationToken token)
    {
        await using var current = await _cacheService.PrepareGenerationAsync(
            EmptyToNull(Settings.SecondaryManifestPath),
            EmptyToNull(Settings.OverridePath),
            CreateProgress(),
            token);
        await ScanPreparedCoreAsync(token, current);
    }

    private async Task ScanPreparedCoreAsync(CancellationToken token, LudusaviGeneration? current)
    {
        token.ThrowIfCancellationRequested();
        if (current == null)
        {
            HasCache = false;
            CacheStatus = I18n.GetString("GameDiscovery_Cache_Missing");
            ProgressText = I18n.GetString("GameDiscovery_Status_NoCache");
        }
        else
        {
            ApplyCacheMetadata(current.Metadata);
        }
        var composition = GameDiscoveryProviderFactory.Create(_cacheService, current);
        var discoveryService = new GameDiscoveryService(composition.Providers, composition.Diagnostics);
        var stopwatch = Stopwatch.StartNew();
        var result = await discoveryService.DiscoverAsync(BuildRequest(), CreateProgress(), token,
            OnboardingOperationBudgets.Discovery, OnboardingOperationBudgets.DiscoveryCandidates);
        token.ThrowIfCancellationRequested();
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
        token.ThrowIfCancellationRequested();
        ApplyResult(result, stopwatch, preset);
    }

    public async Task ScanPluginKnownLocationsAsync()
    {
        if (!IsPluginBatchMode || IsBusy) return;
        SetPluginBatchScope(null);
        await RunOperationAsync(ScanPluginBatchCoreAsync);
    }

    public async Task ScanPluginUserRootAsync(string root)
    {
        if (!IsPluginBatchMode || IsBusy || string.IsNullOrWhiteSpace(root)) return;
        if (!Directory.Exists(root))
        {
            PluginBatchFatalMessage = I18n.GetString("GameDiscovery_PluginBatch_InvalidRequest");
            return;
        }
        SetPluginBatchScope(root.Trim());
        await RunOperationAsync(ScanPluginBatchCoreAsync);
    }

    private void SetPluginBatchScope(string? root)
    {
        _pluginBatchIncludeKnownLocations = root == null;
        _pluginBatchRoot = root ?? string.Empty;
        OnPropertyChanged(nameof(PluginBatchRoot));
        if (ReturnDraftToSetup)
            SetupReentry = new GameDiscoveryNavigationParameter
            {
                Mode = GameDiscoveryNavigationMode.PluginBatch,
                PluginId = _pluginBatchPluginId,
                ConfigKind = _pluginBatchKind,
                UserRoot = _pluginBatchRoot,
                IncludeKnownLocations = _pluginBatchIncludeKnownLocations,
                ReturnDraftToSetup = true,
                ResumingSelections = SetupReentry?.ResumingSelections ?? []
            };
    }

    private async Task ScanPluginBatchCoreAsync(CancellationToken token)
    {

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
                Mode = _pluginBatchIncludeKnownLocations ? DiscoveryRequestMode.FullMachine : DiscoveryRequestMode.UserRoots,
                UserRoots = _pluginBatchIncludeKnownLocations ? [] : [_pluginBatchRoot]
            },
            CreateProgress(),
            token, OnboardingOperationBudgets.Discovery, OnboardingOperationBudgets.DiscoveryCandidates);
        token.ThrowIfCancellationRequested();

        _pluginBatchPlan = PluginBatchCreationPlanner.Build(
            result,
            _pluginBatchPluginId,
            _pluginBatchKind,
            BackupPresetService.GetTemplates(),
            ConfigService.CurrentConfig.BackupConfigs);
        foreach (var item in _pluginBatchPlan.Items)
        {
            if (ReturnDraftToSetup) item.IsSelected = false;
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
        if (!IsSessionActive) return;
        ClearGames();
        foreach (var game in result.Candidates.OrderBy(candidate => candidate.Definition.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new GameDiscoveryCandidateItem(
                game,
                lockedPreset == null
                    ? BackupPresetDiscoveryMatcher.FindMatches(game.Definition, BackupPresetService.GetTemplates())
                    : [lockedPreset],
                ConfigService.CurrentConfig.BackupConfigs,
                lockedPreset);
            if (ReturnDraftToSetup)
                foreach (var set in item.BackupSets)
                    foreach (var resource in set.Resources) resource.IsSelected = false;
            item.PropertyChanged += OnCandidateChanged;
            Games.Add(item);
        }
        NotifyReview();
        NotifySelectionSummary();
        RefreshVisibleGames();
        ResultSummary = I18n.Format(
            "GameDiscovery_ResultSummary",
            Games.Count,
            Games.Sum(game => game.BackupSets.Sum(set => set.Resources.Count)),
            result.Diagnostics.Count(diagnostic => diagnostic.Severity == DiscoveryDiagnosticSeverity.Warning),
            stopwatch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.CurrentCulture));
        var diagnostic = result.Diagnostics.FirstOrDefault(value => value.Severity == DiscoveryDiagnosticSeverity.Error)
            ?? result.Diagnostics.FirstOrDefault(value => value.Code is "discovery-time-budget" or "discovery-result-budget");
        ProgressText = diagnostic?.Code switch
        {
            "discovery-time-budget" => I18n.GetString("Setup_DiscoveryTimeout"),
            "discovery-result-budget" => I18n.GetString("Setup_DiscoveryLimit"),
            _ => diagnostic?.Message ?? I18n.GetString("GameDiscovery_Status_Complete")
        };
        LogService.LogInfo(
            $"Discovery games={Games.Count}, resources={Games.Sum(game => game.BackupSets.Sum(set => set.Resources.Count))}, diagnostics={result.Diagnostics.Count}",
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
        var sink = new SessionProgress(this, _operationCts, SynchronizationContext.Current);
        _progressSinks.Add(sink);
        return sink;
    }

    private sealed class SessionProgress : IProgress<DiscoveryProgress>
    {
        private WeakReference<GameDiscoveryPageViewModel>? _owner;
        private readonly CancellationTokenSource? _request;
        private readonly SynchronizationContext? _context;
        private long _lastUpdate;
        public SessionProgress(GameDiscoveryPageViewModel owner, CancellationTokenSource? request, SynchronizationContext? context)
        { _owner = new(owner); _request = request; _context = context; }
        public void Detach() => Interlocked.Exchange(ref _owner, null);
        public void Report(DiscoveryProgress progress)
        {
            if (_owner is null) return;
            var now = Environment.TickCount64;
            var previous = Interlocked.Read(ref _lastUpdate);
            if (now - previous < 100 || Interlocked.CompareExchange(ref _lastUpdate, now, previous) != previous) return;
            if (_context is null) Publish(progress);
            else _context.Post(_ => Publish(progress), null);
        }
        private void Publish(DiscoveryProgress progress)
        {
            var reference = Volatile.Read(ref _owner);
            if (reference is null || !reference.TryGetTarget(out var owner) || !owner.IsSessionActive
                || !owner.IsBusy || !ReferenceEquals(_request, owner._operationCts)) return;
            var total = progress.Total is > 0 ? $" ({progress.Completed}/{progress.Total})" : string.Empty;
            owner.ProgressText = $"{progress.Message}{total}";
        }
    }

    private async Task RefreshCacheStatusAsync(CancellationToken token)
    {
        try
        {
            var current = await _cacheService.ReadStatusAsync(token);
            if (!IsSessionActive) return;
            if (current == null)
            {
                HasCache = false;
                CacheStatus = I18n.GetString("GameDiscovery_Cache_Missing");
                return;
            }
            ApplyCacheMetadata(current);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsSessionActive) return;
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
            "GameDiscovery_Cache_Present",
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
        ClearGames();
        VisibleGames.Clear();
        Drafts.Clear();
        PluginBatchItems.Clear();
        SelectedGame = null;
        _pluginBatchPluginId = string.Empty;
        _pluginBatchPluginName = string.Empty;
        _pluginBatchKindName = string.Empty;
        _pluginBatchRoot = string.Empty;
        _pluginBatchIncludeKnownLocations = false;
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
        PluginRoots = (source?.PluginRoots ?? new Dictionary<string, List<string>>())
            .ToDictionary(pair => pair.Key, pair => new List<string>(pair.Value ?? new List<string>()),
                StringComparer.OrdinalIgnoreCase),
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

public sealed class GameDiscoveryCandidateItem : FolderRewind.Models.ObservableObject, IDisposable
{
    private DiscoveryCandidateStatus _status;
    private bool _changingSelection;
    public GameDiscoveryCandidateItem(DiscoveredGameCandidate candidate, IEnumerable<BackupPreset> presets,
        IEnumerable<BackupConfig> existingConfigs, BackupPreset? lockedPreset = null)
    {
        Candidate = candidate;
        var configs = existingConfigs.ToList();
        _status = DiscoveryPresentationService.GetStatus(candidate, configs.Select(config => config.DiscoveryOrigin));
        var available = presets.ToList();
        foreach (var set in candidate.BackupSets)
        {
            var standard = set.PluginDraftContext is { } context
                ? BackupPresetService.CreateStandardPluginPreset(context.PluginId, context.Kind)
                : BackupPresetService.CreateStandardGamePreset();
            var options = (lockedPreset is null ? new[] { standard }.Concat(available) : available)
                .Where(preset => string.Equals(preset.Kind.OwnerId, standard.Kind.OwnerId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(preset.Kind.KindId, standard.Kind.KindId, StringComparison.Ordinal))
                .GroupBy(preset => preset.ShareId, StringComparer.OrdinalIgnoreCase)
                .Select(group => new GameDiscoveryPresetItem(group.First())).ToList();
            var item = new GameDiscoveryBackupSetItem(set, options,
                DiscoverySetIdentityMatcher.FindUnique(set.Identity, configs, config => config.DiscoveryOrigin), lockedPreset is not null);
            item.PropertyChanged += OnSetChanged;
            BackupSets.Add(item);
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
    public string InstallationSummary => string.Join(Environment.NewLine, Candidate.Installations.Select(item => $"{item.Store}: {item.BasePath}"));
    public string Notes => string.Join(Environment.NewLine, Candidate.Definition.Notes);
    public string NativeCloud => string.Join(Environment.NewLine, Candidate.Definition.NativeCloud.Select(item => $"{item.Key}: {item.Value}"));
    public ObservableCollection<GameDiscoveryBackupSetItem> BackupSets { get; } = new();
    public bool CanSelect => BackupSets.Any(set => set.CanSelect);
    public bool? SelectionState => GameDiscoverySelection.State(BackupSets.SelectMany(set => set.Resources));
    public bool IsSelected
    {
        get => BackupSets.Any(set => set.IsSelected);
        set
        {
            _changingSelection = true;
            try { foreach (var set in BackupSets) set.IsSelected = value; }
            finally { _changingSelection = false; NotifySelection(); }
        }
    }
    public void ToggleSelection() => IsSelected = SelectionState != true;
    private void NotifySelection() { OnPropertyChanged(nameof(IsSelected)); OnPropertyChanged(nameof(SelectionState)); }
    private void OnSetChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_changingSelection) return;
        if (args.PropertyName == nameof(GameDiscoveryBackupSetItem.SelectionState)) NotifySelection();
        if (args.PropertyName == nameof(GameDiscoveryBackupSetItem.SelectedPreset)) OnPropertyChanged(nameof(GameDiscoveryBackupSetItem.SelectedPreset));
    }
    public DiscoveryCandidateStatus Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); } }
    public void RefreshStatus(IEnumerable<DiscoveryOrigin?> origins) => Status = DiscoveryPresentationService.GetStatus(Candidate, origins);
    public void Dispose() { foreach (var set in BackupSets) { set.PropertyChanged -= OnSetChanged; set.Dispose(); } }
}

internal static class GameDiscoverySelection
{
    public static bool? State(IEnumerable<GameDiscoveryResourceItem> resources)
    {
        var eligible = resources.Where(resource => resource.CanSelect).ToList();
        var selected = eligible.Count(resource => resource.IsSelected);
        return selected == 0 ? false : selected == eligible.Count ? true : null;
    }
}

public sealed class GameDiscoveryBackupSetItem : FolderRewind.Models.ObservableObject, IDisposable
{
    private GameDiscoveryPresetItem? _selectedPreset;
    private readonly bool _isPresetLocked;
    private bool _changingSelection;
    public GameDiscoveryBackupSetItem(BackupSetCandidate candidate, IReadOnlyList<GameDiscoveryPresetItem> presets,
        BackupConfig? existingConfig, bool isPresetLocked = false)
    {
        Candidate = candidate;
        ExistingConfig = existingConfig;
        _isPresetLocked = isPresetLocked;
        foreach (var resource in candidate.Resources)
        {
            var item = new GameDiscoveryResourceItem(resource);
            item.PropertyChanged += OnResourceChanged;
            Resources.Add(item);
        }
        foreach (var preset in presets) Presets.Add(preset);
        SelectedPreset = HasExistingConfiguration ? null : Presets.Count(item => item.Preset.IsRecommended) == 1
            ? Presets.Single(item => item.Preset.IsRecommended) : Presets.FirstOrDefault();
        var kind = existingConfig?.Kind ?? candidate.PluginDraftContext?.Kind ?? SelectedPreset?.Preset.Kind ?? new ConfigKindReference();
        var option = PluginService.GetAllSupportedConfigKinds(includeEncrypted: true).FirstOrDefault(item =>
            string.Equals(item.Kind.OwnerId, kind.OwnerId, StringComparison.OrdinalIgnoreCase) && item.Kind.KindId == kind.KindId);
        KindText = I18n.Format("GameDiscovery_ConfigType", option?.DisplayName ?? kind.KindId);
        var pluginId = candidate.PluginDraftContext?.PluginId ?? existingConfig?.RequiredPluginId ?? option?.RequiredPluginId;
        var pluginName = PluginService.InstalledPlugins.FirstOrDefault(item => item.Id == pluginId)?.Name;
        PluginText = I18n.Format("GameDiscovery_ManagedBy", string.IsNullOrEmpty(pluginId) ? "FolderRewind" : pluginName ?? pluginId);
    }
    public BackupSetCandidate Candidate { get; }
    public BackupConfig? ExistingConfig { get; }
    public bool HasExistingConfiguration => ExistingConfig is not null;
    public bool CanChoosePreset => !HasExistingConfiguration && !_isPresetLocked && Presets.Count > 1;
    public bool HasPresetPicker => CanChoosePreset;
    public string SettingsSummary => HasExistingConfiguration
        ? I18n.Format("GameDiscovery_UseExisting", ExistingConfig!.Name)
        : I18n.Format("GameDiscovery_BackupSettings", SelectedPreset?.Name ?? I18n.GetString("GameDiscovery_NoCompatiblePreset"));
    public string KindText { get; }
    public string PluginText { get; }
    public string Name => Candidate.DisplayName;
    public ObservableCollection<GameDiscoveryResourceItem> Resources { get; } = new();
    public ObservableCollection<GameDiscoveryPresetItem> Presets { get; } = new();
    public bool CanSelect => Resources.Any(resource => resource.CanSelect);
    public int SelectedResourceCount => Resources.Count(resource => resource.CanSelect && resource.IsSelected);
    public string SelectionSummary => I18n.Format("GameDiscovery_SetSelection", SelectedResourceCount, Resources.Count(resource => resource.CanSelect));
    public bool? SelectionState => GameDiscoverySelection.State(Resources);
    public bool IsSelected
    {
        get => SelectedResourceCount > 0;
        set
        {
            _changingSelection = true;
            try { foreach (var resource in Resources.Where(resource => resource.CanSelect)) resource.IsSelected = value; }
            finally { _changingSelection = false; NotifySelection(); }
        }
    }
    public void ToggleSelection() => IsSelected = SelectionState != true;
    public GameDiscoveryPresetItem? SelectedPreset
    {
        get => _selectedPreset;
        set { if (SetProperty(ref _selectedPreset, value)) OnPropertyChanged(nameof(SettingsSummary)); }
    }
    private void OnResourceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    { if (!_changingSelection && args.PropertyName == nameof(GameDiscoveryResourceItem.IsSelected)) NotifySelection(); }
    private void NotifySelection()
    {
        OnPropertyChanged(nameof(IsSelected)); OnPropertyChanged(nameof(SelectionState));
        OnPropertyChanged(nameof(SelectedResourceCount)); OnPropertyChanged(nameof(SelectionSummary));
    }
    public void Dispose() { foreach (var resource in Resources) resource.PropertyChanged -= OnResourceChanged; }
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
    public string Name => Preset.IsBuiltIn && Preset.ShareId.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase)
        ? I18n.GetString("GameDiscovery_DefaultSettings")
        : Preset.IsRecommended
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

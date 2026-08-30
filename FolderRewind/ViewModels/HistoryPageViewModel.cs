using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.History.Legacy;
using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI;

namespace FolderRewind.ViewModels;

public sealed class HistoryPageViewModel : ViewModelBase
{
    private readonly List<NativeHistoryVersionViewItem> _allVersions = [];
    private readonly List<BackupRunViewItem> _allRuns = [];
    private IDisposable? _changeSubscription;
    private BackupConfig? _currentConfig;
    private ManagedFolder? _currentFolder;
    private bool _isEmpty = true;
    private int _missingCount;
    private string _commentFilterText = string.Empty;
    private HistoryViewMode _viewMode = HistoryViewMode.PerSource;
    private BranchViewItem? _selectedBranch;
    private bool _refreshingBranches;

    public ObservableCollection<NativeHistoryVersionViewItem> FilteredHistory { get; } = [];
    public ObservableCollection<BackupRunViewItem> FilteredRuns { get; } = [];
    public ObservableCollection<BranchViewItem> Branches { get; } = [];
    public ObservableCollection<BackupConfig> Configs => ConfigService.CurrentConfig?.BackupConfigs ?? [];
    private GlobalSettings? Settings => ConfigService.CurrentConfig?.GlobalSettings;
    public bool IsEmpty { get => _isEmpty; private set => SetProperty(ref _isEmpty, value); }
    public bool HasMissing => _missingCount > 0;
    public bool IsGroupedRunView => _viewMode == HistoryViewMode.ByRun;
    public bool ShowGroupedRunHistory => IsGroupedRunView;
    public bool ShowPerSourceHistory => !IsGroupedRunView;
    public bool CanUsePerSourceActions => !IsGroupedRunView && _currentFolder is not null;
    public bool CanUseCloudHistoryActions => _currentConfig is not null
        && CloudSyncService.CanUseHistoryCloudActions(_currentConfig);
    public bool CanOpenConfigCloudSync => CanUseCloudHistoryActions;
    public BranchViewItem? SelectedBranch
    {
        get => _selectedBranch;
        set
        {
            if (!SetProperty(ref _selectedBranch, value)) return;
            NotifyBranchSelectionChanged();
            if (!_refreshingBranches) ApplyFilter();
        }
    }
    public string CurrentBranchDisplay
    {
        get
        {
            var active = Branches.FirstOrDefault(branch => branch.IsActive);
            return active is null
                ? I18n.GetString("History_Branch_CurrentUnavailable")
                : I18n.Format("History_Branch_CurrentFormat", active.Name);
        }
    }
    public bool CanCheckoutSelectedBranch => SelectedBranch is { CanCheckout: true, IsActive: false };
    public bool CanRenameSelectedBranch => SelectedBranch?.CanRename == true;
    public bool CanDeleteSelectedBranch => SelectedBranch?.CanDelete == true;

    public string CommentFilterText
    {
        get => _commentFilterText;
        set { if (SetProperty(ref _commentFilterText, value ?? string.Empty)) ApplyFilter(); }
    }

    public bool UseHistoryStatusColors
    {
        get => Settings?.UseHistoryStatusColors ?? true;
        set
        {
            if (Settings is not null) { Settings.UseHistoryStatusColors = value; ConfigService.Save(); }
            UpdateTimelineVisuals(FilteredHistory); OnPropertyChanged();
        }
    }

    public void Initialize() { _viewMode = Settings?.LastHistoryViewMode ?? HistoryViewMode.PerSource; NotifyViewModeChanged(); }

    public void SetCurrentSelection(BackupConfig? config, ManagedFolder? folder, bool refreshHistoryIfFolder, bool persistSelection)
    {
        if (!string.Equals(_currentConfig?.Id, config?.Id, StringComparison.OrdinalIgnoreCase)) SelectedBranch = null;
        _currentConfig = config; _currentFolder = folder; Subscribe(config); NotifyContextChanged();
        if (config is not null && (IsGroupedRunView || (refreshHistoryIfFolder && folder is not null))) RefreshCurrentHistory();
        if (persistSelection) PersistSelection(config, folder);
    }

    public void ClearCurrentSelection()
    {
        _currentConfig = null;
        _currentFolder = null;
        Subscribe(null);
        _allVersions.Clear();
        _allRuns.Clear();
        FilteredHistory.Clear();
        FilteredRuns.Clear();
        Branches.Clear();
        SelectedBranch = null;
        _missingCount = 0;
        IsEmpty = true;
        OnPropertyChanged(nameof(HasMissing));
        OnPropertyChanged(nameof(CurrentBranchDisplay));
        NotifyContextChanged();
    }

    public bool TryGetCurrentSelection(out BackupConfig? config, out ManagedFolder? folder)
    { config = _currentConfig; folder = _currentFolder; return config is not null && folder is not null; }
    public bool TryGetCurrentConfig(out BackupConfig? config) { config = _currentConfig; return config is not null; }

    public bool TryResolveSelection(string? configId, string? folderPath, out BackupConfig? config, out ManagedFolder? folder)
    {
        config = !string.IsNullOrWhiteSpace(configId) ? Configs.FirstOrDefault(item => item.Id == configId) : null;
        config ??= !string.IsNullOrWhiteSpace(folderPath) ? Configs.FirstOrDefault(item => item.SourceFolders.Any(source => source.Path == folderPath)) : null;
        folder = config?.SourceFolders.FirstOrDefault(item => item.Path == folderPath); return config is not null;
    }

    public bool TryResolveLastSelection(out BackupConfig? config, out ManagedFolder? folder)
    {
        config = Configs.FirstOrDefault(item => item.Id == Settings?.LastHistoryConfigId) ?? Configs.FirstOrDefault();
        folder = config?.SourceFolders.FirstOrDefault(item => item.Path == Settings?.LastHistoryFolderPath) ?? config?.SourceFolders.FirstOrDefault();
        return config is not null;
    }

    public void RefreshCurrentHistory()
    {
        var selectedBranchId = SelectedBranch?.BranchId;
        _allVersions.Clear(); _allRuns.Clear(); FilteredHistory.Clear(); FilteredRuns.Clear(); Branches.Clear();
        if (_currentConfig is null) { IsEmpty = true; return; }
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(_currentConfig.Id);
        SourceId? sourceId = _currentFolder is not null && Guid.TryParse(_currentFolder.Id, out var id) && id != Guid.Empty ? new SourceId(id) : null;
        var snapshot = new HistoryPresentationQueryService(runtime).QueryAsync(sourceId).ConfigureAwait(false).GetAwaiter().GetResult();
        var branchNames = snapshot.Branches.ToDictionary(branch => branch.BranchId, branch => branch.Name);
        _allVersions.AddRange(snapshot.Timeline.Select(item => new NativeHistoryVersionViewItem(item, branchNames)));
        _allRuns.AddRange(snapshot.Runs.Select(item => new BackupRunViewItem(item)));
        _refreshingBranches = true;
        try
        {
            foreach (var branch in snapshot.Branches) Branches.Add(new BranchViewItem(branch));
            SelectedBranch = Branches.FirstOrDefault(branch => branch.BranchId == selectedBranchId)
                ?? Branches.FirstOrDefault(branch => branch.IsActive)
                ?? Branches.FirstOrDefault(branch => !branch.IsDeleted);
        }
        finally
        {
            _refreshingBranches = false;
        }
        OnPropertyChanged(nameof(CurrentBranchDisplay));
        NotifyBranchSelectionChanged();
        ApplyFilter();
    }

    public void SetHistoryViewMode(HistoryViewMode mode)
    {
        _viewMode = mode; if (Settings is not null) { Settings.LastHistoryViewMode = mode; ConfigService.Save(); }
        NotifyViewModeChanged(); RefreshCurrentHistory();
    }

    public int GetMissingCount() => _missingCount;
    public async Task<int> ClearMissingEntriesAsync()
    {
        if (_currentConfig is null)
            return 0;
        return await new HistoryLocalReplicaMaintenanceService(
            NativeHistoryCoreGateway.GetRequiredRuntime(_currentConfig.Id))
            .RemoveMissingControlledReplicasAsync()
            .ConfigureAwait(false);
    }
    public async Task<int> ScanAndRecoverHistoryAsync(string scanPath)
    {
        if (_currentConfig is null
            || _currentFolder is null
            || !Guid.TryParse(_currentFolder.Id, out var sourceGuid)
            || sourceGuid == Guid.Empty
            || string.IsNullOrWhiteSpace(scanPath)
            || !Directory.Exists(scanPath))
        {
            return 0;
        }

        var sourceId = new SourceId(sourceGuid);
        var displayName = string.IsNullOrWhiteSpace(_currentFolder.DisplayName)
            ? Path.GetFileName(_currentFolder.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : _currentFolder.DisplayName.Trim();
        var descriptor = new SourceDescriptorSnapshot(displayName, _currentFolder.Path);
        var recovery = new HistoryArchiveRecoveryService(
            NativeHistoryCoreGateway.GetRequiredRuntime(_currentConfig.Id));
        var recovered = 0;
        foreach (var path in Directory.EnumerateFiles(scanPath, "*.*", SearchOption.TopDirectoryOnly)
                     .OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            if (!LegacyArchiveNameParser.TryParse(Path.GetFileName(path), out var parsed)
                || !string.Equals(parsed!.SourceDisplayName, displayName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var result = await recovery.RecoverAsync(
                sourceId,
                descriptor,
                path,
                overlay: string.Equals(parsed.BackupType, "Smart", StringComparison.OrdinalIgnoreCase))
                .ConfigureAwait(false);
            if (result.Created)
            {
                recovered++;
            }
        }
        return recovered;
    }
    public string? GetBackupFilePath(NativeHistoryVersionViewItem item) => item.LocalPath;
    public bool TryRevealBackupFile(NativeHistoryVersionViewItem item, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(item.LocalPath) || !File.Exists(item.LocalPath)) { errorMessage = I18n.GetString("History_ViewFile_NotFound"); return false; }
        return ShellPathService.TryRevealPathInExplorer(item.LocalPath, out errorMessage);
    }

    public void UpdateComment(NativeHistoryVersionViewItem item, string comment)
    {
        if (_currentConfig is null) return;
        NativeHistoryCoreGateway.GetRequiredRuntime(_currentConfig.Id).Annotations.SetCommentAsync(
            new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, item.VersionId.Value), comment).ConfigureAwait(false).GetAwaiter().GetResult();
        RefreshCurrentHistory();
    }

    public void ToggleImportant(NativeHistoryVersionViewItem item)
    {
        if (_currentConfig is null) return;
        NativeHistoryCoreGateway.GetRequiredRuntime(_currentConfig.Id).Annotations.SetPinAsync(
            new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, item.VersionId.Value), !item.IsImportant).ConfigureAwait(false).GetAwaiter().GetResult();
        RefreshCurrentHistory();
    }

    public void UpdateRunComment(BackupRunViewItem item, string comment)
    {
        if (_currentConfig is null) return;
        NativeHistoryCoreGateway.GetRequiredRuntime(_currentConfig.Id).Annotations.SetCommentAsync(
            new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Run, item.RunId.Value), comment).ConfigureAwait(false).GetAwaiter().GetResult();
        RefreshCurrentHistory();
    }

    public void ToggleRunImportant(BackupRunViewItem item)
    {
        if (_currentConfig is null) return;
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(_currentConfig.Id);
        runtime.Annotations.SetRunImportantAsync(new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Run, item.RunId.Value), !item.IsImportant)
            .ConfigureAwait(false).GetAwaiter().GetResult();
        if (item.ResultCheckpointId is { } checkpointId)
            runtime.Annotations.SetPinAsync(new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Checkpoint, checkpointId.Value), !item.IsImportant)
                .ConfigureAwait(false).GetAwaiter().GetResult();
        RefreshCurrentHistory();
    }

    public async Task<HistoryRestoreResult?> RestoreRunAsync(BackupRunViewItem item, BackupService.RestoreMode mode)
    {
        if (_currentConfig is null || item.ResultCheckpointId is not { } checkpointId) return null;
        return await NativeHistoryApplicationService.RestoreCheckpointAsync(
            _currentConfig,
            checkpointId,
            completeCheckpoint: !item.HasPartialBackup,
            mode).ConfigureAwait(false);
    }
    public async Task<bool> DeleteRunAsync(BackupRunViewItem item)
    {
        if (_currentConfig is null) return false;
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(_currentConfig.Id);
        await runtime.Annotations.SetSuppressionAsync(
            new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Run, item.RunId.Value),
            suppressed: true).ConfigureAwait(false);
        return true;
    }
    public async Task<bool> UploadToCloudAsync(NativeHistoryVersionViewItem item)
    {
        if (_currentConfig is null
            || item.RepresentationId is not { } representationId
            || string.IsNullOrWhiteSpace(item.LocalPath)) return false;
        return await CloudSyncService.UploadRepresentationAsync(
            _currentConfig,
            representationId,
            item.LocalPath).ConfigureAwait(false);
    }
    public async Task<bool> DownloadFromCloudAsync(NativeHistoryVersionViewItem item)
    {
        if (_currentConfig is null
            || _currentFolder is null
            || item.RepresentationId is not { } representationId) return false;
        return await CloudSyncService.DownloadRepresentationAsync(
            _currentConfig,
            _currentFolder,
            representationId,
            item.FileName).ConfigureAwait(false);
    }
    public async Task<HistoryRestoreResult?> RestoreVersionAsync(
        NativeHistoryVersionViewItem item,
        BackupService.RestoreMode mode)
    {
        if (_currentConfig is null || _currentFolder is null) return null;
        return await NativeHistoryApplicationService.RestoreVersionAsync(
            _currentConfig,
            _currentFolder,
            item.VersionId,
            mode).ConfigureAwait(false);
    }
    public async Task<BackupService.DeleteBackupResult> DeleteVersionAsync(NativeHistoryVersionViewItem item, BackupDeleteMode mode)
    {
        if (_currentConfig is null)
            return new() { Success = false, Message = "No active configuration is selected." };
        try
        {
            var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(_currentConfig.Id);
            if (mode == BackupDeleteMode.LocalArchiveAndRecord)
            {
                await NativeHistoryApplicationService.ReleaseVersionAsync(_currentConfig, item.VersionId)
                    .ConfigureAwait(false);
            }
            await runtime.Annotations.SetSuppressionAsync(
                new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, item.VersionId.Value),
                suppressed: true).ConfigureAwait(false);
            var archiveDeleted = mode == BackupDeleteMode.LocalArchiveAndRecord
                && (string.IsNullOrWhiteSpace(item.LocalPath) || !File.Exists(item.LocalPath));
            return new()
            {
                Success = true,
                ArchiveDeleted = archiveDeleted,
                HistoryUpdated = true
            };
        }
        catch (Exception ex)
        {
            return new() { Success = false, Message = ex.Message };
        }
    }

    public async Task<bool> CreateBranchAsync(CheckpointId checkpointId, string name)
    {
        if (_currentConfig is null) return false;
        await using var operationLease = await NativeHistoryConfigurationOperationGate
            .EnterAsync(_currentConfig.Id).ConfigureAwait(false);
        await NativeHistoryCoreGateway.GetRequiredRuntime(_currentConfig.Id).Branches
            .CreateFromCheckpointAsync(checkpointId, name).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> CreateBranchAtLatestCheckpointAsync(string name)
    {
        if (_currentConfig is null) return false;
        await using var operationLease = await NativeHistoryConfigurationOperationGate
            .EnterAsync(_currentConfig.Id).ConfigureAwait(false);
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(_currentConfig.Id);
        var checkpoint = (await runtime.Query.GetAllCheckpointsAsync().ConfigureAwait(false))
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.CheckpointId.ToString(), StringComparer.Ordinal)
            .FirstOrDefault();
        if (checkpoint is null) return false;
        await runtime.Branches.CreateFromCheckpointAsync(checkpoint.CheckpointId, name).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RenameBranchAsync(BranchViewItem branch, string name)
    {
        if (_currentConfig is null || !branch.CanRename) return false;
        await using var operationLease = await NativeHistoryConfigurationOperationGate
            .EnterAsync(_currentConfig.Id).ConfigureAwait(false);
        await NativeHistoryCoreGateway.GetRequiredRuntime(_currentConfig.Id).Branches
            .RenameAsync(branch.BranchId, name).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DeleteBranchAsync(BranchViewItem branch)
    {
        if (_currentConfig is null || !branch.CanDelete) return false;
        await using var operationLease = await NativeHistoryConfigurationOperationGate
            .EnterAsync(_currentConfig.Id).ConfigureAwait(false);
        await NativeHistoryCoreGateway.GetRequiredRuntime(_currentConfig.Id).Branches
            .DeleteAsync(branch.BranchId).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> CheckoutBranchTipAsync(BranchViewItem branch, BranchUpdateId selectedTipId)
    {
        if (_currentConfig is null
            || !branch.CanCheckout
            || branch.IsMultiTip && branch.Tips.All(item => item.UpdateId != selectedTipId))
            return false;
        var result = await NativeHistoryApplicationService.CheckoutAsync(_currentConfig, selectedTipId)
            .ConfigureAwait(false);
        return result.Succeeded;
    }

    private void ApplyFilter()
    {
        var needle = CommentFilterText.Trim();
        var branchId = SelectedBranch?.BranchId;
        FilteredHistory.Clear(); FilteredRuns.Clear();
        if (IsGroupedRunView)
        {
            foreach (var item in _allRuns.Where(item => (branchId is null || item.BranchIds.Contains(branchId.Value))
                         && (needle.Length == 0 
                             || item.Comment.Contains(needle, StringComparison.OrdinalIgnoreCase)
                             || item.Message.Contains(needle, StringComparison.OrdinalIgnoreCase))))
                FilteredRuns.Add(item);
            _missingCount = 0; IsEmpty = FilteredRuns.Count == 0;
        }
        else
        {
            foreach (var item in _allVersions.Where(item => (branchId is null || item.BranchIds.Contains(branchId.Value))
                         && (needle.Length == 0 
                             || item.Comment.Contains(needle, StringComparison.OrdinalIgnoreCase)
                             || item.Message.Contains(needle, StringComparison.OrdinalIgnoreCase)
                             || item.FileName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                             || item.BranchDisplay.Contains(needle, StringComparison.OrdinalIgnoreCase))))
                FilteredHistory.Add(item);
            _missingCount = FilteredHistory.Count(item =>
                item.LocalPath is not null
                && !File.Exists(item.LocalPath)
                && !Directory.Exists(item.LocalPath));
            IsEmpty = FilteredHistory.Count == 0; UpdateTimelineVisuals(FilteredHistory);
        }
        OnPropertyChanged(nameof(HasMissing)); NotifyContextChanged();
    }

    private void Subscribe(BackupConfig? config)
    {
        _changeSubscription?.Dispose(); _changeSubscription = null;
        if (config is null) return;
        _changeSubscription = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id).ChangeFeed.Subscribe(change =>
        {
            _ = UiDispatcherService.RunOnUiAsync(RefreshCurrentHistory);
        });
    }

    private void NotifyViewModeChanged()
    { OnPropertyChanged(nameof(IsGroupedRunView)); OnPropertyChanged(nameof(ShowGroupedRunHistory)); OnPropertyChanged(nameof(ShowPerSourceHistory)); OnPropertyChanged(nameof(CanUsePerSourceActions)); }
    private void NotifyContextChanged()
    { OnPropertyChanged(nameof(CanUseCloudHistoryActions)); OnPropertyChanged(nameof(CanOpenConfigCloudSync)); OnPropertyChanged(nameof(CanUsePerSourceActions)); }
    private void NotifyBranchSelectionChanged()
    {
        OnPropertyChanged(nameof(CanCheckoutSelectedBranch));
        OnPropertyChanged(nameof(CanRenameSelectedBranch));
        OnPropertyChanged(nameof(CanDeleteSelectedBranch));
    }
    private void PersistSelection(BackupConfig? config, ManagedFolder? folder)
    {
        if (Settings is null) return; bool changed = false;
        if (config is not null && Settings.LastHistoryConfigId != config.Id) { Settings.LastHistoryConfigId = config.Id; changed = true; }
        if (folder is not null && Settings.LastHistoryFolderPath != folder.Path) { Settings.LastHistoryFolderPath = folder.Path; changed = true; }
        if (changed) ConfigService.Save();
    }

    private static Brush ThemeBrush(string key, Color fallback)
    { try { if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Brush brush) return brush; } catch { } return new SolidColorBrush(fallback); }
    private void UpdateTimelineVisuals(IEnumerable<NativeHistoryVersionViewItem> items)
    {
        var off = ThemeBrush("SystemControlForegroundBaseLowBrush", Colors.Gray);
        var fill = ThemeBrush("SystemControlBackgroundChromeMediumBrush", Colors.Transparent);
        foreach (var item in items)
        {
            var color = !UseHistoryStatusColors ? off : item.Readiness switch
            {
                HistoryPresentationReadiness.Ready => new SolidColorBrush(Colors.DodgerBlue),
                HistoryPresentationReadiness.PreparationRequired => new SolidColorBrush(Colors.LightSkyBlue),
                HistoryPresentationReadiness.PluginOrCredentialRequired => new SolidColorBrush(Colors.Gold),
                _ => new SolidColorBrush(Colors.OrangeRed)
            };
            item.TimelineLineBrush = color; item.TimelineNodeBorderBrush = color;
            item.TimelineNodeFillBrush = item.IsImportant ? new SolidColorBrush(Colors.Gold) : fill;
        }
    }
}

public sealed class NativeHistoryVersionViewItem(
    TimelineEntrySummary summary,
    IReadOnlyDictionary<BranchId, string>? branchNames = null)
{
    public VersionId VersionId => summary.VersionId;
    public RepresentationId? RepresentationId => summary.RepresentationId;
    public string TimeDisplay => summary.CreatedAtUtc.ToLocalTime().ToString("HH:mm:ss");
    public string DateDisplay => summary.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd");
    public string Comment => summary.Comment;
    public string Message => string.IsNullOrWhiteSpace(Comment) ? summary.DisplayName : Comment;
    public string FileName => summary.FileName ?? summary.VersionId.ToString();
    public string? LocalPath => summary.LocalPath;
    public bool IsImportant => summary.IsPinned;
    public bool IsPartialBackup => summary.CaptureScope == CaptureScope.PartialSource || summary.Fidelity == MaterializationFidelity.Partial;
    public bool IsMissing => summary.Readiness is HistoryPresentationReadiness.Unavailable or HistoryPresentationReadiness.PayloadReleased or HistoryPresentationReadiness.MetadataOnly;
    public bool HasLocalFile => summary.LocalPath is not null && File.Exists(summary.LocalPath);
    public bool HasCloudCopy => summary.Readiness == HistoryPresentationReadiness.PreparationRequired;
    public bool IsCloudOnly => HasCloudCopy && !HasLocalFile;
    public IReadOnlyList<BranchId> BranchIds => summary.BranchIds;
    public string FileSizeDisplay => GetFileSizeDisplay();
    public string BranchDisplay => string.Join(" · ", summary.BranchIds
        .Select(branchId => branchNames?.GetValueOrDefault(branchId))
        .Where(name => !string.IsNullOrWhiteSpace(name)));
    public bool HasBranchDisplay => BranchDisplay.Length > 0;
    public string CloudStatusText => HasCloudCopy ? I18n.GetString("History_CloudStatus_CloudAvailable") : string.Empty;
    public bool CanUploadToCloud => RepresentationId is not null && HasLocalFile && !HasCloudCopy;
    public bool CanDownloadFromCloud => RepresentationId is not null && HasCloudCopy && !HasLocalFile;
    public string CloudActionHintText => ReadinessText;
    public string DownloadFromCloudHintText => ReadinessText;
    public HistoryPresentationReadiness Readiness => summary.Readiness;
    public string ReadinessText => summary.Readiness switch
    {
        HistoryPresentationReadiness.Ready => I18n.GetString("History_NativeReadiness_Ready"),
        HistoryPresentationReadiness.PreparationRequired => I18n.GetString("History_NativeReadiness_PreparationRequired"),
        HistoryPresentationReadiness.PluginOrCredentialRequired => I18n.GetString("History_NativeReadiness_PluginRequired"),
        HistoryPresentationReadiness.PayloadReleased => I18n.GetString("History_NativeReadiness_Released"),
        HistoryPresentationReadiness.MetadataOnly => I18n.GetString("History_NativeReadiness_MetadataOnly"),
        _ => I18n.GetString("History_NativeReadiness_Unavailable")
    };
    public Brush? TimelineLineBrush { get; set; }
    public Brush? TimelineNodeFillBrush { get; set; }
    public Brush? TimelineNodeBorderBrush { get; set; }

    private string GetFileSizeDisplay()
    {
        if (string.IsNullOrWhiteSpace(LocalPath) || !File.Exists(LocalPath)) return string.Empty;
        try
        {
            var bytes = new FileInfo(LocalPath).Length;
            if (bytes < 1024) return $"{bytes.ToString("N0", CultureInfo.CurrentCulture)} B";
            if (bytes < 1024L * 1024) return $"{(bytes / 1024d).ToString("N1", CultureInfo.CurrentCulture)} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{(bytes / 1024d / 1024d).ToString("N1", CultureInfo.CurrentCulture)} MB";
            return $"{(bytes / 1024d / 1024d / 1024d).ToString("N2", CultureInfo.CurrentCulture)} GB";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}

public sealed class BackupRunViewItem(RunSummary summary)
{
    public RunId RunId => summary.RunId;
    public CheckpointId? ResultCheckpointId => summary.ResultCheckpointId;
    public string Comment => summary.Comment;
    public string TimeDisplay => summary.CompletedAtUtc.ToLocalTime().ToString("HH:mm");
    public string DateDisplay => summary.CompletedAtUtc.ToLocalTime().ToString("yyyy-MM-dd");
    public string Message => string.IsNullOrWhiteSpace(Comment) ? summary.Outcome.ToString() : Comment;
    public string SourceSummary => $"{summary.Sources.Length} sources";
    public bool IsImportant => summary.IsImportant;
    public bool CanRestore => ResultCheckpointId is not null;
    public bool HasPartialBackup => summary.HasPartialCapture;
    public IReadOnlyList<BranchId> BranchIds => summary.BranchIds;
    public IReadOnlyList<BackupRunSourceViewItem> Sources { get; } = summary.Sources.Select(item => new BackupRunSourceViewItem(item)).ToArray();
}

public sealed class BackupRunSourceViewItem(BackupRunSourceResult result)
{
    public string Name => result.SourceId.ToString();
    public string StatusText => result.Outcome.ToString();
    public string Detail => result.VersionId?.ToString() ?? result.Diagnostics.FirstOrDefault()?.Message ?? string.Empty;
}

public sealed class BranchViewItem(BranchSummary summary)
{
    public BranchId BranchId => summary.BranchId;
    public string Name => summary.Name;
    public string DisplayName => summary.IsActive
        ? I18n.Format("History_Branch_ActiveNameFormat", summary.Name)
        : summary.Name;
    public bool IsActive => summary.IsActive;
    public bool IsDeleted => summary.IsDeleted;
    public bool IsMultiTip => summary.IsMultiTip;
    public bool IsUnborn => summary.IsUnborn;
    public bool CanCheckout => summary.CanCheckout;
    public bool CanRename => summary.CanRename;
    public bool CanDelete => summary.CanDelete;
    public IReadOnlyList<BranchUpdate> Tips => summary.Tips;
}

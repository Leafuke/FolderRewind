using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.History.Legacy;
using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed partial class HistoryPageViewModel : ViewModelBase
{
    private static readonly TimeSpan ChangeRefreshDebounce = TimeSpan.FromMilliseconds(200);
    private readonly List<NativeHistoryVersionViewItem> _allVersions = [];
    private readonly List<BackupRunViewItem> _allRuns = [];
    private readonly HashSet<VersionId> _deletingVersions = [];
    private readonly object _deleteSync = new();
    private readonly object _changeRefreshSync = new();
    private readonly LatestRequestCoordinator _selectionRequests = new();
    private readonly LatestRequestCoordinator _refreshRequests = new();
    private IDisposable? _changeSubscription;
    private CancellationTokenSource? _changeRefreshSource;
    private BackupConfig? _currentConfig;
    private ManagedFolder? _currentFolder;
    private bool _isEmpty = true;
    private bool _isLoading;
    private string _errorMessage = string.Empty;
    private int _missingCount;
    private string _commentFilterText = string.Empty;
    private HistoryViewMode _viewMode = HistoryViewMode.PerSource;
    private BranchViewItem? _selectedBranch;
    private bool _refreshingBranches;
    private volatile bool _isActive;

    public BatchObservableCollection<NativeHistoryVersionViewItem> FilteredHistory { get; } = [];
    public BatchObservableCollection<BackupRunViewItem> FilteredRuns { get; } = [];
    public BatchObservableCollection<BranchViewItem> Branches { get; } = [];
    public BatchObservableCollection<SafetySnapshotViewItem> ActiveSafetySnapshots { get; } = [];
    public ObservableCollection<BackupConfig> Configs => ConfigService.CurrentConfig?.BackupConfigs ?? [];
    private GlobalSettings? Settings => ConfigService.CurrentConfig?.GlobalSettings;
    public bool IsEmpty { get => _isEmpty; private set => SetProperty(ref _isEmpty, value); }
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value)) OnPropertyChanged(nameof(ShowEmptyState));
        }
    }
    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (!SetProperty(ref _errorMessage, value ?? string.Empty)) return;
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool ShowEmptyState => IsEmpty && !IsLoading && !HasError;
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
    public bool CanStartCheckoutSelectedBranch => SelectedBranch?.CanStartCheckout == true;
    public bool CanReconcileSelectedBranch => SelectedBranch?.CanReconcile == true;
    public string SelectedBranchCheckoutStatusText => SelectedBranch?.CheckoutStatusText ?? string.Empty;
    public string SelectedBranchCheckoutDiagnostic => SelectedBranch?.CheckoutDiagnostic ?? string.Empty;
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
            if (Settings is not null)
            {
                Settings.UseHistoryStatusColors = value;
                ObservePreferenceSave(ConfigService.SaveAsync());
            }
            UpdateSemanticStatusPreferences(FilteredHistory); OnPropertyChanged();
        }
    }

    private static void ObservePreferenceSave(Task saveTask)
    {
        _ = saveTask.ContinueWith(
            task => LogService.LogError(
                $"[HistoryPageViewModel] Saving history preferences failed: {task.Exception?.GetBaseException().Message}",
                nameof(HistoryPageViewModel),
                task.Exception?.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    public void Initialize() { _viewMode = Settings?.LastHistoryViewMode ?? HistoryViewMode.PerSource; NotifyViewModeChanged(); }

    public async Task SetCurrentSelectionAsync(
        BackupConfig? config,
        ManagedFolder? folder,
        bool refreshHistoryIfFolder,
        bool persistSelection,
        CancellationToken cancellationToken = default)
    {
        using var request = _selectionRequests.Begin(cancellationToken);
        _isActive = true;
        _refreshRequests.CancelCurrent();
        CancelScheduledChangeRefresh();
        DisposeChangeSubscription();
        ErrorMessage = string.Empty;
        var shouldRefresh = config is not null
            && (IsGroupedRunView || (refreshHistoryIfFolder && folder is not null));
        IsLoading = shouldRefresh;
        if (!string.Equals(_currentConfig?.Id, config?.Id, StringComparison.OrdinalIgnoreCase)) SelectedBranch = null;
        _currentConfig = config;
        _currentFolder = folder;
        if (config is not null)
        {
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, request.Token);
            if (!request.IsCurrent) return;
            _changeSubscription = runtime.ChangeFeed.Subscribe(_ => ScheduleChangeRefresh());
        }
        if (!request.IsCurrent) return;
        NotifyContextChanged();
        if (shouldRefresh)
            await RefreshCurrentHistoryAsync(request.Token);
        else
        {
            ClearPresentation();
            IsLoading = false;
        }
        if (request.IsCurrent && persistSelection)
            await PersistSelectionAsync(config, folder, request.Token);
    }

    public void ClearCurrentSelection()
    {
        Suspend();
        _currentConfig = null;
        _currentFolder = null;
        ClearPresentation();
        ErrorMessage = string.Empty;
        NotifyContextChanged();
    }

    public void Suspend()
    {
        _isActive = false;
        CancelHistoryCommands();
        _selectionRequests.CancelCurrent();
        _refreshRequests.CancelCurrent();
        CancelScheduledChangeRefresh();
        DisposeChangeSubscription();
        IsLoading = false;
    }

    public void ReportLoadFailure(string message)
    {
        ErrorMessage = message ?? string.Empty;
        IsLoading = false;
    }

    private void ReportOperationFailure(string operation, Exception exception)
    {
        ErrorMessage = exception.Message;
        LogService.LogError(
            $"[HistoryPageViewModel] History {operation} failed: {exception.Message}",
            nameof(HistoryPageViewModel),
            exception);
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

    public Task RefreshCurrentHistoryAsync(CancellationToken cancellationToken = default)
    {
        CancelScheduledChangeRefresh();
        return RefreshCurrentHistoryCoreAsync(cancellationToken);
    }

    private async Task RefreshCurrentHistoryCoreAsync(CancellationToken cancellationToken)
    {
        if (!_isActive) return;
        using var request = _refreshRequests.Begin(cancellationToken);
        var token = request.Token;
        var selectedBranchId = SelectedBranch?.BranchId;
        var config = _currentConfig;
        var folder = _currentFolder;
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            if (config is null)
            {
                if (request.IsCurrent) ClearPresentation();
                return;
            }

            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token);
            SourceId? sourceId = folder is not null
                && Guid.TryParse(folder.Id, out var id)
                && id != Guid.Empty
                    ? new SourceId(id)
                    : null;
            var snapshot = await new HistoryPresentationQueryService(runtime)
                .QueryAsync(sourceId, cancellationToken: token);
            var presentation = await BuildPresentationAsync(config, snapshot, token);
            token.ThrowIfCancellationRequested();
            if (!request.IsCurrent || !_isActive) return;

            ApplyPresentation(presentation, selectedBranchId);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!request.IsCurrent || !_isActive) return;
            ErrorMessage = ex.Message;
            LogService.LogError(
                $"[HistoryPageViewModel] History refresh failed: {ex.Message}",
                nameof(HistoryPageViewModel),
                ex);
        }
        finally
        {
            if (request.IsCurrent) IsLoading = false;
        }
    }

    public async Task SetHistoryViewModeAsync(HistoryViewMode mode, CancellationToken cancellationToken = default)
    {
        _viewMode = mode;
        if (Settings is not null)
        {
            Settings.LastHistoryViewMode = mode;
            _ = await ConfigService.SaveAsync(cancellationToken: cancellationToken);
        }
        NotifyViewModeChanged();
        await RefreshCurrentHistoryAsync(cancellationToken);
    }

    public int GetMissingCount() => _missingCount;
    public async Task<int> ClearMissingEntriesAsync()
    {
        if (_currentConfig is null)
            return 0;
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(_currentConfig).ConfigureAwait(false);
        return await new HistoryLocalReplicaMaintenanceService(runtime)
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
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(_currentConfig).ConfigureAwait(false);
        var recovery = new HistoryArchiveRecoveryService(runtime);
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

    public async Task<bool> UpdateCommentAsync(
        NativeHistoryVersionViewItem item,
        string comment,
        CancellationToken cancellationToken = default)
    {
        if (_currentConfig is null) return false;
        try
        {
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(_currentConfig, cancellationToken);
            await runtime.Annotations.SetCommentAsync(
                new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, item.VersionId.Value),
                comment,
                cancellationToken);
            await RefreshCurrentHistoryAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportOperationFailure("version comment", ex);
            return false;
        }
    }

    public async Task<bool> ToggleImportantAsync(
        NativeHistoryVersionViewItem item,
        CancellationToken cancellationToken = default)
    {
        if (_currentConfig is null) return false;
        try
        {
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(_currentConfig, cancellationToken);
            await runtime.Annotations.SetPinAsync(
                new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, item.VersionId.Value),
                !item.IsImportant,
                cancellationToken);
            await RefreshCurrentHistoryAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportOperationFailure("version pin", ex);
            return false;
        }
    }

    public async Task<bool> UpdateRunCommentAsync(
        BackupRunViewItem item,
        string comment,
        CancellationToken cancellationToken = default)
    {
        if (_currentConfig is null) return false;
        try
        {
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(_currentConfig, cancellationToken);
            await runtime.Annotations.SetCommentAsync(
                new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Run, item.RunId.Value),
                comment,
                cancellationToken);
            await RefreshCurrentHistoryAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportOperationFailure("run comment", ex);
            return false;
        }
    }

    public async Task<bool> ToggleRunImportantAsync(
        BackupRunViewItem item,
        CancellationToken cancellationToken = default)
    {
        if (_currentConfig is null) return false;
        try
        {
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(_currentConfig, cancellationToken);
            await runtime.Annotations.SetRunImportantAsync(
                new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Run, item.RunId.Value),
                !item.IsImportant,
                cancellationToken);
            if (item.ResultCheckpointId is { } checkpointId)
                await runtime.Annotations.SetPinAsync(
                    new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Checkpoint, checkpointId.Value),
                    !item.IsImportant,
                    cancellationToken);
            await RefreshCurrentHistoryAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportOperationFailure("run pin", ex);
            return false;
        }
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
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(_currentConfig).ConfigureAwait(false);
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
        var config = _currentConfig;
        if (config is null)
            return new() { Success = false, Message = I18n.GetString("History_NoActiveConfiguration") };
        lock (_deleteSync)
        {
            if (!_deletingVersions.Add(item.VersionId))
                return new() { Success = false, Message = I18n.GetString("History_Delete_InProgress") };
        }
        try
        {
            await using var operationLease = await NativeHistoryConfigurationOperationGate
                .EnterAsync(config.Id).ConfigureAwait(false);
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config).ConfigureAwait(false);
            var deletesLocalPayload = mode is BackupDeleteMode.LocalArchiveOnly
                or BackupDeleteMode.LocalArchiveAndRecord;
            HistoryTargetedReplicaDeletionResult? deletion = null;
            if (deletesLocalPayload)
            {
                if (item.RepresentationId is not { } representationId
                    || string.IsNullOrWhiteSpace(item.LocalPath))
                {
                    return new()
                    {
                        Success = false,
                        Message = I18n.GetString("History_Delete_InvalidRequest")
                    };
                }

                // 手动删除只作用于用户选中的本地副本；不能借机运行配置级 Retention GC。
                deletion = await NativeHistoryApplicationService.DeleteVersionLocalPayloadAsync(
                        config,
                        item.VersionId,
                        representationId,
                        item.LocalPath,
                        releaseVersion: mode == BackupDeleteMode.LocalArchiveAndRecord)
                    .ConfigureAwait(false);
            }

            var suppressesRecord = mode is BackupDeleteMode.RecordOnly
                or BackupDeleteMode.LocalArchiveAndRecord;
            if (suppressesRecord)
            {
                await runtime.Annotations.SetSuppressionAsync(
                    new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, item.VersionId.Value),
                    suppressed: true).ConfigureAwait(false);
            }
            return new()
            {
                Success = true,
                ArchiveDeleted = deletion?.PayloadDeleted == true,
                HistoryUpdated = suppressesRecord || deletion is not null
            };
        }
        catch (Exception ex)
        {
            return new() { Success = false, Message = LocalizeDeleteError(ex.Message) };
        }
        finally
        {
            lock (_deleteSync)
            {
                _deletingVersions.Remove(item.VersionId);
            }
        }
    }

    public async Task<string?> GetLocalDeletionBlockerAsync(NativeHistoryVersionViewItem item)
    {
        var config = _currentConfig;
        if (config is null
            || item.RepresentationId is null
            || string.IsNullOrWhiteSpace(item.LocalPath))
        {
            return I18n.GetString("History_Delete_InvalidRequest");
        }

        try
        {
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config).ConfigureAwait(false);
            await runtime.MaterializationPolicies
                .EnsureCanReleaseAsync(item.VersionId, default).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return LocalizeDeleteError(ex.Message);
        }
    }

    private static string LocalizeDeleteError(string message)
        => message switch
        {
            "Version is protected by the current Workspace baseline." =>
                I18n.GetString("History_Delete_WorkspaceProtected"),
            "Version is protected by a Branch tip." =>
                I18n.GetString("History_Delete_BranchProtected"),
            "Version is protected by an active Safety Snapshot." =>
                I18n.GetString("History_Delete_SafetySnapshotProtected"),
            "Version is pinned and cannot be released." =>
                I18n.GetString("History_Delete_PinProtected"),
            "Version is protected by a pinned Checkpoint and cannot be released." =>
                I18n.GetString("History_Delete_PinProtected"),
            "The selected local backup is required by another Version Representation." =>
                I18n.GetString("History_Delete_DependencyProtected"),
            _ => message
        };

    public async Task<bool> CreateBranchAsync(CheckpointId checkpointId, string name)
    {
        if (_currentConfig is null) return false;
        await using var operationLease = await NativeHistoryConfigurationOperationGate
            .EnterAsync(_currentConfig.Id).ConfigureAwait(false);
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(_currentConfig).ConfigureAwait(false);
        await runtime.Branches
            .CreateFromCheckpointAsync(checkpointId, name).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RenameBranchAsync(BranchViewItem branch, string name)
    {
        if (_currentConfig is null || !branch.CanRename) return false;
        await using var operationLease = await NativeHistoryConfigurationOperationGate
            .EnterAsync(_currentConfig.Id).ConfigureAwait(false);
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(_currentConfig).ConfigureAwait(false);
        await runtime.Branches
            .RenameAsync(branch.BranchId, name).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DeleteBranchAsync(BranchViewItem branch)
    {
        if (_currentConfig is null || !branch.CanDelete) return false;
        await using var operationLease = await NativeHistoryConfigurationOperationGate
            .EnterAsync(_currentConfig.Id).ConfigureAwait(false);
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(_currentConfig).ConfigureAwait(false);
        await runtime.Branches
            .DeleteAsync(branch.BranchId).ConfigureAwait(false);
        return true;
    }

    public Task<HistoryCheckoutPlan?> PlanCheckoutBranchTipAsync(BranchViewItem branch)
    {
        if (_currentConfig is null || branch.Tips.Count != 1)
            return Task.FromResult<HistoryCheckoutPlan?>(null);
        return PlanAsync(_currentConfig, branch.Tips[0].UpdateId);

        static async Task<HistoryCheckoutPlan?> PlanAsync(BackupConfig config, BranchUpdateId tipId)
            => await NativeHistoryApplicationService.PlanCheckoutAsync(config, tipId).ConfigureAwait(false);
    }

    public async Task<HistoryCheckoutPlan?> PrepareCheckoutBranchTipAsync(BranchViewItem branch)
    {
        if (_currentConfig is null || branch.Tips.Count != 1) return null;
        return await NativeHistoryApplicationService.PrepareCheckoutAsync(
            _currentConfig,
            branch.Tips[0].UpdateId).ConfigureAwait(false);
    }

    public async Task<HistoryRestoreResult?> CheckoutBranchTipAsync(BranchViewItem branch)
    {
        if (_currentConfig is null || branch.Tips.Count != 1) return null;
        return await NativeHistoryApplicationService.CheckoutAsync(
            _currentConfig,
            branch.Tips[0].UpdateId).ConfigureAwait(false);
    }

    internal HistoryConfigurationRepairResult RepairMissingSource(
        MissingHistoricalSource missing,
        string confirmedPath,
        string expectedConfigRevision)
        => _currentConfig is null
            ? new(HistoryConfigurationRepairStatus.StaleConfig, "No active configuration is selected.")
            : HistorySourceBindingRepairService.RestoreMissingBinding(
                _currentConfig,
                missing,
                confirmedPath,
                expectedConfigRevision);

    internal HistoryConfigurationRepairResult RepairHistoricalBoundary(
        HistorySourceBoundaryMismatch mismatch,
        string expectedConfigRevision)
        => _currentConfig is null
            ? new(HistoryConfigurationRepairStatus.StaleConfig, "No active configuration is selected.")
            : HistorySourceBindingRepairService.RestoreHistoricalBoundary(
                _currentConfig,
                mismatch,
                expectedConfigRevision,
                acceptConfigWideFilterImpact: true);

    public string? CurrentConfigRevision => _currentConfig?.ConfigRevision;

    public async Task<bool> ReconcileBranchAsync(BranchViewItem branch, BranchUpdateId winnerTipId)
    {
        if (_currentConfig is null || !branch.IsMultiTip) return false;
        await using var operationLease = await NativeHistoryConfigurationOperationGate
            .EnterAsync(_currentConfig.Id).ConfigureAwait(false);
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(_currentConfig).ConfigureAwait(false);
        await new HistoryBranchReconciliationService(runtime)
            .ReconcileAsync(
                branch.BranchId,
                branch.Tips.Select(item => item.UpdateId),
                winnerTipId).ConfigureAwait(false);
        return true;
    }

    public async Task<HistoryRestoreResult?> RestoreSafetySnapshotAsync(SafetySnapshotViewItem item)
    {
        if (_currentConfig is null) return null;
        return await NativeHistoryApplicationService.RestoreSafetySnapshotAsync(
            _currentConfig,
            item.SnapshotId,
            BackupService.RestoreMode.Clean).ConfigureAwait(false);
    }

    public async Task<bool> ReleaseSafetySnapshotAsync(SafetySnapshotViewItem item)
    {
        if (_currentConfig is null) return false;
        return await NativeHistoryApplicationService.ReleaseSafetySnapshotAsync(
            _currentConfig,
            item.SnapshotId).ConfigureAwait(false);
    }

    private static Task<HistoryPresentationResult> BuildPresentationAsync(
        BackupConfig config,
        HistoryPresentationSnapshot snapshot,
        CancellationToken cancellationToken)
        => Task.Run(async () =>
        {
            var branchNames = snapshot.Branches.ToDictionary(branch => branch.BranchId, branch => branch.Name);
            var versions = snapshot.Timeline
                .Select(item => new NativeHistoryVersionViewItem(item, branchNames))
                .ToArray();
            var runs = snapshot.Runs.Select(item => new BackupRunViewItem(item)).ToArray();
            var branches = new BranchViewItem[snapshot.Branches.Length];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, snapshot.Branches.Length),
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = 4
                },
                async (index, token) =>
                {
                    var branch = snapshot.Branches[index];
                    HistoryCheckoutPlan? checkoutPlan = null;
                    if (!branch.IsMultiTip && branch.HasCheckoutTarget && branch.Tips.Length == 1)
                    {
                        try
                        {
                            checkoutPlan = await NativeHistoryApplicationService.PlanCheckoutAsync(
                                    config,
                                    branch.Tips[0].UpdateId,
                                    AssessmentDepth.Fast,
                                    token)
                                .ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            checkoutPlan = new HistoryCheckoutPlan(
                                HistoryCheckoutReadiness.Blocked,
                                branch.Tips[0],
                                null,
                                -1,
                                [],
                                [],
                                [],
                                ex.Message);
                        }
                    }

                    branches[index] = new BranchViewItem(branch, checkoutPlan);
                }).ConfigureAwait(false);
            var safetySnapshots = snapshot.ActiveSafetySnapshots
                .Select(item => new SafetySnapshotViewItem(item))
                .ToArray();
            return new HistoryPresentationResult(versions, runs, branches, safetySnapshots);
        }, cancellationToken);

    private void ApplyPresentation(HistoryPresentationResult presentation, BranchId? selectedBranchId)
    {
        _allVersions.Clear();
        _allVersions.AddRange(presentation.Versions);
        _allRuns.Clear();
        _allRuns.AddRange(presentation.Runs);
        _refreshingBranches = true;
        try
        {
            Branches.ReplaceAll(presentation.Branches);
            ActiveSafetySnapshots.ReplaceAll(presentation.SafetySnapshots);
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

    private void ClearPresentation()
    {
        _allVersions.Clear();
        _allRuns.Clear();
        FilteredHistory.ReplaceAll([]);
        FilteredRuns.ReplaceAll([]);
        _refreshingBranches = true;
        try
        {
            Branches.ReplaceAll([]);
            ActiveSafetySnapshots.ReplaceAll([]);
            SelectedBranch = null;
        }
        finally
        {
            _refreshingBranches = false;
        }
        _missingCount = 0;
        IsEmpty = true;
        OnPropertyChanged(nameof(HasMissing));
        OnPropertyChanged(nameof(CurrentBranchDisplay));
        NotifyBranchSelectionChanged();
    }

    private void ApplyFilter()
    {
        var needle = CommentFilterText.Trim();
        var branchId = SelectedBranch?.BranchId;
        if (IsGroupedRunView)
        {
            var runs = _allRuns.Where(item => (branchId is null || item.BranchIds.Contains(branchId.Value))
                         && (needle.Length == 0
                             || item.Comment.Contains(needle, StringComparison.OrdinalIgnoreCase)
                             || item.Message.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            FilteredRuns.ReplaceAll(runs);
            FilteredHistory.ReplaceAll([]);
            _missingCount = 0; IsEmpty = FilteredRuns.Count == 0;
        }
        else
        {
            var versions = _allVersions.Where(item => (branchId is null || item.BranchIds.Contains(branchId.Value))
                         && (needle.Length == 0
                             || item.Comment.Contains(needle, StringComparison.OrdinalIgnoreCase)
                             || item.Message.Contains(needle, StringComparison.OrdinalIgnoreCase)
                             || item.FileName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                             || item.BranchDisplay.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            FilteredHistory.ReplaceAll(versions);
            FilteredRuns.ReplaceAll([]);
            _missingCount = FilteredHistory.Count(item => item.IsLocalPayloadMissing);
            IsEmpty = FilteredHistory.Count == 0; UpdateSemanticStatusPreferences(FilteredHistory);
        }
        OnPropertyChanged(nameof(HasMissing)); NotifyContextChanged();
    }

    private void ScheduleChangeRefresh()
    {
        if (!_isActive) return;
        var source = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (_changeRefreshSync)
        {
            if (!_isActive)
            {
                source.Dispose();
                return;
            }
            previous = _changeRefreshSource;
            _changeRefreshSource = source;
        }

        CancelAndDispose(previous);
        _ = RefreshAfterChangeDelayAsync(source, source.Token);
    }

    private async Task RefreshAfterChangeDelayAsync(
        CancellationTokenSource source,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ChangeRefreshDebounce, cancellationToken).ConfigureAwait(false);
            await UiDispatcherService.RunOnUiAsync(
                () => RefreshCurrentHistoryCoreAsync(cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogService.LogError(
                $"[HistoryPageViewModel] Debounced history refresh failed: {ex.Message}",
                nameof(HistoryPageViewModel),
                ex);
        }
        finally
        {
            lock (_changeRefreshSync)
            {
                if (ReferenceEquals(_changeRefreshSource, source))
                    _changeRefreshSource = null;
            }
            source.Dispose();
        }
    }

    private void CancelScheduledChangeRefresh()
    {
        CancellationTokenSource? source;
        lock (_changeRefreshSync)
        {
            source = _changeRefreshSource;
            _changeRefreshSource = null;
        }
        CancelAndDispose(source);
    }

    private static void CancelAndDispose(CancellationTokenSource? source)
    {
        if (source is null) return;
        try
        {
            source.Cancel(throwOnFirstException: false);
        }
        catch (AggregateException ex)
        {
            LogService.LogWarning(
                $"[HistoryPageViewModel] A history refresh cancellation callback failed: {ex.Message}",
                nameof(HistoryPageViewModel));
        }
        finally
        {
            source.Dispose();
        }
    }

    private void DisposeChangeSubscription()
    {
        _changeSubscription?.Dispose();
        _changeSubscription = null;
    }

    private void NotifyViewModeChanged()
    { OnPropertyChanged(nameof(IsGroupedRunView)); OnPropertyChanged(nameof(ShowGroupedRunHistory)); OnPropertyChanged(nameof(ShowPerSourceHistory)); OnPropertyChanged(nameof(CanUsePerSourceActions)); NotifyCommandStateChanged(); }
    private void NotifyContextChanged()
    { OnPropertyChanged(nameof(CanUseCloudHistoryActions)); OnPropertyChanged(nameof(CanOpenConfigCloudSync)); OnPropertyChanged(nameof(CanUsePerSourceActions)); NotifyCommandStateChanged(); }
    private void NotifyBranchSelectionChanged()
    {
        OnPropertyChanged(nameof(CanStartCheckoutSelectedBranch));
        OnPropertyChanged(nameof(CanReconcileSelectedBranch));
        OnPropertyChanged(nameof(SelectedBranchCheckoutStatusText));
        OnPropertyChanged(nameof(SelectedBranchCheckoutDiagnostic));
        OnPropertyChanged(nameof(CanRenameSelectedBranch));
        OnPropertyChanged(nameof(CanDeleteSelectedBranch));
        NotifyCommandStateChanged();
    }
    private async Task PersistSelectionAsync(
        BackupConfig? config,
        ManagedFolder? folder,
        CancellationToken cancellationToken)
    {
        if (Settings is null) return; bool changed = false;
        if (config is not null && Settings.LastHistoryConfigId != config.Id) { Settings.LastHistoryConfigId = config.Id; changed = true; }
        if (folder is not null && Settings.LastHistoryFolderPath != folder.Path) { Settings.LastHistoryFolderPath = folder.Path; changed = true; }
        if (changed) _ = await ConfigService.SaveAsync(cancellationToken: cancellationToken);
    }

    private sealed record HistoryPresentationResult(
        IReadOnlyList<NativeHistoryVersionViewItem> Versions,
        IReadOnlyList<BackupRunViewItem> Runs,
        IReadOnlyList<BranchViewItem> Branches,
        IReadOnlyList<SafetySnapshotViewItem> SafetySnapshots);

    private void UpdateSemanticStatusPreferences(IEnumerable<NativeHistoryVersionViewItem> items)
    {
        foreach (var item in items)
        {
            item.ApplySemanticColorPreference(UseHistoryStatusColors);
        }
    }
}

public sealed class NativeHistoryVersionViewItem(
    TimelineEntrySummary summary,
    IReadOnlyDictionary<BranchId, string>? branchNames = null) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private readonly bool _hasLocalFile = summary.LocalPath is not null && File.Exists(summary.LocalPath);
    private readonly bool _isLocalPayloadMissing = summary.LocalPath is not null
        && !File.Exists(summary.LocalPath)
        && !Directory.Exists(summary.LocalPath);
    private readonly string _fileSizeDisplay = GetFileSizeDisplay(summary.LocalPath);
    private SemanticStatus _readinessStatus = MapReadiness(summary.Readiness);

    public VersionId VersionId => summary.VersionId;
    public RepresentationId? RepresentationId => summary.RepresentationId;
    public string TimeDisplay => UserDisplayFormatter.LongTime(summary.CreatedAtUtc.ToLocalTime());
    public string DateDisplay => UserDisplayFormatter.Date(summary.CreatedAtUtc.ToLocalTime());
    public string Comment => summary.Comment;
    public string Message => string.IsNullOrWhiteSpace(Comment) ? summary.DisplayName : Comment;
    public string FileName => summary.FileName ?? summary.VersionId.ToString();
    public string? LocalPath => summary.LocalPath;
    public bool IsImportant => summary.IsPinned;
    public bool IsPartialBackup => summary.CaptureScope == CaptureScope.PartialSource || summary.Fidelity == MaterializationFidelity.Partial;
    public bool IsMissing => summary.Readiness is HistoryPresentationReadiness.Unavailable or HistoryPresentationReadiness.PayloadReleased or HistoryPresentationReadiness.MetadataOnly;
    public bool HasLocalFile => _hasLocalFile;
    public bool IsLocalPayloadMissing => _isLocalPayloadMissing;
    public bool HasCloudCopy => summary.Readiness == HistoryPresentationReadiness.PreparationRequired;
    public bool IsCloudOnly => HasCloudCopy && !HasLocalFile;
    public IReadOnlyList<BranchId> BranchIds => summary.BranchIds;
    public CheckpointId? BranchableCheckpointId => summary.BranchableCheckpointId;
    public bool CanCreateBranch => summary.BranchableCheckpointCount == 1;
    public string CreateBranchHintText => summary.BranchableCheckpointCount switch
    {
        0 => I18n.GetString("History_Branch_CreateUnavailableNoCheckpoint"),
        1 => I18n.GetString("History_Branch_CreateFromHereHint"),
        _ => I18n.GetString("History_Branch_CreateUnavailableAmbiguousCheckpoint")
    };
    public string FileSizeDisplay => _fileSizeDisplay;
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
    public SemanticStatus ReadinessStatus
    {
        get => _readinessStatus;
        private set
        {
            SetProperty(ref _readinessStatus, value);
        }
    }
    public string ReadinessGlyph => SemanticStatusGlyphs.GetGlyph(MapReadiness(Readiness));

    public void ApplySemanticColorPreference(bool useStatusColors)
        => ReadinessStatus = useStatusColors ? MapReadiness(Readiness) : SemanticStatus.Neutral;

    private static SemanticStatus MapReadiness(HistoryPresentationReadiness readiness) => readiness switch
    {
        HistoryPresentationReadiness.Ready => SemanticStatus.Success,
        HistoryPresentationReadiness.PreparationRequired => SemanticStatus.Info,
        HistoryPresentationReadiness.PluginOrCredentialRequired => SemanticStatus.Warning,
        _ => SemanticStatus.Error
    };

    private static string GetFileSizeDisplay(string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath)) return string.Empty;
        try
        {
            var bytes = new FileInfo(localPath).Length;
            if (bytes < 1024) return $"{UserDisplayFormatter.Number(bytes)} B";
            if (bytes < 1024L * 1024) return $"{UserDisplayFormatter.Number(bytes / 1024d, 1)} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{UserDisplayFormatter.Number(bytes / 1024d / 1024d, 1)} MB";
            return $"{UserDisplayFormatter.Number(bytes / 1024d / 1024d / 1024d, 2)} GB";
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
    public string TimeDisplay => UserDisplayFormatter.ShortTime(summary.CompletedAtUtc.ToLocalTime());
    public string DateDisplay => UserDisplayFormatter.Date(summary.CompletedAtUtc.ToLocalTime());
    public string Message => string.IsNullOrWhiteSpace(Comment) ? GetRunOutcomeText(summary.Outcome) : Comment;
    public string SourceSummary => I18n.Format("History_Run_SourceCount", summary.Sources.Length);
    public bool IsImportant => summary.IsImportant;
    public bool CanRestore => ResultCheckpointId is not null;
    public bool CanCreateBranch => summary.IsBranchableCheckpoint;
    public string CreateBranchHintText => CanCreateBranch
        ? I18n.GetString("History_Branch_CreateFromHereHint")
        : I18n.GetString("History_Branch_CreateUnavailableNoCheckpoint");
    public bool HasPartialBackup => summary.HasPartialCapture;
    public IReadOnlyList<BranchId> BranchIds => summary.BranchIds;
    public IReadOnlyList<BackupRunSourceViewItem> Sources { get; } = summary.Sources.Select(item => new BackupRunSourceViewItem(item)).ToArray();

    private static string GetRunOutcomeText(BackupRunOutcome outcome) => outcome switch
    {
        BackupRunOutcome.Completed => I18n.GetString("History_Run_OutcomeCompleted"),
        BackupRunOutcome.Partial => I18n.GetString("History_Run_OutcomePartial"),
        BackupRunOutcome.Failed => I18n.GetString("History_Run_OutcomeFailed"),
        BackupRunOutcome.NoChange => I18n.GetString("History_Run_OutcomeNoChange"),
        _ => outcome.ToString()
    };
}

public sealed class BackupRunSourceViewItem(BackupRunSourceResult result)
{
    public string Name => result.SourceId.ToString();
    public string StatusText => result.Outcome switch
    {
        BackupRunSourceOutcome.Captured => I18n.GetString("History_Run_SourceNewArchive"),
        BackupRunSourceOutcome.Reused => I18n.GetString("History_Run_SourceReused"),
        BackupRunSourceOutcome.Failed => I18n.GetString("History_Run_SourceFailed"),
        BackupRunSourceOutcome.Unavailable => I18n.GetString("History_Run_SourceUnavailable"),
        BackupRunSourceOutcome.CarriedForward => I18n.GetString("History_Run_SourceCarriedForward"),
        _ => result.Outcome.ToString()
    };
    public string Detail => result.VersionId?.ToString() ?? result.Diagnostics.FirstOrDefault()?.Message ?? string.Empty;
}

public sealed class BranchViewItem(BranchSummary summary, HistoryCheckoutPlan? checkoutPlan)
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
    public HistoryCheckoutReadiness CheckoutReadiness => summary.IsMultiTip
        ? HistoryCheckoutReadiness.BranchReconciliationRequired
        : checkoutPlan?.Readiness ?? HistoryCheckoutReadiness.Blocked;
    public string CheckoutStatusText => CheckoutReadiness switch
    {
        HistoryCheckoutReadiness.Ready => I18n.GetString("History_CheckoutReadiness_Ready"),
        HistoryCheckoutReadiness.PreparationRequired => I18n.GetString("History_CheckoutReadiness_PreparationRequired"),
        HistoryCheckoutReadiness.ProtectionRequired => I18n.GetString("History_CheckoutReadiness_ProtectionRequired"),
        HistoryCheckoutReadiness.ConfigurationMappingRequired => I18n.GetString("History_CheckoutReadiness_ConfigurationMappingRequired"),
        HistoryCheckoutReadiness.ConfigurationBoundaryChangeRequired => I18n.GetString("History_CheckoutReadiness_ConfigurationBoundaryChangeRequired"),
        HistoryCheckoutReadiness.ExactRepresentationUnavailable => I18n.GetString("History_CheckoutReadiness_ExactRepresentationUnavailable"),
        HistoryCheckoutReadiness.BranchReconciliationRequired => I18n.GetString("History_CheckoutReadiness_BranchReconciliationRequired"),
        HistoryCheckoutReadiness.StalePlan => I18n.GetString("History_CheckoutReadiness_StalePlan"),
        HistoryCheckoutReadiness.CoordinatorUnavailable => I18n.GetString("History_CheckoutReadiness_CoordinatorUnavailable"),
        _ => I18n.GetString("History_CheckoutReadiness_Blocked")
    };
    public string CheckoutDiagnostic => checkoutPlan?.Diagnostic ?? CheckoutStatusText;
    public bool CanStartCheckout => !(summary.IsActive && summary.IsWorkspaceAnchoredAtTip)
        && summary.HasCheckoutTarget
        && CheckoutReadiness is HistoryCheckoutReadiness.Ready
            or HistoryCheckoutReadiness.PreparationRequired
            or HistoryCheckoutReadiness.ProtectionRequired
            or HistoryCheckoutReadiness.ConfigurationMappingRequired
            or HistoryCheckoutReadiness.ConfigurationBoundaryChangeRequired
            or HistoryCheckoutReadiness.StalePlan;
    public bool CanReconcile => summary.IsMultiTip;
    public bool CanRename => summary.CanRename;
    public bool CanDelete => summary.CanDelete;
    public IReadOnlyList<BranchUpdate> Tips => summary.Tips;
}

public sealed class SafetySnapshotViewItem(SafetySnapshotProjection projection)
{
    public SafetySnapshotId SnapshotId => projection.Snapshot.SnapshotId;
    public CheckpointId CheckpointId => projection.Snapshot.CheckpointId;
    public string DisplayName => $"{UserDisplayFormatter.LongDateTime(projection.Snapshot.CreatedAtUtc.ToLocalTime())} · {projection.Snapshot.Reason}";
}

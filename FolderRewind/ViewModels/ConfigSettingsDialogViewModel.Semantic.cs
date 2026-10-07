using CommunityToolkit.Mvvm.Input;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed partial class ConfigSettingsDialogViewModel
{
    private string? _semanticUnavailableReason;
    private bool _semanticCheckpointRunning;
    private string _semanticCheckpointStatus = string.Empty;
    public IAsyncRelayCommand SemanticCheckpointCommand { get; private set; } = null!;
    public IRelayCommand CancelSemanticCheckpointCommand { get; private set; } = null!;
    public IRelayCommand RefreshSemanticAvailabilityCommand { get; private set; } = null!;

    public bool ExperimentalSemanticBackupEnabled
    {
        get => Config.ArtifactTransformPolicy?.ExperimentalEnabled == true;
        set
        {
            if (value == ExperimentalSemanticBackupEnabled || _semanticCheckpointRunning) return;
            if (!value) Config.ArtifactTransformPolicy = null;
            else
            {
                RefreshSemanticAvailability();
                if (_semanticUnavailableReason is not null) return;
                var provider = ExperimentalArtifactPolicyService.FindProvider(Config);
                if (provider is null) { RefreshSemanticAvailability(); return; }
                Config.ArtifactTransformPolicy = new()
                {
                    ExperimentalEnabled = true,
                    Transformer = new() { PluginId = provider.Value.PluginId.Value, TransformerId = provider.Value.TransformerId },
                    Parameters = new(StringComparer.Ordinal) { ["representation"] = JsonSerializer.SerializeToElement("incremental-v1") },
                    FailureBehavior = PersistedArtifactTransformFailureBehavior.KeepPrimaryWithWarnings
                };
            }
            SemanticCheckpointStatus = string.Empty;
            RefreshSemanticAvailability();
        }
    }
    public bool SemanticBackupFallbackEnabled
    {
        get => Config.ArtifactTransformPolicy?.FailureBehavior != PersistedArtifactTransformFailureBehavior.RequireTransform;
        set
        {
            if (!ExperimentalSemanticBackupEnabled || _semanticCheckpointRunning) return;
            var policy = ConfigSnapshotMapper.Copy(Config.ArtifactTransformPolicy);
            policy.FailureBehavior = value ? PersistedArtifactTransformFailureBehavior.KeepPrimaryWithWarnings
                : PersistedArtifactTransformFailureBehavior.RequireTransform;
            Config.ArtifactTransformPolicy = policy;
        }
    }
    public bool CanToggleSemanticBackup => !_semanticCheckpointRunning && (ExperimentalSemanticBackupEnabled || _semanticUnavailableReason is null);
    public bool CanEditSemanticBackup => !_semanticCheckpointRunning && ExperimentalSemanticBackupEnabled;
    public bool CanConfigureCoreSmartChain => !ExperimentalSemanticBackupEnabled;
    public bool IsSemanticCheckpointRunning => _semanticCheckpointRunning;
    public bool CanCreateSemanticCheckpoint => ExperimentalSemanticBackupEnabled && _semanticUnavailableReason is null
        && !_semanticCheckpointRunning && _actionLifetime.CanExecute && !_saveController.IsSaving;
    public string SemanticBackupAvailability => _semanticUnavailableReason is { } reason ? I18n.GetString(reason)
        : I18n.GetString(ExperimentalSemanticBackupEnabled ? "SemanticBackup_EnabledHint" : "SemanticBackup_DisabledHint");
    public string SemanticCheckpointLabel => I18n.GetString(HasUnsavedChanges ? "SemanticBackup_SaveCheckpoint" : "SemanticBackup_CreateCheckpoint");
    public string SemanticCheckpointStatus
    {
        get => _semanticCheckpointStatus;
        private set => SetProperty(ref _semanticCheckpointStatus, value);
    }
    private void InitializeSemanticCommands()
    {
        SemanticCheckpointCommand = new AsyncRelayCommand(token => _actionLifetime.RunAsync(CreateSemanticCheckpointCoreAsync, token),
            () => CanCreateSemanticCheckpoint);
        CancelSemanticCheckpointCommand = new RelayCommand(() => _actionLifetime.Cancel(), () => _semanticCheckpointRunning);
        RefreshSemanticAvailabilityCommand = new RelayCommand(RefreshSemanticAvailability);
        _actionLifetime.StateChanged += RaiseSemanticProperties;
        RefreshSemanticAvailability();
    }
    internal void RefreshSemanticAvailability()
    {
        _semanticUnavailableReason = ExperimentalArtifactPolicyService.UnavailableReason(Config);
        RaiseSemanticProperties();
    }
    private void RaiseSemanticProperties()
    {
        OnPropertyChanged(nameof(ExperimentalSemanticBackupEnabled));
        OnPropertyChanged(nameof(SemanticBackupFallbackEnabled));
        OnPropertyChanged(nameof(CanToggleSemanticBackup));
        OnPropertyChanged(nameof(CanEditSemanticBackup));
        OnPropertyChanged(nameof(CanConfigureCoreSmartChain));
        OnPropertyChanged(nameof(CanCreateSemanticCheckpoint));
        OnPropertyChanged(nameof(IsSemanticCheckpointRunning));
        OnPropertyChanged(nameof(SemanticBackupAvailability));
        OnPropertyChanged(nameof(SemanticCheckpointLabel));
        SemanticCheckpointCommand?.NotifyCanExecuteChanged();
        CancelSemanticCheckpointCommand?.NotifyCanExecuteChanged();
        SaveCommand?.NotifyCanExecuteChanged();
        DeleteCommand?.NotifyCanExecuteChanged();
        ActionCommand?.NotifyCanExecuteChanged();
    }
    private async Task CreateSemanticCheckpointCoreAsync(CancellationToken token)
    {
        _semanticCheckpointRunning = true;
        SemanticCheckpointStatus = I18n.GetString("SemanticBackup_CheckpointRunning");
        RaiseSemanticProperties();
        try
        {
            await SaveForCleanupAsync(token);
            var current = ConfigService.CurrentConfig.BackupConfigs.Single(c => c.Id == Config.Id);
            var result = await Task.Run(() => BackupService.CreateSemanticCheckpointAsync(current, token), token);
            SemanticCheckpointStatus = I18n.GetString(result.HistoryRecoveryRequired ? "SemanticBackup_CheckpointRecoveryRequired"
                : result.Outcome == OperationOutcome.Canceled ? "SemanticBackup_CheckpointCanceled"
                : !result.IsSuccessful || !result.CreatedNewArchive ? "SemanticBackup_CheckpointFailed"
                : result.Outcome == OperationOutcome.SuccessWithWarnings ? "SemanticBackup_CheckpointWarnings"
                : "SemanticBackup_CheckpointSucceeded");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { SemanticCheckpointStatus = I18n.GetString("SemanticBackup_CheckpointCanceled"); }
        catch (Exception ex)
        {
            SemanticCheckpointStatus = I18n.GetString("SemanticBackup_CheckpointFailed") + " " + ex.Message;
            ReportSettingsError(SemanticCheckpointStatus);
        }
        finally
        {
            _editingDraft?.RefreshBackupTimesAfterOperation();
            _semanticCheckpointRunning = false;
            RefreshSemanticAvailability();
        }
    }
}

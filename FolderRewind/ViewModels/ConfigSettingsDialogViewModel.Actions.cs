using CommunityToolkit.Mvvm.Input;
using FolderRewind.Models;
using FolderRewind.History.Application;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

namespace FolderRewind.ViewModels;

public sealed partial class ConfigSettingsDialogViewModel
{
    private readonly SettingsSaveController _saveController = new();
    private IConfigSettingsActions? _actions;
    private readonly AsyncCommandLifetime _actionLifetime = new(ex => ReportSettingsError(ex.Message));
    internal IAsyncRelayCommand<ConfigSettingsAction> ActionCommand { get; private set; } = null!;
    internal IRelayCommand<ConfigSettingsEditRequest> EditCommand { get; private set; } = null!;
    public IAsyncRelayCommand SaveCommand { get; private set; } = null!;
    public IAsyncRelayCommand DeleteCommand { get; private set; } = null!;
    public bool LastSaveSucceeded { get; private set; }
    public bool LastDeleteSucceeded { get; private set; }
    private void InitializeActions(IConfigSettingsActions actions)
    {
        _actions = actions;
        EditCommand = new RelayCommand<ConfigSettingsEditRequest>(ApplyDraftEdit);
        ActionCommand = new AsyncRelayCommand<ConfigSettingsAction>(
            (action, token) => _actionLifetime.RunAsync(ct => _actions.ExecuteAsync(action, ct), token),
            _ => _actionLifetime.CanExecute && !_saveController.IsSaving);
        SaveCommand = new AsyncRelayCommand(async () =>
        {
            LastSaveSucceeded = await _saveController.SaveAsync(ValidateForSave, () => ConfigService.SaveAsync(), ReportSettingsError);
        }, () => _actionLifetime.CanExecute);
        DeleteCommand = new AsyncRelayCommand(token => _actionLifetime.RunAsync(async ct =>
        {
            LastDeleteSucceeded = false;
            LastDeleteSucceeded = await DeleteAsync(ct);
        }, token), () => _actionLifetime.CanExecute && !_saveController.IsSaving);
    }

    private static void ReportSettingsError(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) message = I18n.GetString("Common_Failed");
        LogService.LogError(message, nameof(ConfigSettingsDialogViewModel));
        NotificationService.ShowError(message, I18n.GetString("Common_Failed"));
    }

    private string? ValidateForSave()
    {
        foreach (var folder in Config.SourceFolders ?? new ObservableCollection<ManagedFolder>())
        {
            if (!BackupStoragePathService.TryResolveBackupStoragePaths(
                    Config.DestinationPath,
                    folder.DisplayName,
                    folder.Path,
                    out _,
                    out var backupSubDir,
                    out var metadataDir))
            {
                return I18n.GetString("BackupService_Log_InvalidStorageFolderName");
            }

            var overlap = BackupPathOverlapPolicy.Validate(folder.Path, backupSubDir, metadataDir);
            if (!overlap.IsSafe)
            {
                return I18n.Format(
                    "BackupService_Folder_SourceDestinationOverlap",
                    overlap.SourcePath,
                    overlap.TargetPath);
            }
        }

        if (!TryValidateAndNormalizeAdditionalSevenZipArguments(out var errorMessage))
        {
            return errorMessage;
        }

        if (!TryValidateFilters(out errorMessage))
        {
            return errorMessage;
        }

        if (!TryValidateBackupScope(out errorMessage))
        {
            return errorMessage;
        }


        return null;
    }

    public string ConfigFilePath => ConfigService.ConfigFilePath;
    internal void SelectConfigKind(PluginConfigKindOption option)
    {
        PluginService.ApplyConfigKind(Config, option, applyEncryption: false);
        RefreshBackupScopeOptions();
    }
    internal System.Collections.Generic.IReadOnlyList<PluginConfigKindOption> GetConfigKindOptions() => PluginService.GetAllSupportedConfigKinds();
    internal PluginConfigKindOption ResolveConfigKind() => PluginService.ResolveConfigKindOption(Config);

    private async Task<bool> DeleteAsync(CancellationToken token)
    {
        try
        {
            if (!await _actions!.ConfirmDeleteAsync(token)) return false;
            token.ThrowIfCancellationRequested();
            var current = ConfigService.CurrentConfig;
            var config = current.BackupConfigs.FirstOrDefault(item => item.Id == Config.Id);
            if (config is null) return true;
            var index = current.BackupConfigs.IndexOf(config);
            var settings = current.GlobalSettings;
            var managerId = settings.LastManagerConfigId;
            var managerPath = settings.LastManagerFolderPath;
            var historyId = settings.LastHistoryConfigId;
            var historyPath = settings.LastHistoryFolderPath;
            var fallback = current.BackupConfigs.FirstOrDefault(item => item.Id != config.Id)?.Id ?? string.Empty;
            await ConfigEditTransaction.ApplyAsync(() =>
            {
                current.BackupConfigs.Remove(config);
                if (settings.LastManagerConfigId == config.Id) { settings.LastManagerConfigId = fallback; settings.LastManagerFolderPath = string.Empty; }
                if (settings.LastHistoryConfigId == config.Id) { settings.LastHistoryConfigId = fallback; settings.LastHistoryFolderPath = string.Empty; }
            }, () =>
            {
                if (!current.BackupConfigs.Contains(config)) current.BackupConfigs.Insert(Math.Min(index, current.BackupConfigs.Count), config);
                settings.LastManagerConfigId = managerId; settings.LastManagerFolderPath = managerPath;
                settings.LastHistoryConfigId = historyId; settings.LastHistoryFolderPath = historyPath;
            }, () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"));
            try { await NativeHistoryCoreGateway.DetachActiveConfigAsync(config.Id); }
            catch (Exception ex) { ReportSettingsError(ex.Message); }
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception ex) { ReportSettingsError(ex.Message); return false; }
    }

    internal void ActivateActions() => _actionLifetime.Activate();
    internal void CancelActions() => _actionLifetime.Deactivate();
}

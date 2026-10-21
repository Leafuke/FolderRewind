using CommunityToolkit.Mvvm.Input;
using FolderRewind.History.Application;
using FolderRewind.History.LocalState;
using FolderRewind.Services;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed partial class HistoryPageViewModel
{
    private string _recoveryMessage = string.Empty;
    private string _recoveryDetails = string.Empty;
    private bool _canRepairWorkspace;
    private string? _preservationDirectory;
    public string RecoveryMessage { get => _recoveryMessage; private set { if (SetProperty(ref _recoveryMessage, value)) OnPropertyChanged(nameof(HasRecoveryMessage)); } }
    public string RecoveryDetails { get => _recoveryDetails; private set => SetProperty(ref _recoveryDetails, value); }
    public bool HasRecoveryMessage => RecoveryMessage.Length > 0;
    public bool CanRepairWorkspace { get => _canRepairWorkspace; private set => SetProperty(ref _canRepairWorkspace, value); }
    public IAsyncRelayCommand RepairWorkspaceCommand { get; private set; } = null!;
    public IAsyncRelayCommand RetryRecoveryCommand { get; private set; } = null!;
    public IRelayCommand OpenWorkspacePreservationCommand { get; private set; } = null!;

    private void InitializeWorkspaceRepairCommands()
    {
        RepairWorkspaceCommand = new AsyncRelayCommand(RepairWorkspaceCommandAsync, () => CanExecuteOperation() && CanRepairWorkspace);
        RetryRecoveryCommand = new AsyncRelayCommand(RetryRecoveryCommandAsync, CanExecuteOperation);
        OpenWorkspacePreservationCommand = new RelayCommand(() =>
        {
            if (_preservationDirectory is not null && !ShellPathService.TryOpenPath(_preservationDirectory, out var error))
                _interactions.Notify(HistoryNotificationKind.Warning, error ?? I18n.GetString("Common_Failed"));
        });
    }

    private void ApplyRecoveryState(HistoryRuntime? runtime, DeviceLocalStateLoadResult<HistoryWorkspace>? workspace = null)
    {
        RecoveryMessage = runtime is null || runtime.Issues.Count == 0 ? string.Empty
            : I18n.GetString("History_Recovery_Readable") + "\n" + string.Join("\n", runtime.Issues.Select(i => I18n.GetString("History_Recovery_" + i.Kind)).Distinct());
        RecoveryDetails = runtime is null ? string.Empty : CloudCommandSecurity.Redact(string.Join("\n", runtime.Issues.Select(i => i.Diagnostic + (i.ErrorCode is { } code ? $" [0x{code:X8}]" : string.Empty))));
        if (workspace?.IsUnsupportedFormat == true) RecoveryMessage += "\n" + I18n.GetString("History_Recovery_UnsupportedFormat");
        CanRepairWorkspace = workspace is { IsUnsupportedFormat: false, Status: DeviceLocalStateStatus.Missing or DeviceLocalStateStatus.Corrupt };
        _preservationDirectory = runtime?.Repository.Paths.LocalStateRoot;
        RepairWorkspaceCommand.NotifyCanExecuteChanged();
    }

    private Task RepairWorkspaceCommandAsync(CancellationToken token)
        => ExecuteOperationAsync("workspace repair", async ct =>
        {
            var config = _currentConfig;
            if (config is null) return;
            try
            {
                var preview = await Task.Run(() => NativeHistoryApplicationService.PreviewWorkspaceRepairAsync(config, ct), ct);
                if (!ReferenceEquals(config, _currentConfig)) return;
                if (!preview.CanRepair)
                {
                    await RefreshCurrentHistoryAsync(ct);
                    return;
                }
                if (!await _interactions.ConfirmAsync(I18n.GetString("History_Repair_Title"),
                    I18n.Format("History_Repair_Confirm", config.Name, preview.PreservationDirectory),
                    I18n.GetString("History_Repair_Primary"), true, ct)) return;
                var result = await Task.Run(() => NativeHistoryApplicationService.RepairWorkspaceAsync(config, preview, ct), ct);
                _interactions.Notify(HistoryNotificationKind.Success, I18n.GetString(result.Rebuilt ? "History_Repair_Completed" : "History_Repair_AlreadyHealthy"));
                if (ReferenceEquals(config, _currentConfig)) await RefreshCurrentHistoryAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { ShowRecoveryFailure(ex); }
        }, token);

    private Task RetryRecoveryCommandAsync(CancellationToken token)
        => ExecuteOperationAsync("history recovery", async ct =>
        {
            var config = _currentConfig;
            if (config is null) return;
            try
            {
                await Task.Run(async () => await (await NativeHistoryApplicationService.CreateRestoreServiceAsync(config, ct)).RecoverIncompleteAsync(ct), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { ShowRecoveryFailure(ex); }
            if (ReferenceEquals(config, _currentConfig)) await RefreshCurrentHistoryAsync(ct);
        }, token);

    private void ShowRecoveryFailure(Exception ex)
    {
        RecoveryMessage = I18n.GetString("History_Recovery_Failed");
        RecoveryDetails = CloudCommandSecurity.Redact(ex.ToString());
        _interactions.Notify(HistoryNotificationKind.Warning, I18n.GetString("History_Recovery_Failed"));
    }
}

using CommunityToolkit.Mvvm.Input;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed partial class SettingsPageViewModel
{
    private CancellationTokenSource? _coordinationPolling;
    private AsyncRelayCommand? _resetCoordination;
    private bool _hasMinecraftCoordination, _canResetMinecraftCoordination;
    private string _minecraftCoordinationText = "";
    public bool HasMinecraftCoordination { get => _hasMinecraftCoordination; private set => SetProperty(ref _hasMinecraftCoordination, value); }
    public bool CanResetMinecraftCoordination
    {
        get => _canResetMinecraftCoordination;
        private set { if (SetProperty(ref _canResetMinecraftCoordination, value)) _resetCoordination?.NotifyCanExecuteChanged(); }
    }
    public string MinecraftCoordinationText { get => _minecraftCoordinationText; private set => SetProperty(ref _minecraftCoordinationText, value); }
    public IAsyncRelayCommand ResetMinecraftCoordinationCommand => _resetCoordination ??= new AsyncRelayCommand(ResetCoordinationAsync, () => CanResetMinecraftCoordination);

    private void StartCoordinationPolling()
    {
        if (_coordinationPolling is not null) return;
        _coordinationPolling = new();
        TaskObserver.Observe(PollCoordinationAsync(_coordinationPolling.Token, _presentationGeneration), nameof(SettingsPageViewModel));
    }

    private async Task PollCoordinationAsync(CancellationToken token, int generation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                BackupCoordinationStatus? status = null;
                try { status = await PluginV3CoordinationRecoveryService.GetStatusAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) { LogService.LogWarning(error.ToString(), "Minecraft coordination status"); }
                await UiDispatcherService.RunOnUiAsync(() =>
                {
                    if (!_active || token.IsCancellationRequested || generation != _presentationGeneration) return;
                    HasMinecraftCoordination = status is not null;
                    CanResetMinecraftCoordination = status?.State == BackupCoordinationState.NeedsReset;
                    MinecraftCoordinationText = I18n.GetString(status?.State switch
                    {
                        BackupCoordinationState.Busy => "SettingsPage_MinecraftCoordinationBusy",
                        BackupCoordinationState.NeedsReset => "SettingsPage_MinecraftCoordinationNeedsReset",
                        _ => "SettingsPage_MinecraftCoordinationReady"
                    });
                }).ConfigureAwait(false);
            } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { LogService.LogWarning(error.ToString(), "Minecraft coordination status"); }
    }

    private async Task ResetCoordinationAsync()
    {
        CanResetMinecraftCoordination = false;
        try
        {
            var reset = await PluginV3CoordinationRecoveryService.ResetAsync(CancellationToken.None);
            if (_active) NotificationService.ShowInfo(I18n.GetString(reset ? "SettingsPage_MinecraftCoordinationResetDone" : "SettingsPage_MinecraftCoordinationResetFailed"));
        }
        catch (Exception error)
        {
            LogService.LogWarning(error.ToString(), "Minecraft coordination reset");
            if (_active) NotificationService.ShowError(I18n.GetString("SettingsPage_MinecraftCoordinationResetFailed"));
        }
    }
}

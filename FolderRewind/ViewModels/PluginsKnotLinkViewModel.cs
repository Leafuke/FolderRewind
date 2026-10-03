using CommunityToolkit.Mvvm.Input;
using FolderRewind.Services;
using System;

namespace FolderRewind.ViewModels;

internal sealed class PluginsKnotLinkViewModel : ViewModelBase, IDisposable
{
    private readonly PluginSettingsCommandController _lifetime;
    public IAsyncRelayCommand<PluginSettingsRequest> ExecuteCommand { get; }
    public bool IsBusy => _lifetime.IsBusy;
    public PluginsKnotLinkViewModel(IPluginSettingsActions actions)
    {
        _lifetime = new(actions, ex =>
        {
            LogService.LogError(ex.Message, nameof(PluginsKnotLinkViewModel), ex);
            NotificationService.ShowError(ex.Message);
        });
        ExecuteCommand = new AsyncRelayCommand<PluginSettingsRequest>(
            (request, token) => _lifetime.ExecuteAsync(request!, token),
            request => request is not null && _lifetime.CanExecute);
        _lifetime.Changed += OnStateChanged;
    }
    private void OnStateChanged() { OnPropertyChanged(nameof(IsBusy)); ExecuteCommand.NotifyCanExecuteChanged(); }
    public void Activate() => _lifetime.Activate();
    public void Deactivate() => _lifetime.Deactivate();
    public void Dispose() { _lifetime.Changed -= OnStateChanged; _lifetime.Dispose(); }
}

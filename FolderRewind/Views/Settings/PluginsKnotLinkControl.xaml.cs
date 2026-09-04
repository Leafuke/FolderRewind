using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderRewind.Views.Settings;

public sealed partial class PluginsKnotLinkControl : UserControl
{
    public SettingsPageViewModel ViewModel { get; private set; } = null!;
    private PluginsKnotLinkViewModel? _commands;
    private bool _bindingsApplied;

    public PluginsKnotLinkControl()
    {
        InitializeComponent();
        Loaded += (_, _) => _commands?.Activate();
        Unloaded += (_, _) => _commands?.Deactivate();
    }

    public void SetViewModel(SettingsPageViewModel viewModel)
    {
        _bindingsApplied = false;
        if (_commands is not null)
        {
            _commands.PropertyChanged -= OnCommandStateChanged;
            _commands.Dispose();
        }
        ViewModel = viewModel;
        _commands = new PluginsKnotLinkViewModel(new PluginSettingsActions(viewModel, () => XamlRoot));
        _commands.PropertyChanged += OnCommandStateChanged;
        IsEnabled = true;
        Bindings.Update();
        _bindingsApplied = true;
    }

    private void OnCommandStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PluginsKnotLinkViewModel.IsBusy))
        {
            IsEnabled = _commands?.IsBusy != true;
            if (IsEnabled)
            {
                _bindingsApplied = false;
                Bindings.Update();
                _bindingsApplied = true;
            }
        }
    }

    private void Execute(PluginSettingsAction action, object? parameter = null)
    {
        var request = new PluginSettingsRequest(action, parameter);
        if (_commands?.ExecuteCommand.CanExecute(request) == true)
            _commands.ExecuteCommand.Execute(request);
    }

    private void OnOpenPluginStoreClick(object sender, RoutedEventArgs e)
    {
        Execute(PluginSettingsAction.OpenPluginStore);
    }

    private void OnManualInstallPluginClick(object sender, RoutedEventArgs e)
    {
        Execute(PluginSettingsAction.ManualInstallPlugin);
    }

    private void OnOpenPluginFolderClick(object sender, RoutedEventArgs e)
    {
        Execute(PluginSettingsAction.OpenPluginFolder);
    }

    private void OnRefreshPluginsClick(object sender, RoutedEventArgs e)
    {
        Execute(PluginSettingsAction.RefreshPlugins);
    }

    private void OnRestartSafeModeClick(object sender, RoutedEventArgs e)
    {
        Execute(PluginSettingsAction.RestartSafeMode);
    }

    private void OnPluginUninstallClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: InstalledPluginInfo plugin })
            Execute(PluginSettingsAction.PluginUninstall, plugin);
    }

    private void OnPluginDeleteDataClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: InstalledPluginInfo plugin })
            Execute(PluginSettingsAction.PluginDeleteData, plugin);
    }

    private void OnCheckPluginUpdatesClick(object sender, RoutedEventArgs e)
    {
        Execute(PluginSettingsAction.CheckPluginUpdates);
    }

    private void OnPluginUpdateClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: InstalledPluginInfo plugin })
            Execute(PluginSettingsAction.PluginUpdate, plugin);
    }

    private void OnPluginSettingsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: InstalledPluginInfo plugin })
            Execute(PluginSettingsAction.PluginSettings, plugin);
    }

    private void OnKnotLinkRestartClick(object sender, RoutedEventArgs e)
    {
        Execute(PluginSettingsAction.KnotLinkRestart);
    }

    private void OnKnotLinkTestClick(object sender, RoutedEventArgs e)
    {
        Execute(PluginSettingsAction.KnotLinkTest);
    }

    private void OnKnotLinkSendCustomClick(object sender, RoutedEventArgs e)
    {
        Execute(PluginSettingsAction.KnotLinkSendCustom);
    }

    private void OnKnotLinkStartServerClick(object sender, RoutedEventArgs e)
    {
        Execute(PluginSettingsAction.KnotLinkStartServer);
    }

    private void OnKnotLinkCheckServerUpdateClick(object sender, RoutedEventArgs e)
    {
        Execute(PluginSettingsAction.KnotLinkCheckServerUpdate);
    }

    private void OnKnotLinkUpdateServerClick(object sender, RoutedEventArgs e)
    {
        Execute(PluginSettingsAction.KnotLinkUpdateServer);
    }

    private void OnPluginsExpanderExpanded(object? sender, object e)
        => Execute(PluginSettingsAction.RefreshOnExpand);

    private void OnPluginEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { DataContext: InstalledPluginInfo plugin } toggle || plugin.IsEnabled == toggle.IsOn) return;
        Execute(PluginSettingsAction.SetPluginEnabled, new PluginEnabledEdit(plugin.Id, toggle.IsOn));
    }
    private void OnPluginsAutoCheckUpdatesToggled(object sender, RoutedEventArgs e)
    {
        if (_bindingsApplied && sender is ToggleSwitch toggle) ViewModel.HandlePluginsAutoCheckUpdatesToggled(toggle.IsOn);
    }
    private void OnKnotLinkToggled(object sender, RoutedEventArgs e)
    {
        if (_bindingsApplied && sender is ToggleSwitch toggle) Execute(PluginSettingsAction.ToggleKnotLink, toggle.IsOn);
    }
    private void OnKnotLinkAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_bindingsApplied && sender is ToggleSwitch toggle) ViewModel.HandleKnotLinkAutoStartToggled(toggle.IsOn);
    }
}

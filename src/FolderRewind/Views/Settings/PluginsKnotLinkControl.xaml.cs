using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderRewind.Views.Settings;

public sealed partial class PluginsKnotLinkControl : UserControl
{
    private readonly SettingsPresentationLifetime _presentation = new();
    internal DeferredSettingsSection PluginsSection { get; } = new();
    internal DeferredSettingsSection KnotLinkSection { get; } = new();
    internal DeferredSettingsSection CommandsSection { get; } = new();

    private void UpdatePresentationBindings() => _presentation.UpdateBindings(Bindings.Update);

    internal void DeactivatePresentation()
    {
        _presentation.Deactivate();
        _commands?.Deactivate();
        Bindings.StopTracking();
    }

    private void EnsureSection(DeferredSettingsSection section, SettingsSection kind)
    {
        if (!_presentation.IsActive) return;
        System.Threading.Tasks.Task? initialization = null;
        _presentation.UpdateBindings(() =>
        {
            initialization = ViewModel.InitializeSectionAsync(kind);
            section.Create();
            Bindings.Update();
            ApplyDeferredCardStyles();
        });
        TaskObserver.Observe(initialization ?? System.Threading.Tasks.Task.CompletedTask, nameof(PluginsKnotLinkControl));
    }


    private void ApplyDeferredCardStyles()
    {
        SettingsDeferredContent.ApplyCardStyles(CommandsBody, CommandsExpander);
        SettingsDeferredContent.ApplyCardStyles(KnotLinkBody, KnotLinkExpander);
        SettingsDeferredContent.ApplyCardStyles(PluginsBody, PluginsExpander);
    }

    private void RefreshCreatedSections()
    {
        if (PluginsSection.IsCreated) EnsureSection(PluginsSection, SettingsSection.Plugins);
        if (KnotLinkSection.IsCreated) EnsureSection(KnotLinkSection, SettingsSection.KnotLink);
        if (CommandsSection.IsCreated) EnsureSection(CommandsSection, SettingsSection.KnotLink);
    }

    private void OnPluginsExpanded(object? sender, object e) => EnsureSection(PluginsSection, SettingsSection.Plugins);

    private void OnKnotLinkExpanded(object? sender, object e) => EnsureSection(KnotLinkSection, SettingsSection.KnotLink);

    private void OnCommandsExpanded(object? sender, object e) => EnsureSection(CommandsSection, SettingsSection.KnotLink);

    public SettingsPageViewModel ViewModel { get; private set; } = null!;
    private PluginsKnotLinkViewModel? _commands;
    private bool _bindingsApplied;
    public void ShowRepairTarget(bool knotLink)
    {
        EnsureSection(knotLink ? KnotLinkSection : PluginsSection, knotLink ? SettingsSection.KnotLink : SettingsSection.Plugins);
        var target = knotLink ? KnotLinkExpander : PluginsExpander;
        target.IsExpanded = true;
        target.StartBringIntoView();
    }

    public PluginsKnotLinkControl()
    {
        InitializeComponent();
        Unloaded += (_, _) => DeactivatePresentation();
    }

    public void SetViewModel(SettingsPageViewModel viewModel)
    {
        _bindingsApplied = false;
        if (ReferenceEquals(ViewModel, viewModel) && _commands is not null)
        {
            _presentation.Activate();
            _commands.Activate();
            UpdatePresentationBindings();
            RefreshCreatedSections();
            _bindingsApplied = true;
            return;
        }
        if (_commands is not null)
        {
            _commands.PropertyChanged -= OnCommandStateChanged;
            _commands.Dispose();
        }
        ViewModel = viewModel;
        _presentation.Activate();
        _commands = new PluginsKnotLinkViewModel(new PluginSettingsActions(viewModel, () => XamlRoot));
        _commands.PropertyChanged += OnCommandStateChanged;
        IsEnabled = true;
        UpdatePresentationBindings();
        _bindingsApplied = true;
        RefreshCreatedSections();
    }

    private void OnCommandStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_presentation.CanEdit) return;
        if (e.PropertyName == nameof(PluginsKnotLinkViewModel.IsBusy))
        {
            IsEnabled = _commands?.IsBusy != true;
            if (IsEnabled && _presentation.IsActive)
            {
                _bindingsApplied = false;
                UpdatePresentationBindings();
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


    private void OnPluginEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (!_presentation.CanEdit) return;
        if (sender is not ToggleSwitch { DataContext: InstalledPluginInfo plugin } toggle || plugin.IsEnabled == toggle.IsOn) return;
        Execute(PluginSettingsAction.SetPluginEnabled, new PluginEnabledEdit(plugin.Id, toggle.IsOn));
    }
    private void OnPluginsAutoCheckUpdatesToggled(object sender, RoutedEventArgs e)
    {
        if (!_presentation.CanEdit) return;
        if (_bindingsApplied && sender is ToggleSwitch toggle) ViewModel.HandlePluginsAutoCheckUpdatesToggled(toggle.IsOn);
    }
    private void OnKnotLinkToggled(object sender, RoutedEventArgs e)
    {
        if (!_presentation.CanEdit) return;
        if (_bindingsApplied && sender is ToggleSwitch toggle) Execute(PluginSettingsAction.ToggleKnotLink, toggle.IsOn);
    }
    private void OnKnotLinkAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (!_presentation.CanEdit) return;
        if (_bindingsApplied && sender is ToggleSwitch toggle) ViewModel.HandleKnotLinkAutoStartToggled(toggle.IsOn);
    }
}

using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FolderRewind.Views;

public sealed partial class SettingsPage : Page
{
    private readonly SettingsPageViewModel _viewModel = new();
    private string? _pendingRepairTarget;
    private FolderRewind.Models.ProductSettingsNavigationParameter? _pendingProductTarget;
    private bool _active;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (!_active) return;
            ActivatePresentation();
            ApplyPendingRepairTarget();
        };
        Unloaded += (_, _) => DeactivatePresentation();
    }

    private void ActivatePresentation()
    {
        _viewModel.Activate();
        PresetControl.SetViewModel(_viewModel);
        CoreBehaviorControl.SetViewModel(_viewModel);
        AppearanceLayoutControl.SetViewModel(_viewModel);
        RuntimeEnvControl.SetViewModel(_viewModel);
        DiagnosticsControl.SetViewModel(_viewModel);
        DataManagementControl.SetViewModel(_viewModel);
        PluginsKnotLinkControl.SetViewModel(_viewModel);
        AboutControl.SetViewModel(_viewModel);
    }

    private void DeactivatePresentation()
    {
        _active = false;
        PresetControl.DeactivatePresentation();
        CoreBehaviorControl.DeactivatePresentation();
        AppearanceLayoutControl.DeactivatePresentation();
        RuntimeEnvControl.DeactivatePresentation();
        DiagnosticsControl.DeactivatePresentation();
        DataManagementControl.DeactivatePresentation();
        PluginsKnotLinkControl.DeactivatePresentation();
        AboutControl.DeactivatePresentation();
        _viewModel.Deactivate();
        _viewModel.SaveIfDirty();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _active = true;
        _pendingRepairTarget = e.Parameter as string;
        _pendingProductTarget = e.Parameter as FolderRewind.Models.ProductSettingsNavigationParameter;
        if (IsLoaded)
        {
            ActivatePresentation();
            DispatcherQueue.TryEnqueue(ApplyPendingRepairTarget);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        DeactivatePresentation();
        base.OnNavigatedFrom(e);
    }

    internal void ShowProductTarget(FolderRewind.Models.ProductSettingsNavigationParameter target)
    {
        _active = true;
        _pendingProductTarget = target;
        ActivatePresentation();
        DispatcherQueue.TryEnqueue(ApplyPendingRepairTarget);
    }

    private void ApplyPendingRepairTarget()
    {
        if (!_active) return;
        if (_pendingProductTarget is { } product)
        { _pendingProductTarget = null; AboutControl.ShowProductTarget(product); }
        var target = _pendingRepairTarget;
        _pendingRepairTarget = null;
        switch (target)
        {
            case NavigationService.SettingsOpenListTarget: RuntimeEnvControl.ShowOpenList(); break;
            case NavigationService.SettingsPluginsTarget: PluginsKnotLinkControl.ShowRepairTarget(false); break;
            case NavigationService.SettingsKnotLinkTarget: PluginsKnotLinkControl.ShowRepairTarget(true); break;
            case NavigationService.SettingsMinecraftPresetTarget: PresetControl.ShowPresets(); break;
        }
    }
}

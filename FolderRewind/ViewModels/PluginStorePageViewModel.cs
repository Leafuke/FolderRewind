using CommunityToolkit.Mvvm.Input;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed class PluginStorePageViewModel : ViewModelBase
{
    private readonly PluginStoreOperationController _operations;
    private readonly IRelayCommand[] _commands;
    private bool _isActive;
    private bool _hasAutoLoaded;
    private string _statusMessage = string.Empty;
    private string _releaseSummary = string.Empty;
    private string? _pendingEnablePluginId;
    private bool _canEnableManualNow;

    public PluginStorePageViewModel()
    {
        _operations = new(ReportUnexpectedError);
        LoadCommand = PageCommand(LoadAssetsAsync);
        InstallCommand = new AsyncRelayCommand<PluginStoreAssetItem>(
            item => item is null ? Task.CompletedTask : RunAsync((ct, current) => InstallAsync(item, ct, current), item),
            item => item is not null && CanLoad && item.CanInstall);
        EnableCommand = new AsyncRelayCommand<PluginStoreAssetItem>(
            item => item is null || !item.CanEnableNow ? Task.CompletedTask : RunAsync((ct, current) => EnableInstalledAsync(item, ct, current), item),
            item => item is not null && CanLoad && item.CanEnableNow && item.CanInstall);
        InstallManualCommand = PageCommand(InstallManualAsync);
        EnableManualCommand = new AsyncRelayCommand(
            () => CanEnableManualNow ? RunAsync(EnableManualAsync) : Task.CompletedTask,
            () => CanLoad && CanEnableManualNow);
        _commands = new IRelayCommand[] { LoadCommand, InstallCommand, EnableCommand, InstallManualCommand, EnableManualCommand };
        _operations.StateChanged += OnOperationStateChanged;
        Assets.CollectionChanged += OnAssetsCollectionChanged;
    }

    public ObservableCollection<PluginStoreAssetItem> Assets { get; } = new();
    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand<PluginStoreAssetItem> InstallCommand { get; }
    public IAsyncRelayCommand<PluginStoreAssetItem> EnableCommand { get; }
    public IAsyncRelayCommand InstallManualCommand { get; }
    public IAsyncRelayCommand EnableManualCommand { get; }

    public bool IsLoading => _operations.IsBusy;
    public bool CanLoad => _operations.CanExecute && PluginService.IsPluginSystemEnabled();
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool HasReleaseSummary => !string.IsNullOrWhiteSpace(ReleaseSummary);
    public bool IsEmptyList => !IsLoading && Assets.Count == 0;

    public bool CanEnableManualNow
    {
        get => _canEnableManualNow;
        private set
        {
            if (SetProperty(ref _canEnableManualNow, value)) EnableManualCommand.NotifyCanExecuteChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set { if (SetProperty(ref _statusMessage, value)) OnPropertyChanged(nameof(HasStatus)); }
    }

    public string ReleaseSummary
    {
        get => _releaseSummary;
        private set { if (SetProperty(ref _releaseSummary, value)) OnPropertyChanged(nameof(HasReleaseSummary)); }
    }

    public async Task ActivateAsync()
    {
        if (_isActive) return;
        _isActive = true;
        _operations.Activate();
        await EnsureInitialLoadAsync();
    }

    public void Deactivate()
    {
        if (!_isActive) return;
        _isActive = false;
        _operations.Deactivate();
    }

    public async Task EnsureInitialLoadAsync()
    {
        if (_hasAutoLoaded || !CanLoad) return;
        _hasAutoLoaded = true;
        await LoadCommand.ExecuteAsync(null);
    }

    public void CancelPendingOperations() => _operations.Cancel();

    private IAsyncRelayCommand PageCommand(Func<CancellationToken, Func<bool>, Task> action)
        => new AsyncRelayCommand(() => RunAsync(action), () => CanLoad);

    private Task RunAsync(Func<CancellationToken, Func<bool>, Task> action, PluginStoreAssetItem? item = null)
    {
        if (!PluginService.IsPluginSystemEnabled()) return Task.CompletedTask;
        return _operations.RunAsync(async (ct, isCurrent) =>
        {
            if (item is not null) item.IsBusy = true;
            try
            {
                await action(ct, isCurrent);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (isCurrent()) SetOperationStatus(item, I18n.GetString("Common_Canceled"));
            }
            catch (Exception ex)
            {
                LogService.LogError("Plugin store operation failed.", nameof(PluginStorePageViewModel), ex);
                if (isCurrent())
                {
                    SetOperationStatus(item, ex.Message);
                    NotificationService.ShowError(ex.Message, I18n.GetString("PluginStorePage_Title.Text"));
                }
            }
            finally
            {
                // Always release the old item's busy flag, even when a transaction completes after navigation.
                if (item is not null) item.IsBusy = false;
            }
        });
    }

    private async Task LoadAssetsAsync(CancellationToken ct, Func<bool> isCurrent)
    {
        Assets.Clear();
        ReleaseSummary = string.Empty;
        StatusMessage = string.Empty;
        var result = await PluginStoreService.GetOfficialCatalogAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (!isCurrent()) return;
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            StatusMessage = result.ErrorMessage;
            NotificationService.ShowError(result.ErrorMessage, I18n.GetString("PluginStorePage_Title.Text"));
            return;
        }
        ReleaseSummary = result.Summary ?? string.Empty;
        var installedIds = PluginService.InstalledPlugins.Select(p => p.Id).ToList();
        foreach (var item in result.Items)
        {
            item.IsInstalled = installedIds.Any(id => string.Equals(id, item.PluginId, StringComparison.OrdinalIgnoreCase));
            item.IsOperationAllowed = CanLoad;
            Assets.Add(item);
        }
        if (Assets.Count == 0) StatusMessage = I18n.GetString("PluginStorePage_NoAssets.Text");
    }

    private async Task InstallAsync(PluginStoreAssetItem item, CancellationToken ct, Func<bool> isCurrent)
    {
        item.Status = I18n.GetString("PluginStorePage_StatusDownloading");
        var result = await PluginStoreService.DownloadAndInstallAsync(item, ct);
        PluginService.RefreshInstalledList();
        if (!isCurrent()) return;
        if (result.Success)
        {
            item.IsInstalled = true;
            item.CanEnableNow = result.CanEnableNow;
            item.RequiresRestart = result.RequiresRestart;
        }
        ShowInstallResult(result, ct, item);
    }

    private async Task EnableInstalledAsync(PluginStoreAssetItem item, CancellationToken ct, Func<bool> isCurrent)
    {
        var result = await PluginV3PackageService.SetEnabledAsync(new PluginId(item.PluginId), enabled: true, ct);
        PluginService.RefreshInstalledList();
        if (!isCurrent()) return;
        if (ct.IsCancellationRequested && !result.Success) { item.Status = I18n.GetString("Common_Canceled"); return; }
        if (result.Success)
        {
            item.CanEnableNow = false;
            item.RequiresRestart = result.RequiresRestart;
        }
        item.Status = result.Success ? EnabledMessage(result.RequiresRestart) : PluginV3PackageService.FormatRuntimeDiagnostics(result.Diagnostics);
        ShowOutcome(item.Status, result.Success, result.RequiresRestart);
    }

    private async Task InstallManualAsync(CancellationToken ct, Func<bool> isCurrent)
    {
        // The shared gate is acquired before displaying the picker.
        var path = await MainWindowService.PickFilePathAsync(
            I18n.GetString("PluginStorePage_InstallManual.Content"), "FolderRewind.PluginV3.ManualInstall", new[] { ".frplugin" });
        ct.ThrowIfCancellationRequested();
        if (!isCurrent() || string.IsNullOrWhiteSpace(path)) return;
        var result = await PluginStoreService.InstallManualAsync(path, ct);
        PluginService.RefreshInstalledList();
        if (!isCurrent()) return;
        if (result.Success)
        {
            CanEnableManualNow = result.CanEnableNow;
            _pendingEnablePluginId = result.Operation?.RuntimeAfterOperation.PluginId.Value;
        }
        ShowInstallResult(result, ct);
    }

    private async Task EnableManualAsync(CancellationToken ct, Func<bool> isCurrent)
    {
        if (string.IsNullOrWhiteSpace(_pendingEnablePluginId)) return;
        var result = await PluginV3PackageService.SetEnabledAsync(new PluginId(_pendingEnablePluginId), enabled: true, ct);
        PluginService.RefreshInstalledList();
        if (!isCurrent()) return;
        if (ct.IsCancellationRequested && !result.Success) { StatusMessage = I18n.GetString("Common_Canceled"); return; }
        if (result.Success)
        {
            CanEnableManualNow = false;
            _pendingEnablePluginId = null;
        }
        StatusMessage = result.Success ? EnabledMessage(result.RequiresRestart) : PluginV3PackageService.FormatRuntimeDiagnostics(result.Diagnostics);
        ShowOutcome(StatusMessage, result.Success, result.RequiresRestart);
    }

    private void ShowInstallResult(PluginStoreInstallResult result, CancellationToken ct, PluginStoreAssetItem? item = null)
    {
        if (ct.IsCancellationRequested && !result.Success)
        {
            SetOperationStatus(item, I18n.GetString("Common_Canceled"));
            return;
        }
        SetOperationStatus(item, result.Message);
        if (item is null && result.CanEnableNow) return;
        ShowOutcome(result.Message, result.Success, result.RequiresRestart);
    }

    private void SetOperationStatus(PluginStoreAssetItem? item, string message)
    {
        if (item is null) StatusMessage = message;
        else item.Status = message;
    }

    private static string EnabledMessage(bool requiresRestart)
        => I18n.GetString(requiresRestart ? "Plugins_InstallOutcomeRequiresRestartShort" : "Plugins_EnableSucceeded");

    private static void ShowOutcome(string message, bool success, bool requiresRestart)
    {
        var title = I18n.GetString("PluginStorePage_Title.Text");
        if (!success) NotificationService.ShowError(message, title);
        else if (requiresRestart) NotificationService.ShowWarning(message, title);
        else NotificationService.ShowSuccess(message, title);
    }

    private void ReportUnexpectedError(Exception error)
    {
        LogService.LogError("Plugin store lifetime failed.", nameof(PluginStorePageViewModel), error);
        if (_isActive) { StatusMessage = error.Message; NotificationService.ShowError(error.Message); }
    }

    private void OnOperationStateChanged()
    {
        foreach (var item in Assets) item.IsOperationAllowed = CanLoad;
        foreach (var command in _commands) command.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(CanLoad));
        OnPropertyChanged(nameof(IsEmptyList));
    }

    private void OnAssetsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(IsEmptyList));
}

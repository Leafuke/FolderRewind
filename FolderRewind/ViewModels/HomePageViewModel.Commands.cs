using CommunityToolkit.Mvvm.Input;
using FolderRewind.History.Application;
using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Discovery;
using FolderRewind.Services.Plugins;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ResourceLoader = FolderRewind.Services.AppResourceLoader;

namespace FolderRewind.ViewModels;

public sealed partial class HomePageViewModel
{
    private IHomeInteractionService _interactions = null!;
    private int _operationBusy;
    private readonly SemaphoreSlim _sortGate = new(1, 1);
    private CancellationTokenSource _pageLifetime = new();
    private bool _formBusy;

    internal CancellationToken InteractionCancellationToken => _pageLifetime.Token;

    internal sealed record CreateConfigRequest(
        string Name,
        string IconGlyph,
        PluginConfigKindOption ConfigKind,
        bool CreatePluginBatch);

    internal sealed record CreateConfigFromTemplateRequest(
        BackupPreset Template,
        string ConfigName,
        PluginConfigKindOption? ConfigKind);

    public IAsyncRelayCommand<ManagedFolder> QuickBackupCommand { get; private set; } = null!;
    public IAsyncRelayCommand<BackupConfig> BackupAllCommand { get; private set; } = null!;
    public IRelayCommand<BackupConfig> OpenDestinationCommand { get; private set; } = null!;
    public IAsyncRelayCommand<BackupConfig> DeleteConfigCommand { get; private set; } = null!;
    public IAsyncRelayCommand<string> ChangeSortModeCommand { get; private set; } = null!;
    internal IAsyncRelayCommand<CreateConfigRequest> CreateConfigCommand { get; private set; } = null!;
    internal IAsyncRelayCommand<CreateConfigFromTemplateRequest> CreateConfigFromTemplateCommand { get; private set; } = null!;
    public IRelayCommand AutoDiscoverGamesCommand { get; private set; } = null!;

    public bool IsOperationBusy => Volatile.Read(ref _operationBusy) != 0;

    private void InitializeCommands()
    {
        QuickBackupCommand = new AsyncRelayCommand<ManagedFolder>(QuickBackupCommandAsync, CanExecuteItem);
        BackupAllCommand = new AsyncRelayCommand<BackupConfig>(BackupAllCommandAsync, CanExecuteItem);
        OpenDestinationCommand = new RelayCommand<BackupConfig>(OpenDestinationCommandExecute, CanExecuteItem);
        DeleteConfigCommand = new AsyncRelayCommand<BackupConfig>(DeleteConfigCommandAsync, CanExecuteItem);
        ChangeSortModeCommand = new AsyncRelayCommand<string>(ChangeSortModeCommandAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        CreateConfigCommand = new AsyncRelayCommand<CreateConfigRequest>(CreateConfigCommandAsync, CanExecuteItem);
        CreateConfigFromTemplateCommand = new AsyncRelayCommand<CreateConfigFromTemplateRequest>(CreateConfigFromTemplateCommandAsync, CanExecuteItem);
        AutoDiscoverGamesCommand = new RelayCommand(() => _interactions.NavigateToGameDiscovery());
    }

    internal IReadOnlyList<PluginConfigKindOption> GetConfigKinds()
    {
        PluginService.Initialize();
        return PluginService.GetAllSupportedConfigKinds(includeEncrypted: true).ToArray();
    }

    internal IReadOnlyList<BackupPreset> GetTemplates()
        => BackupPresetService.GetTemplates()
            .OrderBy(template => template.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    internal IReadOnlyList<string> GetMissingRequiredPluginIds(BackupPreset template)
        => BackupPresetService.GetMissingRequiredPluginIds(template);

    internal PluginBatchProviderAvailability GetPluginBatchAvailability(string? pluginId)
        => GameDiscoveryProviderFactory.GetPluginBatchAvailability(pluginId);

    internal async Task<HomeOfficialTemplateImportResult> PickAndImportOfficialTemplateAsync(
        string searchText,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _interactions.PickAndImportOfficialTemplateAsync(searchText, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, true, string.Empty, null);
        }
        catch (Exception ex)
        {
            LogService.LogError("[HomePageViewModel] Official template import failed.", nameof(HomePageViewModel), ex);
            return new(false, false, ex.Message, null);
        }
    }

    private Task QuickBackupCommandAsync(ManagedFolder? folder, CancellationToken cancellationToken)
        => folder is null
            ? Task.CompletedTask
            : ExecuteOperationAsync(
                "quick backup",
                async token =>
                {
                    token.ThrowIfCancellationRequested();
                    await BackupFolderAsync(folder, "HomePage Quick Backup");
                },
                cancellationToken);

    private Task BackupAllCommandAsync(BackupConfig? config, CancellationToken cancellationToken)
        => config is null
            ? Task.CompletedTask
            : ExecuteOperationAsync(
                "configuration backup",
                async token =>
                {
                    if (config.SourceFolders.Count == 0)
                    {
                        var resources = ResourceLoader.GetForViewIndependentUse();
                        await _interactions.ShowMessageAsync(
                            resources.GetString("HomePage_ContextMenu_NoFolders_Title"),
                            resources.GetString("HomePage_ContextMenu_NoFolders_Content"),
                            token);
                        return;
                    }

                    token.ThrowIfCancellationRequested();
                    await BackupAllFoldersAsync(config, "HomePage Batch Backup");
                },
                cancellationToken);

    private void OpenDestinationCommandExecute(BackupConfig? config)
    {
        if (config is null)
        {
            return;
        }

        TryOpenDestination(config);
    }

    private Task DeleteConfigCommandAsync(BackupConfig? config, CancellationToken cancellationToken)
        => config is null
            ? Task.CompletedTask
            : ExecuteOperationAsync(
                "configuration delete",
                async token =>
                {
                    if (!await _interactions.ConfirmConfigDeletionAsync(config.Name, token))
                    {
                        return;
                    }

                    var configs = ConfigService.CurrentConfig?.BackupConfigs;
                    if (configs is null)
                    {
                        return;
                    }

                    var index = configs.IndexOf(config);
                    if (index < 0)
                    {
                        return;
                    }

                    await ConfigEditTransaction.ApplyAsync(
                        () => configs.RemoveAt(index),
                        () =>
                        {
                            if (!configs.Contains(config)) configs.Insert(Math.Min(index, configs.Count), config);
                        },
                        () => ConfigService.SaveAsync(),
                        I18n.GetString("Common_Failed"),
                        token);

                    await NativeHistoryCoreGateway.DetachActiveConfigAsync(config.Id);
                },
                cancellationToken);

    private async Task ChangeSortModeCommandAsync(string? mode, CancellationToken cancellationToken)
    {
        if (mode is not ("NameAsc" or "NameDesc" or "LastBackupDesc" or "LastModifiedDesc"))
        {
            return;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InteractionCancellationToken);
        try
        {
            await _sortGate.WaitAsync(lifetime.Token);
            try
            {
                var settings = Settings;
                if (settings is null || settings.HomeSortMode == mode) return;
                var previous = settings.HomeSortMode;
                await ConfigEditTransaction.ApplyAsync(
                    () => settings.HomeSortMode = mode,
                    () =>
                    {
                        if (settings.HomeSortMode == mode) settings.HomeSortMode = previous;
                    },
                    () => ConfigService.SaveAsync(),
                    I18n.GetString("Common_Failed"),
                    lifetime.Token);
                RefreshConfigsView();
            }
            finally
            {
                _sortGate.Release();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportInteractionFailure(ex);
        }
        finally
        {
            OnPropertyChanged(nameof(CurrentSortMode));
        }
    }

    private Task CreateConfigCommandAsync(CreateConfigRequest? request, CancellationToken cancellationToken)
        => request is null
            ? Task.CompletedTask
            : ExecuteOperationAsync(
                "configuration create",
                token => CreateConfigCoreAsync(request, token),
                cancellationToken);

    private async Task CreateConfigCoreAsync(CreateConfigRequest request, CancellationToken cancellationToken)
    {
        if (request.CreatePluginBatch)
        {
            var availability = GetPluginBatchAvailability(request.ConfigKind.RequiredPluginId);
            if (!availability.IsAvailable)
            {
                await _interactions.ShowMessageAsync(
                    ResourceLoader.GetForViewIndependentUse().GetString("HomePage_PluginBatchCreateFailedTitle"),
                    availability.Message,
                    cancellationToken);
                return;
            }

            var root = await _interactions.PickPluginBatchRootAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(root))
            {
                _interactions.NavigateToGameDiscovery(
                    GameDiscoveryNavigationParameter.ForPluginBatch(
                        request.ConfigKind.RequiredPluginId,
                        request.ConfigKind.CreateReference(),
                        root));
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return;
        }

        var password = request.ConfigKind.IsEncrypted
            ? await _interactions.RequestEncryptionPasswordAsync(cancellationToken)
            : null;
        if (request.ConfigKind.IsEncrypted && password is null)
        {
            return;
        }

        var resources = ResourceLoader.GetForViewIndependentUse();
        var config = new BackupConfig
        {
            Name = request.Name.Trim(),
            IconGlyph = request.IconGlyph,
            IsEncrypted = request.ConfigKind.IsEncrypted,
            DestinationPath = ConfigService.BuildDefaultDestinationPath(request.Name.Trim()),
            SummaryText = resources.GetString("HomePage_NewConfigSummary"),
            Cloud = new CloudSettings
            {
                RemoteBasePath = ConfigService.GetRecommendedDefaultCloudRemoteBasePath()
            }
        };
        PluginService.ApplyConfigKind(config, request.ConfigKind);
        await AddConfigAsync(config, password, cancellationToken);
    }

    private Task CreateConfigFromTemplateCommandAsync(
        CreateConfigFromTemplateRequest? request,
        CancellationToken cancellationToken)
        => request is null
            ? Task.CompletedTask
            : ExecuteOperationAsync(
                "template configuration create",
                token => CreateConfigFromTemplateCoreAsync(request, token),
                cancellationToken);

    private async Task CreateConfigFromTemplateCoreAsync(
        CreateConfigFromTemplateRequest request,
        CancellationToken cancellationToken)
    {
        var mode = BackupPresetApplicationClassifier.Classify(request.Template);
        if (mode == BackupPresetApplicationMode.ProviderTargeted)
        {
            _interactions.NavigateToGameDiscovery(
                GameDiscoveryNavigationParameter.ForPreset(request.Template.ShareId, request.ConfigName));
            return;
        }
        if (mode == BackupPresetApplicationMode.Invalid)
        {
            await _interactions.ShowMessageAsync(
                I18n.GetString("Template_CreateFrom_Home_Title"),
                I18n.GetString("Template_CreateFrom_Home_InvalidPreset"),
                cancellationToken);
            return;
        }

        var result = BackupPresetService.CreateConfigFromTemplate(
            request.Template,
            request.ConfigName,
            request.ConfigKind);
        if (!result.Success || result.Config is null)
        {
            await _interactions.ShowMessageAsync(
                I18n.GetString("Template_CreateFrom_Home_Title"),
                result.Message,
                cancellationToken);
            return;
        }

        if (request.ConfigKind is not null)
        {
            PluginService.ApplyConfigKind(result.Config, request.ConfigKind);
        }
        var folders = await _interactions.ConfirmTemplateFoldersAsync(
            request.Template,
            result.FolderCandidates,
            cancellationToken);
        if (folders is null)
        {
            return;
        }
        foreach (var folder in folders)
        {
            result.Config.SourceFolders.Add(folder);
        }

        var password = result.Config.IsEncrypted
            ? await _interactions.RequestEncryptionPasswordAsync(cancellationToken)
            : null;
        if (result.Config.IsEncrypted && password is null)
        {
            return;
        }

        result.Config.SummaryText = ResourceLoader.GetForViewIndependentUse()
            .GetString("HomePage_NewConfigSummary");
        await AddConfigAsync(result.Config, password, cancellationToken, navigate: false);
        cancellationToken.ThrowIfCancellationRequested();

        var message = BuildTemplateCreationMessage(result, result.Config.SourceFolders.Count);
        if (!string.IsNullOrWhiteSpace(message))
        {
            await _interactions.ShowMessageAsync(string.Empty, message, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        _interactions.NavigateToManager(result.Config.Id);
    }

    private async Task AddConfigAsync(
        BackupConfig config,
        string? password,
        CancellationToken cancellationToken,
        bool navigate = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configs = ConfigService.CurrentConfig.BackupConfigs;
        // Store credentials before exposing an encrypted config; history initialization may be slow.
        if (config.IsEncrypted && !string.IsNullOrEmpty(password))
        {
            EncryptionService.StorePassword(config.Id, password);
            if (!EncryptionService.VerifyPassword(config.Id, password))
                throw new InvalidOperationException(I18n.GetString("Common_Failed"));
        }
        try
        {
            await ConfigEditTransaction.ApplyAsync(
                () => configs.Add(config),
                () => configs.Remove(config),
                () => ConfigService.SaveAsync(),
                I18n.GetString("Common_Failed"));
        }
        catch (Exception ex)
        {
            // If compensation also failed, a background snapshot may still reference the
            // encrypted config. Retain its credential rather than make that config unreadable.
            if (config.IsEncrypted && ex is not ConfigEditRollbackException)
                EncryptionService.RemovePassword(config.Id);
            throw;
        }
        try
        {
            _ = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return; // The config is durable; leaving the page only cancels the UI continuation.
        }
        catch (Exception ex)
        {
            var message = I18n.Format("History_NativeInitializationFailed", config.Name, ex.Message);
            LogService.LogError(message, nameof(HomePageViewModel), ex);
            _interactions.NotifyError(message);
        }

        if (navigate && !cancellationToken.IsCancellationRequested)
        {
            _interactions.NavigateToManager(config.Id);
        }
    }

    private static string BuildTemplateCreationMessage(
        BackupPresetService.CreateConfigFromTemplateResult result,
        int selectedFolderCount)
        => result.FolderCandidates.Count == 0
            ? result.Message
            : I18n.Format(
                "Template_CreateFrom_Home_SelectionSummary",
                selectedFolderCount.ToString(CultureInfo.CurrentCulture),
                result.FolderCandidates.Count.ToString(CultureInfo.CurrentCulture));

    private bool CanExecuteItem<T>(T? item) where T : class
        => item is not null && !IsOperationBusy;

    private async Task ExecuteOperationAsync(
        string operation,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _operationBusy, 1, 0) != 0)
        {
            return;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InteractionCancellationToken);
        NotifyCommandStateChanged();
        OnPropertyChanged(nameof(IsOperationBusy));
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            await action(lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogService.LogError(
                $"[HomePageViewModel] {operation} failed: {ex.Message}",
                nameof(HomePageViewModel),
                ex);
            _interactions.NotifyError(ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _operationBusy, 0);
            OnPropertyChanged(nameof(IsOperationBusy));
            NotifyCommandStateChanged();
        }
    }

    private void NotifyCommandStateChanged()
    {
        QuickBackupCommand.NotifyCanExecuteChanged();
        BackupAllCommand.NotifyCanExecuteChanged();
        OpenDestinationCommand.NotifyCanExecuteChanged();
        DeleteConfigCommand.NotifyCanExecuteChanged();
        CreateConfigCommand.NotifyCanExecuteChanged();
        CreateConfigFromTemplateCommand.NotifyCanExecuteChanged();
    }

    internal void ReportInteractionFailure(Exception exception)
    {
        LogService.LogError("[HomePageViewModel] Interaction failed.", nameof(HomePageViewModel), exception);
        _interactions.NotifyError(exception.Message);
    }

    internal async Task RunFormInteractionAsync(Func<CancellationToken, Task> interaction)
    {
        if (_formBusy || IsOperationBusy) return;
        var token = InteractionCancellationToken;
        _formBusy = true;
        try
        {
            token.ThrowIfCancellationRequested();
            await interaction(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportInteractionFailure(ex);
        }
        finally
        {
            _formBusy = false;
        }
    }
}

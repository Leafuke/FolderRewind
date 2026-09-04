using FolderRewind.Models;
using FolderRewind.Services.Plugins;
using FolderRewind.ViewModels;
using FolderRewind.Views;
using FolderRewind.Views.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ResourceLoader = FolderRewind.Services.AppResourceLoader;
using PickerViewMode = Windows.Storage.Pickers.PickerViewMode;

namespace FolderRewind.Services;

internal sealed class PluginSettingsActions(SettingsPageViewModel viewModel, Func<XamlRoot?> rootProvider) : IPluginSettingsActions
{
    private SettingsPageViewModel ViewModel => viewModel;
    private XamlRoot? XamlRoot => rootProvider();
    private CancellationToken _token;
    public async Task ExecuteAsync(PluginSettingsRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _token = token;
        switch (request.Action)
        {
            case PluginSettingsAction.OpenPluginStore: await OpenPluginStoreAsync(); break;
            case PluginSettingsAction.ManualInstallPlugin: await ManualInstallPluginAsync(); break;
            case PluginSettingsAction.OpenPluginFolder: await OpenPluginFolderAsync(); break;
            case PluginSettingsAction.RefreshPlugins: await RefreshPluginsAsync(); break;
            case PluginSettingsAction.RestartSafeMode: await RestartSafeModeAsync(); break;
            case PluginSettingsAction.PluginUninstall: await PluginUninstallAsync((InstalledPluginInfo)request.Parameter!); break;
            case PluginSettingsAction.PluginDeleteData: await PluginDeleteDataAsync((InstalledPluginInfo)request.Parameter!); break;
            case PluginSettingsAction.CheckPluginUpdates: await CheckPluginUpdatesAsync(); break;
            case PluginSettingsAction.PluginUpdate: await PluginUpdateAsync((InstalledPluginInfo)request.Parameter!); break;
            case PluginSettingsAction.PluginSettings: await PluginSettingsAsync((InstalledPluginInfo)request.Parameter!); break;
            case PluginSettingsAction.KnotLinkRestart: await KnotLinkRestartAsync(); break;
            case PluginSettingsAction.KnotLinkTest: await KnotLinkTestAsync(); break;
            case PluginSettingsAction.KnotLinkSendCustom: await KnotLinkSendCustomAsync(); break;
            case PluginSettingsAction.KnotLinkStartServer: await KnotLinkStartServerAsync(); break;
            case PluginSettingsAction.KnotLinkCheckServerUpdate: await KnotLinkCheckServerUpdateAsync(); break;
            case PluginSettingsAction.KnotLinkUpdateServer: await KnotLinkUpdateServerAsync(); break;
            case PluginSettingsAction.RefreshOnExpand: await ViewModel.EnsurePluginsRefreshedAsync(); break;
            case PluginSettingsAction.SetPluginEnabled:
                var edit = (PluginEnabledEdit)request.Parameter!;
                await ViewModel.HandlePluginEnabledToggledAsync(edit.PluginId, edit.Enabled); break;
            case PluginSettingsAction.ToggleKnotLink: await ViewModel.SetKnotLinkEnabledAsync((bool)request.Parameter!, token); break;
        }
    }
    private async Task OpenPluginStoreAsync()
    {
        if (!PluginService.IsPluginSystemEnabled()) return;

        var rl = ResourceLoader.GetForViewIndependentUse();

        var dialog = new ContentDialog
        {
            Title = rl.GetString("Plugins_StoreDialogTitle"),
            CloseButtonText = rl.GetString("Common_Close"),
            XamlRoot = XamlRoot,
            DefaultButton = ContentDialogButton.Close,
            Content = new Frame()
        };

        if (dialog.Content is Frame frame)
        {
            frame.Navigate(typeof(PluginStorePage));
        }

        ThemeService.ApplyThemeToDialog(dialog);
        await AppDialogService.Default.ShowCustomAsync(dialog, XamlRoot, _token);
    }

    private async Task ManualInstallPluginAsync()
    {
        if (!PluginService.IsPluginSystemEnabled()) return;

        var rl = ResourceLoader.GetForViewIndependentUse();

        var filePath = await MainWindowService.PickFilePathAsync(
            string.Empty,
            "FolderRewind.Settings.Plugins.ManualInstall",
            new[] { ".frplugin" },
            MainWindowService.SuggestedPickerLocation.Downloads,
            viewMode: PickerViewMode.List);
        if (string.IsNullOrWhiteSpace(filePath)) return;

        _token.ThrowIfCancellationRequested();
        var res = await PluginStoreService.InstallManualAsync(filePath);

        var msg = new AppDialogRequest
        {
            Title = !res.Success
                ? rl.GetString("Common_Failed")
                : res.RequiresRestart
                    ? rl.GetString("Notification_Warning_Title")
                    : rl.GetString("Common_Done"),
            Content = res.Message,
            PrimaryButtonText = res.CanEnableNow
                ? rl.GetString("Plugins_EnableNowButton")
                : rl.GetString("Common_Ok"),
            CloseButtonText = res.CanEnableNow
                ? rl.GetString("Plugins_EnableLaterButton")
                : string.Empty
        };
        if (await AppDialogService.Default.ShowRequestAsync(msg, XamlRoot, _token) == ContentDialogResult.Primary && res.CanEnableNow)
        {
            try
            {
                var enabled = await FolderRewind.Services.Plugins.V3.PluginV3PackageService.SetEnabledAsync(
                    new FolderRewind.Plugin.Abstractions.PluginId(res.Operation!.RuntimeAfterOperation.PluginId.Value),
                    enabled: true);
                if (!enabled.Success)
                    NotificationService.ShowError(
                        FolderRewind.Services.Plugins.V3.PluginV3PackageService.FormatRuntimeDiagnostics(
                            enabled.Diagnostics));
                else if (enabled.RequiresRestart)
                    NotificationService.ShowWarning(I18n.GetString("Plugins_RuntimeRequiresRestart"));
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                NotificationService.ShowError(ex.Message);
            }
        }
        else if (res.Success && res.RequiresRestart)
        {
            NotificationService.ShowWarning(res.Message, rl.GetString("Plugins_StoreDialogTitle"));
        }

        PluginService.RefreshInstalledList();
    }

    private async Task OpenPluginFolderAsync()
    {
        PluginService.OpenPluginFolder();
        await Task.CompletedTask;
    }

    private async Task RefreshPluginsAsync()
    {
        PluginService.RefreshRuntimeUi();
        await Task.CompletedTask;
    }

    private async Task RestartSafeModeAsync()
    {
        var rl = ResourceLoader.GetForViewIndependentUse();
        if (!await AppDialogService.Default.ConfirmAsync(
                rl.GetString("Plugins_RestartSafeModeTitle"),
                rl.GetString("Plugins_RestartSafeModeConfirm"),
                rl.GetString("Plugins_RestartSafeModeButton"),
                XamlRoot,
                isDestructive: true, cancellationToken: _token)) return;

        if (PluginRuntimeModeService.TryStartSafeModeInstance(out var error))
        {
            Application.Current.Exit();
            return;
        }

        await ShowMessageAsync(
            rl.GetString("Common_Failed"),
            string.Format(rl.GetString("Plugins_RestartSafeModeFailed"), error));
    }

    private async Task PluginUninstallAsync(InstalledPluginInfo plugin)
    {

        var rl = ResourceLoader.GetForViewIndependentUse();
        FolderRewind.Services.Plugins.V3.PluginUninstallPreview preview;
        try
        {
            preview = await FolderRewind.Services.Plugins.V3.PluginV3PackageService.PreviewUninstallAsync(
                new FolderRewind.Plugin.Abstractions.PluginId(plugin.Id));
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            NotificationService.ShowError(ex.Message);
            return;
        }

        var confirmText = string.Format(rl.GetString("Plugins_UninstallConfirm"), plugin.Name, plugin.Id);
        if (preview.AffectedArtifactIds.Count > 0)
        {
            confirmText += Environment.NewLine + Environment.NewLine + I18n.Format(
                "Plugins_UninstallHistoryWarning",
                preview.AffectedArtifactIds.Count);
        }

        if (!await AppDialogService.Default.ConfirmAsync(
                rl.GetString("Plugins_UninstallTitle"),
                confirmText,
                rl.GetString("Plugins_UninstallButton"),
                XamlRoot,
                isDestructive: true, cancellationToken: _token)) return;

        (bool Success, string Message) result;
        var warning = false;
        try
        {
            var v3Id = new FolderRewind.Plugin.Abstractions.PluginId(plugin.Id);
            var uninstall = await FolderRewind.Services.Plugins.V3.PluginV3PackageService.UninstallAsync(
                v3Id,
                deleteData: false,
                confirmation: null);
            result = (
                uninstall.Outcome is FolderRewind.Plugin.Abstractions.OperationOutcome.Success
                    or FolderRewind.Plugin.Abstractions.OperationOutcome.SuccessWithWarnings,
                uninstall.Outcome == FolderRewind.Plugin.Abstractions.OperationOutcome.Success
                    ? I18n.Format(
                        "Plugins_UninstallPreservedResult",
                        preview.SettingsCount,
                        preview.ProviderStateLocationCount,
                        preview.DataPath)
                    : uninstall.Diagnostic);
            warning = uninstall.Outcome == FolderRewind.Plugin.Abstractions.OperationOutcome.SuccessWithWarnings;
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogService.LogError(ex.Message, "PluginV3Uninstall", ex);
            result = (false, ex.Message);
        }

        await ShowMessageAsync(
            !result.Success
                ? rl.GetString("Common_Failed")
                : warning
                    ? rl.GetString("Notification_Warning_Title")
                    : rl.GetString("Common_Done"),
            result.Message);

        if (warning)
        {
            NotificationService.ShowWarning(result.Message);
        }

        PluginService.RefreshInstalledList();
    }

    private async Task PluginDeleteDataAsync(InstalledPluginInfo plugin)
    {
        var pluginId = new FolderRewind.Plugin.Abstractions.PluginId(plugin.Id);
        if (!await FolderRewind.Services.Plugins.V3.PluginV3PackageService.IsInstalledAsync(pluginId))
        {
            NotificationService.ShowWarning(I18n.GetString("Plugins_DeleteDataV3Only"));
            return;
        }

        var preview = await FolderRewind.Services.Plugins.V3.PluginV3PackageService.PreviewUninstallAsync(pluginId);
        var confirmation = new TextBox
        {
            Header = I18n.Format("Plugins_DeleteDataConfirmationHeader", preview.RequiredConfirmation),
            PlaceholderText = preview.RequiredConfirmation
        };
        AutomationProperties.SetAutomationId(confirmation, "PluginDeleteDataConfirmation");
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = I18n.Format(
                "Plugins_DeleteDataPreview",
                preview.SettingsCount,
                preview.ProviderStateLocationCount,
                preview.DataPath,
                preview.AffectedArtifactIds.Count),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(confirmation);
        var dialog = new ContentDialog
        {
            Title = I18n.GetString("Plugins_DeleteDataTitle"),
            Content = content,
            PrimaryButtonText = I18n.GetString("Plugins_DeleteDataButton"),
            CloseButtonText = I18n.GetString("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        ThemeService.ApplyThemeToDialog(dialog);
        if (await AppDialogService.Default.ShowCustomAsync(dialog, XamlRoot, _token) != ContentDialogResult.Primary) return;
        _token.ThrowIfCancellationRequested();
        try
        {
            var result = await FolderRewind.Services.Plugins.V3.PluginV3PackageService.UninstallAsync(
                pluginId,
                deleteData: true,
                confirmation: confirmation.Text);
            var status = PluginSettingsOutcomePolicy.GetStatus(result.Outcome);
            if (status == SemanticStatus.Warning)
                NotificationService.ShowWarning(result.Diagnostic);
            else if (status == SemanticStatus.Success)
                NotificationService.ShowSuccess(I18n.GetString("Plugins_DeleteDataSuccess"));
            else
                NotificationService.ShowError(result.Diagnostic);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            NotificationService.ShowError(ex.Message);
        }
        PluginService.RefreshInstalledList();
    }

    private async Task CheckPluginUpdatesAsync()
    {
        var rl = ResourceLoader.GetForViewIndependentUse();

        try
        {
            await PluginService.CheckAllPluginUpdatesAsync(respectAutoCheckSetting: false);

            var hasUpdates = ViewModel.InstalledPlugins.Any(p => p.HasUpdate && !string.IsNullOrWhiteSpace(p.UpdateDownloadUrl));
            await ShowMessageAsync(
                rl.GetString("Common_Done"),
                hasUpdates
                    ? rl.GetString("PluginService_UpdatesAvailable")
                    : rl.GetString("PluginService_NoUpdatesAvailable"));
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await ShowMessageAsync(rl.GetString("Common_Failed"), ex.Message);
        }
    }

    private async Task PluginUpdateAsync(InstalledPluginInfo plugin)
    {

        var rl = ResourceLoader.GetForViewIndependentUse();

        if (string.IsNullOrWhiteSpace(plugin.UpdateDownloadUrl))
        {
            await ShowMessageAsync(rl.GetString("Common_Failed"), rl.GetString("PluginService_NoUpdateUrl"));
            return;
        }

        if (!await AppDialogService.Default.ConfirmAsync(
                rl.GetString("Plugins_UpdateTitle"),
                string.Format(rl.GetString("Plugins_UpdateConfirm"), plugin.Name, plugin.Version, plugin.LatestVersion),
                rl.GetString("Plugins_UpdateButton"),
                XamlRoot, cancellationToken: _token)) return;

        {
            var result = await PluginService.UpdatePluginFromUrlAsync(plugin);

            await ShowMessageAsync(
                !result.Success
                    ? rl.GetString("Common_Failed")
                    : result.RequiresRestart
                        ? rl.GetString("Notification_Warning_Title")
                        : rl.GetString("Common_Done"),
                result.Message);
            if (result.Success && result.RequiresRestart)
                NotificationService.ShowWarning(result.Message, rl.GetString("Plugins_StoreDialogTitle"));

            PluginService.RefreshInstalledList();
        }
    }

    private async Task PluginSettingsAsync(InstalledPluginInfo plugin)
    {

        var pluginId = new FolderRewind.Plugin.Abstractions.PluginId(plugin.Id);
        if (!await FolderRewind.Services.Plugins.V3.PluginV3PackageService.IsInstalledAsync(pluginId))
        {
            await ShowMessageAsync(
                I18n.GetString("Common_Failed"),
                I18n.GetString("Plugins_NotInstalled"));
            return;
        }

        await ShowPluginV3SettingsAsync(plugin, pluginId);
    }

    private async Task KnotLinkRestartAsync()
    {
        var initialized = await ViewModel.RestartKnotLinkServiceAsync(_token);

        await ShowMessageAsync(
            I18n.GetString("SettingsPage_KnotLink_Title"),
            initialized
                ? I18n.GetString("SettingsPage_KnotLink_RestartSuccess")
                : I18n.GetString("SettingsPage_KnotLink_RestartFailed"));
    }

    private async Task KnotLinkTestAsync()
    {
        if (!KnotLinkService.IsInitialized)
        {
            await ShowMessageAsync(
                I18n.GetString("SettingsPage_KnotLinkTest_Title"),
                I18n.GetString("SettingsPage_KnotLinkTest_NotInitialized"));
            return;
        }

        KnotLinkService.BroadcastEvent(null, "test", new Dictionary<string, string?>
        {
            ["message"] = "Hello from FolderRewind!"
        });

        await ShowMessageAsync(
            I18n.GetString("SettingsPage_KnotLinkTest_Title"),
            I18n.GetString("SettingsPage_KnotLinkTest_Broadcasted"));
    }

    private async Task KnotLinkSendCustomAsync()
    {
        if (!KnotLinkService.IsInitialized)
        {
            await ShowMessageAsync(
                I18n.GetString("SettingsPage_KnotLinkSendCustom_Title"),
                I18n.GetString("SettingsPage_KnotLinkTest_NotInitialized"));
            return;
        }

        var message = await AppDialogService.Default.RequestTextAsync(
            I18n.GetString("SettingsPage_KnotLinkSendCustom_Title"),
            I18n.GetString("SettingsPage_KnotLinkSendCustom_Placeholder"),
            placeholderText: I18n.GetString("SettingsPage_KnotLinkSendCustom_Placeholder"),
            primaryButtonText: I18n.GetString("Common_Confirm"),
            acceptsReturn: true,
            xamlRoot: XamlRoot, cancellationToken: _token);
        if (message is null) return;

        if (string.IsNullOrWhiteSpace(message))
        {
            await ShowMessageAsync(
                I18n.GetString("SettingsPage_KnotLinkSendCustom_Title"),
                I18n.GetString("SettingsPage_KnotLinkSendCustom_Empty"));
            return;
        }

        try
        {
            var response = await KnotLinkService.QueryAsync(message, 5000).WaitAsync(_token);

            await ShowMessageAsync(I18n.GetString("SettingsPage_KnotLinkSendCustom_ResultTitle"), response);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await ShowMessageAsync(I18n.GetString("Common_Failed"), ex.Message);
        }
    }

    private async Task KnotLinkStartServerAsync()
    {
        var started = await ViewModel.StartKnotLinkServerAsync(_token);
        await ShowMessageAsync(
            I18n.GetString("SettingsPage_KnotLink_Title"),
            started
                ? I18n.GetString("SettingsPage_KnotLinkServer_StartSuccess")
                : I18n.GetString("SettingsPage_KnotLinkServer_StartFailed"));
    }

    private async Task KnotLinkCheckServerUpdateAsync()
    {
        try
        {
            var info = await ViewModel.CheckKnotLinkServerUpdateAsync();
            await ShowMessageAsync(
                I18n.GetString("SettingsPage_KnotLinkCheckUpdate"),
                info?.HasUpdate == true
                    ? I18n.Format("SettingsPage_KnotLinkUpdateAvailable", info.LatestVersion)
                    : I18n.GetString("SettingsPage_KnotLinkUpToDate"));
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await ShowMessageAsync(
                I18n.GetString("SettingsPage_KnotLinkCheckUpdate"),
                I18n.Format("SettingsPage_KnotLinkUpdateServerError", ex.Message));
        }
    }

    private async Task KnotLinkUpdateServerAsync()
    {
        try
        {
            await ViewModel.DownloadAndRunKnotLinkInstallerAsync();
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await ShowMessageAsync(I18n.GetString("Common_Failed"), ex.Message);
        }
    }

    private async Task ShowPluginV3SettingsAsync(
        InstalledPluginInfo plugin,
        FolderRewind.Plugin.Abstractions.PluginId pluginId)
    {
        try
        {
            var data = await FolderRewind.Services.Plugins.V3.PluginV3PackageService
                .GetSettingsEditorDataAsync(pluginId);
            if (data == null || data.Schema.Settings.Count == 0)
            {
                await ShowMessageAsync(
                    I18n.GetString("Plugins_SettingsTitle"),
                    I18n.GetString("Plugins_NoSettings"));
                return;
            }

            var dialog = new PluginV3SettingsDialog(plugin.Name, data,
                XamlRoot ?? throw new InvalidOperationException("The settings view is no longer attached."));
            if (await AppDialogService.Default.ShowCustomAsync(dialog, XamlRoot, _token) != ContentDialogResult.Primary
                || dialog.ResultSettings is null)
            {
                return;
            }

            var apply = await FolderRewind.Services.Plugins.V3.PluginV3PackageService.ApplySettingsAsync(
                pluginId,
                dialog.ResultSettings);
            if (!apply.Success)
            {
                var diagnostics = apply.Validation.Issues.Select(issue => issue.Code).ToList();
                if (apply.Transition is not null)
                {
                    diagnostics.Add(
                        FolderRewind.Services.Plugins.V3.PluginV3PackageService.FormatRuntimeDiagnostics(
                            apply.Transition.Diagnostics));
                }
                await ShowMessageAsync(
                    I18n.GetString("Common_Failed"),
                    I18n.Format("Plugins_SettingsSaveFailed", string.Join(", ", diagnostics)));
                return;
            }

            if (apply.Transition?.RequiresRestart == true)
                NotificationService.ShowWarning(I18n.GetString("Plugins_SettingsApplyRequiresRestart"));
            else
                NotificationService.ShowSuccess(I18n.GetString("Plugins_SettingsSaved"));
            PluginService.RefreshInstalledList();
            await FolderRewind.Services.Plugins.V3.PluginV3DiscoveryService
                .RunAutoCreateAsync(pluginId);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await ShowMessageAsync(
                I18n.GetString("Common_Failed"),
                I18n.Format("Plugins_SettingsLoadFailed", ex.Message));
        }
    }

    private Task ShowMessageAsync(string title, string content)
        => AppDialogService.Default.ShowMessageAsync(title, content, XamlRoot, cancellationToken: _token);
}

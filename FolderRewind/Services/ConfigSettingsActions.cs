using FolderRewind.Models;
using FolderRewind.Views;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Windows.System;
using Microsoft.UI.Xaml.Automation;

namespace FolderRewind.Services;

internal enum ConfigSettingsAction { Browse, OpenDestination, OpenConfigFolder, OpenConfigFile, SaveAsTemplate, BrowseCloudExecutable, BrowseCloudWorkingDirectory, OpenCloudSync }

internal interface IConfigSettingsActions
{
    Task ExecuteAsync(ConfigSettingsAction action, CancellationToken token);
    Task<bool> ConfirmDeleteAsync(CancellationToken token);
}

internal sealed class ConfigSettingsActions(ConfigSettingsDialogViewModel viewModel, Func<XamlRoot?> root) : IConfigSettingsActions
{
    private ConfigSettingsDialogViewModel ViewModel => viewModel;
    private BackupConfig Config => viewModel.Config;
    private XamlRoot? XamlRoot => root();
    private CancellationToken _token;
    public async Task ExecuteAsync(ConfigSettingsAction action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _token = token;
        await ExecuteCoreAsync(action);
    }
    public Task<bool> ConfirmDeleteAsync(CancellationToken token)
        => AppDialogService.Default.ConfirmAsync(I18n.GetString("ConfigSettingsDialog_DeleteConfirmTitle"),
            I18n.GetString("ConfigSettingsDialog_DeleteConfirmContent"), I18n.GetString("Common_Delete"), XamlRoot,
            isDestructive: true, cancellationToken: token);
    private Task ExecuteCoreAsync(ConfigSettingsAction action) => action switch
    {
        ConfigSettingsAction.Browse => BrowseAsync(),
        ConfigSettingsAction.OpenDestination => OpenDestinationAsync(),
        ConfigSettingsAction.OpenConfigFolder => OpenConfigFolderAsync(),
        ConfigSettingsAction.OpenConfigFile => OpenConfigFileAsync(),
        ConfigSettingsAction.SaveAsTemplate => SaveAsTemplateAsync(),
        ConfigSettingsAction.BrowseCloudExecutable => BrowseCloudExecutableAsync(),
        ConfigSettingsAction.BrowseCloudWorkingDirectory => BrowseCloudWorkingDirectoryAsync(),
        ConfigSettingsAction.OpenCloudSync => OpenCloudSyncAsync(),
        _ => Task.CompletedTask
    };
    private async Task BrowseAsync()
    {
        var folderPath = await MainWindowService.PickFolderPathAsync(
            string.Empty,
            "FolderRewind.ConfigSettings.Destination",
            MainWindowService.SuggestedPickerLocation.ComputerFolder);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        _token.ThrowIfCancellationRequested();
        Config.DestinationPath = folderPath;
    }
    private async Task OpenDestinationAsync()
    {
        if (string.IsNullOrWhiteSpace(Config?.DestinationPath))
        {
            LogService.Log(I18n.GetString("Config_OpenDestination_Empty"));
            return;
        }

        if (!Directory.Exists(Config.DestinationPath))
        {
            LogService.Log(I18n.GetString("Config_OpenDestination_NotFound"));
            return;
        }

        if (!ShellPathService.TryOpenPath(Config.DestinationPath, out var error))
            throw new IOException(error);
        await Task.CompletedTask;
    }
    private async Task OpenConfigFolderAsync()
    {
        ConfigService.OpenConfigFolder();
        await Task.CompletedTask;
    }
    private async Task OpenConfigFileAsync()
    {
        ConfigService.OpenConfigFile();
        await Task.CompletedTask;
    }
    private async Task SaveAsTemplateAsync()
    {
        if (Config == null)
        {
            return;
        }


        var templateNameBox = new TextBox
        {
            Header = I18n.GetString("Template_SaveDialog_Name"),
            Text = string.IsNullOrWhiteSpace(Config.Name) ? I18n.GetString("Template_DefaultName") : Config.Name
        };
        var authorBox = new TextBox
        {
            Header = I18n.GetString("Template_SaveDialog_Author"),
            PlaceholderText = I18n.GetString("Template_SaveDialog_AuthorPlaceholder")
        };
        var descriptionBox = new TextBox
        {
            Header = I18n.GetString("Template_SaveDialog_Description"),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MinHeight = 96,
            MaxHeight = 200
        };

        AutomationProperties.SetAutomationId(templateNameBox, "ConfigTemplateName");
        AutomationProperties.SetAutomationId(authorBox, "ConfigTemplateAuthor");
        AutomationProperties.SetAutomationId(descriptionBox, "ConfigTemplateDescription");
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = I18n.GetString("Template_SaveDialog_Hint"),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(templateNameBox);
        panel.Children.Add(authorBox);
        panel.Children.Add(descriptionBox);

        var dialog = new AppDialogRequest
        {
            Title = I18n.GetString("Template_SaveDialog_Title"),
            Content = panel,
            PrimaryButtonText = I18n.GetString("Common_Save"),
            CloseButtonText = I18n.GetString("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await AppDialogService.Default.ShowRequestAsync(dialog, XamlRoot, _token);
        if (result == ContentDialogResult.Primary)
        {
            var createResult = BackupPresetService.UpsertTemplateFromConfig(
                Config,
                templateNameBox.Text,
                authorBox.Text,
                descriptionBox.Text);

            await AppDialogService.Default.ShowMessageAsync(
                string.Empty,
                createResult.Message,
                MainWindowService.GetXamlRoot() ?? XamlRoot, cancellationToken: _token);
        }

    }
    private async Task BrowseCloudExecutableAsync()
    {
        var filePath = await MainWindowService.PickFilePathAsync(
            string.Empty,
            "FolderRewind.ConfigSettings.CloudExecutable",
            new[] { ".exe", ".cmd", ".bat", ".ps1" },
            MainWindowService.SuggestedPickerLocation.ComputerFolder);
        if (string.IsNullOrWhiteSpace(filePath)) return;

        _token.ThrowIfCancellationRequested();
        ViewModel.CloudExecutablePathText = filePath;
        ViewModel.RefreshCloudUi();
    }
    private async Task BrowseCloudWorkingDirectoryAsync()
    {
        var folderPath = await MainWindowService.PickFolderPathAsync(
            string.Empty,
            "FolderRewind.ConfigSettings.CloudWorkingDirectory",
            MainWindowService.SuggestedPickerLocation.ComputerFolder);
        if (string.IsNullOrWhiteSpace(folderPath)) return;

        _token.ThrowIfCancellationRequested();
        ViewModel.CloudWorkingDirectoryText = folderPath;
        ViewModel.RefreshCloudUi();
    }
    private async Task OpenCloudSyncAsync()
    {

        var dialog = new ConfigCloudSyncDialog(Config)
        {
            XamlRoot = MainWindowService.GetXamlRoot() ?? XamlRoot
        };

        await AppDialogService.Default.ShowCustomAsync(dialog, XamlRoot, _token);
        ViewModel.RefreshCloudUi();
    }
}

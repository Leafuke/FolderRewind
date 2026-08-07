using FolderRewind.Models;
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
using Windows.System;

namespace FolderRewind.Views
{
    public sealed partial class ConfigSettingsDialog : ContentDialog
    {
        private async void OnBrowseClick(object sender, RoutedEventArgs e)
        {
            var folderPath = await MainWindowService.PickFolderPathAsync(
                string.Empty,
                "FolderRewind.ConfigSettings.Destination",
                MainWindowService.SuggestedPickerLocation.ComputerFolder);
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            Config.DestinationPath = folderPath;
            DestPathBox.Text = folderPath;
        }

        private void OnOpenDestinationClick(object sender, RoutedEventArgs e)
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

            OpenPathInShell(Config.DestinationPath);
        }

        private void OnOpenConfigFolderClick(object sender, RoutedEventArgs e)
        {
            ConfigService.OpenConfigFolder();
        }

        private void OnOpenConfigFileClick(object sender, RoutedEventArgs e)
        {
            ConfigService.OpenConfigFile();
        }

        private async void OnSaveAsTemplateClick(object sender, RoutedEventArgs e)
        {
            if (Config == null)
            {
                return;
            }

            // WinUI 同时只允许一个 ContentDialog，先临时隐藏当前设置对话框。
            this.Hide();
            await Task.Yield();

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

            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock
            {
                Text = I18n.GetString("Template_SaveDialog_Hint"),
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(templateNameBox);
            panel.Children.Add(authorBox);
            panel.Children.Add(descriptionBox);

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("Template_SaveDialog_Title"),
                Content = panel,
                PrimaryButtonText = I18n.GetString("Common_Save"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = MainWindowService.GetXamlRoot() ?? this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var createResult = BackupPresetService.UpsertTemplateFromConfig(
                    Config,
                    templateNameBox.Text,
                    authorBox.Text,
                    descriptionBox.Text);

                var tipDialog = new ContentDialog
                {
                    Content = createResult.Message,
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = MainWindowService.GetXamlRoot() ?? this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(tipDialog);
                await tipDialog.ShowAsync();
            }

            await this.ShowAsync();
        }

        private void OnApplyCloudTemplateClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ApplyCloudTemplate();
            UpdateCloudBindings();
        }

        private async void OnBrowseCloudExecutableClick(object sender, RoutedEventArgs e)
        {
            var filePath = await MainWindowService.PickFilePathAsync(
                string.Empty,
                "FolderRewind.ConfigSettings.CloudExecutable",
                new[] { ".exe", ".cmd", ".bat", ".ps1" },
                MainWindowService.SuggestedPickerLocation.ComputerFolder);
            if (string.IsNullOrWhiteSpace(filePath)) return;

            ViewModel.CloudExecutablePathText = filePath;
            UpdateCloudBindings();
        }

        private async void OnBrowseCloudWorkingDirectoryClick(object sender, RoutedEventArgs e)
        {
            var folderPath = await MainWindowService.PickFolderPathAsync(
                string.Empty,
                "FolderRewind.ConfigSettings.CloudWorkingDirectory",
                MainWindowService.SuggestedPickerLocation.ComputerFolder);
            if (string.IsNullOrWhiteSpace(folderPath)) return;

            ViewModel.CloudWorkingDirectoryText = folderPath;
            UpdateCloudBindings();
        }

        private async void OnOpenCloudSyncClick(object sender, RoutedEventArgs e)
        {
            this.Hide();
            await Task.Yield();

            var dialog = new ConfigCloudSyncDialog(Config)
            {
                XamlRoot = MainWindowService.GetXamlRoot() ?? this.XamlRoot
            };

            await TemplateDialogCoordinatorService.ShowAsync(dialog, this.XamlRoot);
            ViewModel.RefreshCloudUi();
            await this.ShowAsync();
        }

    }
}

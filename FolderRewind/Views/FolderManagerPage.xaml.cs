using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Hotkeys;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System;
using Windows.System;

namespace FolderRewind.Views
{
    public sealed partial class FolderManagerPage : Page
    {
        public FolderManagerPageViewModel ViewModel { get; }

        public FolderManagerPage()
        {
            ViewModel = new(new FolderManagerInteractionService(() => XamlRoot));
            this.InitializeComponent();

            ViewModel.PendingFolderSelectionRequested += TryApplyPendingSelection;
            Loaded += (_, __) => TryApplyPendingSelection();
        }

        private void OnFolderContainerContentChanging(
            ListViewBase sender,
            ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                AutomationProperties.SetName(args.ItemContainer, string.Empty);
                AutomationProperties.SetAutomationId(args.ItemContainer, string.Empty);
                return;
            }

            if (args.Item is not ManagedFolder folder)
            {
                return;
            }

            AutomationProperties.SetName(args.ItemContainer, folder.DisplayName);
            AutomationProperties.SetAutomationId(args.ItemContainer, $"FolderManagerFolder_{folder.Id}");
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            ViewModel.Activate();

            try
            {
                HotkeyManager.Invoked -= HotkeyManager_Invoked;
                HotkeyManager.Invoked += HotkeyManager_Invoked;
            }
            catch
            {
            }

            // 进入页面时刷新一次视图集合，避免安装包环境下首次绑定异常。
            ViewModel.RefreshConfigsView();

            if (e.Parameter is ManagerNavigationParameter managerParam)
            {
                ApplyManagerNavigation(managerParam);
                return;
            }

            if (e.Parameter is ManagedFolder folder)
            {
                var parent = ViewModel.FindConfigByFolderPath(folder.Path);
                if (parent != null)
                {
                    ViewModel.CurrentConfig = parent;
                    ViewModel.SetPendingFolderPath(folder.Path);
                    TryApplyPendingSelection();
                    return;
                }
            }

            if (e.Parameter is BackupConfig config)
            {
                var match = ViewModel.FindConfigById(config.Id);
                if (match != null)
                {
                    ViewModel.CurrentConfig = match;
                }
            }
            else
            {
                ViewModel.EnsureCurrentConfigSelectedFromSettings();
            }

            ViewModel.RefreshCurrentFoldersView();
            TryApplyPendingSelection();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            try
            {
                HotkeyManager.Invoked -= HotkeyManager_Invoked;
            }
            catch
            {
            }

            ViewModel.Deactivate();
        }

        private void HotkeyManager_Invoked(object? sender, HotkeyInvokedEventArgs e)
        {
            if (e.HotkeyId == HotkeyManager.Action_BackupSelectedFolder)
                DispatcherQueue.TryEnqueue(() => ExecuteCommand(ViewModel.HotkeyBackupCommand));
        }


        private void ApplyManagerNavigation(ManagerNavigationParameter param)
        {
            BackupConfig? match = null;

            if (!string.IsNullOrWhiteSpace(param.ConfigId))
            {
                match = ViewModel.FindConfigById(param.ConfigId);
            }

            if (match == null && !string.IsNullOrWhiteSpace(param.FolderPath))
            {
                match = ViewModel.FindConfigByFolderPath(param.FolderPath);
            }

            if (match != null)
            {
                ViewModel.CurrentConfig = match;
            }
            else if (ViewModel.CurrentConfig == null && ViewModel.Configs.Count > 0)
            {
                ViewModel.CurrentConfig = ViewModel.Configs[0];
            }

            if (!string.IsNullOrWhiteSpace(param.FolderPath))
            {
                ViewModel.SetPendingFolderPath(param.FolderPath);
            }

            TryApplyPendingSelection();
        }

        private void TryApplyPendingSelection()
        {
            var path = ViewModel.PendingFolderPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (ViewModel.CurrentConfig?.SourceFolders == null || FolderList == null)
            {
                return;
            }

            _ = DispatcherQueue.TryEnqueue(() =>
            {
                var target = ViewModel.FindFolderInCurrentConfig(path);
                if (target == null)
                {
                    ViewModel.ClearRememberedFolderPathIfMatches(path);
                    ViewModel.ClearPendingFolderPath();
                    return;
                }

                FolderList.SelectedItem = target;
                FolderList.ScrollIntoView(target);
                ViewModel.SetSelectedFolder(target, persistSelection: true);
                ViewModel.ClearPendingFolderPath();
            });
        }

        private void ConfigSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ConfigSelector.SelectedItem is BackupConfig config)
            {
                ViewModel.CurrentConfig = config;
            }
            else
            {
                ViewModel.CurrentConfig = null;
            }

            ViewModel.SetSelectedFolder(null, persistSelection: false);
        }

        private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ViewModel.SetSelectedFolder(FolderList.SelectedItem as ManagedFolder, persistSelection: true);
        }

        private void OnRemoveFolderClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.RemoveFolderCommand, (sender as FrameworkElement)?.DataContext);
        private void OnFavoriteToggleClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.ToggleFavoriteCommand, (sender as FrameworkElement)?.DataContext);
        private void OnPinToTopClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.PinFolderCommand, (sender as FrameworkElement)?.DataContext);
        private void OnOpenFolderClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.OpenFolderCommand, (sender as FrameworkElement)?.DataContext);
        private void OnOpenMiniWindowClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.OpenMiniWindowCommand, (sender as FrameworkElement)?.DataContext);
        private void OnShowFolderDetailsClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.ShowDetailsCommand, (sender as FrameworkElement)?.DataContext);
        private void OnRenameFolderClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.RenameFolderCommand, (sender as FrameworkElement)?.DataContext);
        private void OnEditSourceScopeClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.EditSourceScopeCommand, (sender as FrameworkElement)?.DataContext);
        private void OnChangeIconClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.ChangeIconCommand, (sender as FrameworkElement)?.DataContext);
        private void OnAddSingleFolderClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.AddSingleFolderCommand);
        private void OnAddSubFoldersClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.AddSubFoldersCommand);
        private void OnPluginDiscoverFoldersClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.PluginDiscoverCommand);
        private void OnBackupConfigClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.BackupConfigCommand);
        private void OnBackupSelectedClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.BackupSelectedCommand);
        private void OnConfigSettingsClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.ConfigSettingsCommand);
        private static void ExecuteCommand(System.Windows.Input.ICommand command, object? parameter = null)
        {
            if (command.CanExecute(parameter)) command.Execute(parameter);
        }

        private void OnDescriptionEditorKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter || sender is not TextBox input) return;
            e.Handled = true;
            SaveDescription(input);
            FolderList.Focus(FocusState.Programmatic);
        }

        private void OnDescriptionEditorLostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox input) SaveDescription(input);
        }

        private void SaveDescription(TextBox input)
        {
            if (input.DataContext is ManagedFolder folder)
                ExecuteCommand(ViewModel.SaveDescriptionCommand, new FolderManagerPageViewModel.DescriptionEdit(folder, input.Text));
        }

        private void OnHistoryClick(object sender, RoutedEventArgs e)
        {
            if (!TryGetSelectedContext(out var config, out var folder))
            {
                return;
            }

            var param = ManagerNavigationParameter.ForFolder(config.Id, folder.Path);
            _ = NavigationService.NavigateTo("History", param);
        }
        private bool TryGetSelectedContext(out BackupConfig config, out ManagedFolder folder)
        {
            config = ViewModel.CurrentConfig ?? null!;
            folder = ViewModel.SelectedFolder ?? null!;
            return config != null && folder != null;
        }
    }
}

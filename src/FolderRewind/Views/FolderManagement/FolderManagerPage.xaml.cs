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
        private bool? _compactToolbar;
        private void OnToolbarSizeChanged(object sender, SizeChangedEventArgs e)
        {
            var compact = e.NewSize.Width < 1000;
            if (_compactToolbar == compact) return;
            _compactToolbar = compact;
            var textVisibility = compact ? Visibility.Collapsed : Visibility.Visible;
            // Keep both rows in place. Reclaim space from labels rather than
            // moving commands or the comment field into extra rows.
            foreach (var label in new UIElement[] { ProjectLabel, SelectedLabel, BackupCommentLabel,
                AddFolderText, AddFolderChevron, BackupConfigText, BackupSelectedText, HistoryText })
                label.Visibility = textVisibility;
            CommentIcon.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            CommentBox.PlaceholderText = I18n.GetString(compact
                ? "FolderManager_CompactCommentPlaceholder" : "FolderManager_CommentPlaceholder.PlaceholderText");
            SelectionColumn.Width = new GridLength(compact ? 160 : 240);
            Grid.SetColumn(ConfigSelector, compact ? 0 : 1);
            Grid.SetColumnSpan(ConfigSelector, compact ? 2 : 1);
            Grid.SetColumn(SelectedFolderName, compact ? 1 : 2);
            Grid.SetColumnSpan(SelectedFolderName, compact ? 2 : 1);
            foreach (var content in new[] { AddFolderContent, BackupConfigContent, BackupSelectedContent, HistoryContent })
                content.Spacing = compact ? 0 : 8;
            foreach (var button in new[] { AddFolderButton, BackupConfigButton, BackupSelectedButton, HistoryButton })
                button.Padding = compact ? new Thickness(8, 4, 8, 4) : new Thickness(12, 4, 12, 4);
        }

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
            Bindings.Update();

            ViewModel.Activate();

            try
            {
                HotkeyManager.Invoked -= HotkeyManager_Invoked;
                HotkeyManager.Invoked += HotkeyManager_Invoked;
            }
            catch
            {
            }

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
            Bindings.StopTracking();
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
            var request = ViewModel.PendingSelection;
            if (request is null)
            {
                return;
            }

            if (ViewModel.CurrentConfig?.SourceFolders == null || FolderList == null)
            {
                return;
            }

            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (ViewModel.ApplyPendingSelection(request) && ViewModel.SelectedFolderItem is { } target)
                    FolderList.ScrollIntoView(target);
            });
        }

        private ManagedFolder? GetActionFolder(object sender)
        {
            if ((sender as FrameworkElement)?.DataContext is not ManagedFolder folder ||
                ViewModel.CurrentConfig?.SourceFolders.Contains(folder) != true) return null;
            ViewModel.SetSelectedFolder(folder, true);
            return folder;
        }

        private void OnRemoveFolderClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.RemoveFolderCommand, GetActionFolder(sender));
        private void OnFavoriteToggleClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.ToggleFavoriteCommand, GetActionFolder(sender));
        private void OnPinToTopClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.PinFolderCommand, GetActionFolder(sender));
        private void OnOpenFolderClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.OpenFolderCommand, GetActionFolder(sender));
        private void OnOpenMiniWindowClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.OpenMiniWindowCommand, GetActionFolder(sender));
        private void OnShowFolderDetailsClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.ShowDetailsCommand, GetActionFolder(sender));
        private void OnFolderActionsOpening(object sender, object args)
        {
            if (sender is not MenuFlyout flyout) return;
            var available = ViewModel.CurrentConfig is { } config && Services.Plugins.V3.PluginV3SpatialPreview.CanPreview(config);
            foreach (var item in flyout.Items)
                if (item is MenuFlyoutItem { Tag: "SpatialPreview" } preview)
                    preview.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
                else if (item is MenuFlyoutItem { Tag: "SpatialScope" } scope)
                    scope.Visibility = ViewModel.CurrentConfig is { } scopeConfig && Services.Plugins.V3.PluginV3SpatialPreview.CanUseWorkspace(scopeConfig, SpatialWorkspaceMode.Scope) ? Visibility.Visible : Visibility.Collapsed;
        }
        private void OnSpatialPreviewClick(object sender, RoutedEventArgs e)
        {
            if (GetActionFolder(sender) is { } folder && ViewModel.CurrentConfig is { } config
                && Guid.TryParse(folder.Id, out var id))
                NavigationService.NavigateTo("SpatialPreview", new SpatialPreviewNavigationParameter(config.Id, id));
        }
        private void OnSpatialScopeClick(object sender, RoutedEventArgs e)
        {
            if (GetActionFolder(sender) is { } folder && ViewModel.CurrentConfig is { } config && Guid.TryParse(folder.Id, out var id))
                NavigationService.NavigateTo("SpatialWorkspace", new SpatialWorkspaceNavigationParameter(config.Id, id, SpatialWorkspaceMode.Scope));
        }
        private void OnRenameFolderClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.RenameFolderCommand, GetActionFolder(sender));
        private void OnEditSourceScopeClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.EditSourceScopeCommand, GetActionFolder(sender));
        private void OnChangeIconClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.ChangeIconCommand, GetActionFolder(sender));
        private void OnAddSingleFolderClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.AddSingleFolderCommand);
        private void OnAddSubFoldersClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.AddSubFoldersCommand);
        private void OnPluginDiscoverFoldersClick(object sender, RoutedEventArgs e) => ExecuteCommand(ViewModel.PluginDiscoverCommand);
        private static void ExecuteCommand(System.Windows.Input.ICommand command, object? parameter = null)
        {
            if (command.CanExecute(parameter)) command.Execute(parameter);
        }

        private void OnHistoryClick(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.CanBackupSelected || ViewModel.SelectedFolder is not { } folder || ViewModel.CurrentConfig is not { } config)
            {
                return;
            }

            var param = ManagerNavigationParameter.ForFolder(config.Id, folder.Path);
            _ = NavigationService.NavigateTo("History", param);
        }

        private void OnDescriptionEditorKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (sender is not TextBox { DataContext: ManagedFolder folder } input) return;
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                input.Text = folder.Description;
                FolderList.Focus(FocusState.Programmatic);
            }
            else if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                SaveDescription(input);
                FolderList.Focus(FocusState.Programmatic);
            }
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
    }
}

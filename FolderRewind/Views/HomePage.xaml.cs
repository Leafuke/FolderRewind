using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ResourceLoader = FolderRewind.Services.AppResourceLoader;

namespace FolderRewind.Views;

public sealed partial class HomePage : Page
{
    private bool _applyingSortSelection;
    public HomePageViewModel ViewModel { get; }

    public GlobalSettings? Settings => ViewModel.Settings;

    public HomePage()
    {
        ViewModel = new HomePageViewModel(new HomeInteractionService(() => XamlRoot));
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnFavoriteContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            ClearContainerAutomationMetadata(args);
            return;
        }

        if (args.Item is ManagedFolder folder)
        {
            AutomationProperties.SetName(args.ItemContainer, folder.DisplayName);
            AutomationProperties.SetAutomationId(args.ItemContainer, $"HomeFavoriteItem_{folder.Id}");
        }
    }

    private void OnConfigContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            ClearContainerAutomationMetadata(args);
            return;
        }

        if (args.Item is BackupConfig config)
        {
            AutomationProperties.SetName(args.ItemContainer, config.Name);
            AutomationProperties.SetAutomationId(args.ItemContainer, $"HomeConfigItem_{config.Id}");
        }
    }

    private static void ClearContainerAutomationMetadata(ContainerContentChangingEventArgs args)
    {
        AutomationProperties.SetName(args.ItemContainer, string.Empty);
        AutomationProperties.SetAutomationId(args.ItemContainer, string.Empty);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Activate();
        ApplySortSelection();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Deactivate();
        base.OnNavigatedFrom(e);
    }

    private void OnFavoriteCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ManagedFolder folder })
        {
            return;
        }

        var config = ViewModel.FindParentConfig(folder);
        if (config is not null)
        {
            _ = NavigationService.NavigateTo(
                "Manager",
                ManagerNavigationParameter.ForFolder(config.Id, folder.Path));
        }
    }

    private void OnConfigCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BackupConfig config })
        {
            NavigateToConfig(config);
        }
    }

    private void OnEditConfigClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: BackupConfig config })
        {
            NavigateToConfig(config);
        }
    }

    private static void NavigateToConfig(BackupConfig config)
        => _ = NavigationService.NavigateTo("Manager", ManagerNavigationParameter.ForConfig(config.Id));

    private async void OnSortModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_applyingSortSelection && SortCombo?.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            await ViewModel.ChangeSortModeCommand.ExecuteAsync(tag);
            ApplySortSelection();
        }
    }

    private void OnQuickBackupClick(object sender, RoutedEventArgs e)
        => ExecuteItemCommand<ManagedFolder>(sender, ViewModel.QuickBackupCommand);

    private void OnBackupAllFoldersClick(object sender, RoutedEventArgs e)
        => ExecuteItemCommand<BackupConfig>(sender, ViewModel.BackupAllCommand);

    private void OnOpenDestinationClick(object sender, RoutedEventArgs e)
        => ExecuteItemCommand<BackupConfig>(sender, ViewModel.OpenDestinationCommand);

    private void OnDeleteConfigClick(object sender, RoutedEventArgs e)
        => ExecuteItemCommand<BackupConfig>(sender, ViewModel.DeleteConfigCommand);

    private static void ExecuteItemCommand<T>(object sender, System.Windows.Input.ICommand command)
        where T : class
    {
        var item = sender switch
        {
            Button { DataContext: T buttonItem } => buttonItem,
            MenuFlyoutItem { DataContext: T menuItem } => menuItem,
            _ => null
        };
        if (item is not null && command.CanExecute(item))
        {
            command.Execute(item);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ApplySortSelection();

    private void ApplySortSelection()
    {
        if (SortCombo is null || Settings is null)
        {
            return;
        }

        _applyingSortSelection = true;
        try
        {
            SortCombo.SelectedItem = SortCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is string tag
                    && string.Equals(tag, ViewModel.CurrentSortMode, StringComparison.OrdinalIgnoreCase));
            if (SortCombo.SelectedItem is null)
                SortCombo.SelectedIndex = 0;
        }
        finally
        {
            _applyingSortSelection = false;
        }
    }

    private async void OnAddConfigFromTemplateClick(object sender, RoutedEventArgs e)
        => await ViewModel.RunFormInteractionAsync(ShowTemplateFormAsync);

    private async Task ShowTemplateFormAsync(CancellationToken cancellationToken)
    {
        var resources = ResourceLoader.GetForViewIndependentUse();
        var configKinds = ViewModel.GetConfigKinds();
        var preferredTemplateId = string.Empty;
        var draftConfigName = string.Empty;
        var draftOfficialSearch = string.Empty;
        string? preferredType = null;
        var feedbackMessage = string.Empty;
        var feedbackSeverity = InfoBarSeverity.Informational;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var templates = ViewModel.GetTemplates();
            var feedbackBar = new InfoBar
            {
                IsClosable = true,
                IsOpen = !string.IsNullOrWhiteSpace(feedbackMessage),
                Message = feedbackMessage,
                Severity = feedbackSeverity
            };
            var templateCombo = new ComboBox
            {
                Header = I18n.GetString("Template_CreateFrom_Home_Template"),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetAutomationId(templateCombo, "HomeTemplatePicker");
            foreach (var template in templates)
            {
                templateCombo.Items.Add(new ComboBoxItem { Content = template.Name, Tag = template });
            }

            var templateInfo = new TextBlock { Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
            var warning = new InfoBar
            {
                Severity = InfoBarSeverity.Warning,
                IsClosable = false,
                IsOpen = false
            };
            var officialSearch = new TextBox
            {
                Header = I18n.GetString("Template_CreateFrom_Home_OfficialSearchHeader"),
                PlaceholderText = I18n.GetString("Template_CreateFrom_Home_OfficialSearchPlaceholder"),
                Text = draftOfficialSearch
            };
            var nameBox = new TextBox
            {
                Header = resources.GetString("HomePage_ConfigNameHeader"),
                PlaceholderText = resources.GetString("HomePage_ConfigNamePlaceholder"),
                Text = draftConfigName
            };
            AutomationProperties.SetAutomationId(nameBox, "HomeTemplateConfigName");
            AutomationProperties.SetAutomationId(officialSearch, "HomeOfficialTemplateSearch");
            var typeCombo = CreateConfigKindCombo(configKinds, "TemplateConfigKindPicker", resources);
            if (!string.IsNullOrWhiteSpace(preferredType))
            {
                typeCombo.SelectedItem = configKinds.FirstOrDefault(kind =>
                    string.Equals(kind.SelectionValue, preferredType, StringComparison.OrdinalIgnoreCase));
            }

            BackupPreset? GetSelectedTemplate()
                => (templateCombo.SelectedItem as ComboBoxItem)?.Tag as BackupPreset;

            void RefreshSelection()
            {
                var template = GetSelectedTemplate();
                if (template is null)
                {
                    templateInfo.Text = templates.Count == 0
                        ? I18n.GetString("Template_CreateFrom_Home_NoTemplates")
                        : string.Empty;
                    warning.IsOpen = false;
                    return;
                }

                if (string.IsNullOrWhiteSpace(nameBox.Text))
                {
                    nameBox.Text = string.IsNullOrWhiteSpace(template.DefaultConfigName)
                        ? template.Name
                        : template.DefaultConfigName;
                }
                templateInfo.Text = I18n.Format(
                    "Template_CreateFrom_Home_TemplateInfo",
                    template.Name,
                    (template.PathRules?.Count ?? 0).ToString(CultureInfo.CurrentCulture));

                var matchingKind = configKinds.FirstOrDefault(kind =>
                    string.Equals(kind.Kind.OwnerId, template.Kind.OwnerId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(kind.Kind.KindId, template.Kind.KindId, StringComparison.OrdinalIgnoreCase)
                    && kind.IsEncrypted == template.IsEncrypted);
                typeCombo.SelectedItem = matchingKind ?? configKinds.FirstOrDefault();

                var warnings = new List<string>();
                if (matchingKind is null)
                {
                    warnings.Add(I18n.Format(
                        "Template_ConfigKindUnavailable",
                        $"{template.Kind.OwnerId}/{template.Kind.KindId}"));
                }
                var missingPlugins = ViewModel.GetMissingRequiredPluginIds(template);
                if (missingPlugins.Count > 0)
                {
                    warnings.Add(I18n.Format("Template_RequiredPluginsMissing", string.Join(", ", missingPlugins)));
                }
                warning.Message = string.Join(Environment.NewLine, warnings);
                warning.IsOpen = warnings.Count > 0;
            }

            if (templates.Count > 0)
            {
                templateCombo.SelectedItem = templateCombo.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(
                        (item.Tag as BackupPreset)?.Id,
                        preferredTemplateId,
                        StringComparison.OrdinalIgnoreCase))
                    ?? templateCombo.Items[0];
            }
            templateCombo.SelectionChanged += (_, _) => RefreshSelection();
            RefreshSelection();

            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(feedbackBar);
            panel.Children.Add(new TextBlock
            {
                Text = templates.Count == 0
                    ? I18n.GetString("Template_CreateFrom_Home_NoTemplates")
                    : I18n.GetString("Template_CreateFrom_Home_LocalTemplatesHint"),
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(templateCombo);
            panel.Children.Add(templateInfo);
            panel.Children.Add(warning);
            panel.Children.Add(officialSearch);
            panel.Children.Add(nameBox);
            panel.Children.Add(typeCombo);

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("Template_CreateFrom_Home_Title"),
                Content = panel,
                PrimaryButtonText = resources.GetString("HomePage_CreateButton"),
                SecondaryButtonText = I18n.GetString("Template_CreateFrom_Home_SearchOfficial"),
                CloseButtonText = resources.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            dialog.IsPrimaryButtonEnabled = GetSelectedTemplate() is not null;
            templateCombo.SelectionChanged += (_, _) =>
                dialog.IsPrimaryButtonEnabled = GetSelectedTemplate() is not null;
            var searchFromKeyboard = false;
            officialSearch.KeyDown += (_, args) =>
            {
                if (args.Key == Windows.System.VirtualKey.Enter)
                {
                    args.Handled = true;
                    searchFromKeyboard = true;
                    dialog.Hide();
                }
            };

            var result = await AppDialogService.Default.ShowCustomAsync(dialog, XamlRoot, cancellationToken);
            draftConfigName = nameBox.Text;
            draftOfficialSearch = officialSearch.Text?.Trim() ?? string.Empty;
            preferredType = (typeCombo.SelectedItem as PluginConfigKindOption)?.SelectionValue;
            preferredTemplateId = GetSelectedTemplate()?.Id ?? preferredTemplateId;
            feedbackMessage = string.Empty;

            if (result == ContentDialogResult.Secondary || searchFromKeyboard)
            {
                var import = await ViewModel.PickAndImportOfficialTemplateAsync(draftOfficialSearch, cancellationToken);
                if (import.Canceled)
                {
                    continue;
                }

                feedbackMessage = import.Message;
                feedbackSeverity = import.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
                if (import.Success && import.ImportedTemplate is not null)
                {
                    preferredTemplateId = import.ImportedTemplate.Id;
                }
                continue;
            }
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            var selectedTemplate = GetSelectedTemplate();
            if (selectedTemplate is null)
            {
                feedbackMessage = I18n.GetString("Template_Export_TemplateNotFound");
                feedbackSeverity = InfoBarSeverity.Warning;
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await ViewModel.CreateConfigFromTemplateCommand.ExecuteAsync(
                new HomePageViewModel.CreateConfigFromTemplateRequest(
                    selectedTemplate,
                    nameBox.Text,
                    typeCombo.SelectedItem as PluginConfigKindOption));
            return;
        }
    }

    private void OnAutoDiscoverGamesClick(object sender, RoutedEventArgs e)
        => ViewModel.AutoDiscoverGamesCommand.Execute(null);
    private void OnScenarioChosen(object? sender, BackupSetupScenario scenario)
        => NavigationService.NavigateTo("BackupSetup", new BackupSetupNavigationParameter(Scenario: scenario));
    private void OnOpenHistory(object sender, RoutedEventArgs e) => NavigationService.NavigateTo("History");
    private void OnOpenCloudRecovery(object sender, RoutedEventArgs e) => NavigationService.NavigateTo("CloudSetup");

    private void OnAddConfigClick(SplitButton sender, SplitButtonClickEventArgs args)
        => NavigationService.NavigateTo("BackupSetup");

    private async void OnPluginBatchCreateClick(object sender, RoutedEventArgs e)
        => await ViewModel.RunFormInteractionAsync(ShowPluginBatchFormAsync);

    private async Task ShowPluginBatchFormAsync(CancellationToken cancellationToken)
    {
        var resources = ResourceLoader.GetForViewIndependentUse();
        var kinds = ViewModel.GetConfigKinds().Where(k => ViewModel.GetPluginBatchAvailability(k.RequiredPluginId).IsAvailable).ToArray();
        if (kinds.Length == 0)
        {
            await AppDialogService.Default.ShowMessageAsync(I18n.GetString("Home_PluginBatchTitle"), I18n.GetString("Setup_PluginUnavailable"), XamlRoot, cancellationToken: cancellationToken);
            return;
        }
        var picker = CreateConfigKindCombo(kinds, "HomePluginBatchKind", resources);
        var dialog = new ContentDialog { Title = I18n.GetString("Home_PluginBatchTitle"), Content = picker,
            PrimaryButtonText = I18n.GetString("GameDiscovery_PluginBatch_DiscoverKnown.Content"),
            SecondaryButtonText = I18n.GetString("GameDiscovery_PluginBatch_PickRoot.Content"),
            CloseButtonText = I18n.GetString("Common_Cancel"), IsPrimaryButtonEnabled = false, IsSecondaryButtonEnabled = false };
        picker.SelectionChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = picker.SelectedItem is PluginConfigKindOption;
            dialog.IsSecondaryButtonEnabled = dialog.IsPrimaryButtonEnabled;
        };
        picker.SelectedIndex = 0;
        var result = await AppDialogService.Default.ShowCustomAsync(dialog, XamlRoot, cancellationToken);
        if (result == ContentDialogResult.None
            || picker.SelectedItem is not PluginConfigKindOption selected) return;
        await ViewModel.CreateConfigCommand.ExecuteAsync(new HomePageViewModel.CreateConfigRequest(
            "", IconCatalog.DefaultConfigIconGlyph, selected, true, DiscoverKnownLocations: result == ContentDialogResult.Primary));
    }

    private static ComboBox CreateConfigKindCombo(
        IReadOnlyList<PluginConfigKindOption> configKinds,
        string automationId,
        ResourceLoader resources)
    {
        var combo = new ComboBox
        {
            Header = resources.GetString("HomePage_ConfigKindHeader"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            DisplayMemberPath = nameof(PluginConfigKindOption.DisplayName),
            ItemsSource = configKinds
        };
        AutomationProperties.SetAutomationId(combo, automationId);
        AutomationProperties.SetName(combo, resources.GetString("HomePage_ConfigKindHeader"));
        return combo;
    }

}

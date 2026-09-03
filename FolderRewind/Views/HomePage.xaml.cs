using FolderRewind.Models;
using FolderRewind.History.Application;
using FolderRewind.Services;
using FolderRewind.Services.Discovery;
using FolderRewind.Services.Plugins;
using FolderRewind.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ResourceLoader = FolderRewind.Services.AppResourceLoader;

namespace FolderRewind.Views
{
    public sealed partial class HomePage : Page
    {
        public HomePageViewModel ViewModel { get; } = new();

        public GlobalSettings? Settings => ViewModel.Settings;

        public HomePage()
        {
            this.InitializeComponent();

            this.Loaded += OnLoaded;
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

            if (args.Item is not ManagedFolder folder)
            {
                return;
            }

            AutomationProperties.SetName(args.ItemContainer, folder.DisplayName);
            AutomationProperties.SetAutomationId(args.ItemContainer, $"HomeFavoriteItem_{folder.Id}");
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

            if (args.Item is not BackupConfig config)
            {
                return;
            }

            AutomationProperties.SetName(args.ItemContainer, config.Name);
            AutomationProperties.SetAutomationId(args.ItemContainer, $"HomeConfigItem_{config.Id}");
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
            base.OnNavigatedFrom(e);
            ViewModel.Deactivate();
        }

        private void OnFavoriteCardClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ManagedFolder folder)
            {
                var parentConfig = ViewModel.FindParentConfig(folder);
                if (parentConfig != null)
                {
                    _ = NavigationService.NavigateTo("Manager", ManagerNavigationParameter.ForFolder(parentConfig.Id, folder.Path));
                }
            }
        }

        private void OnConfigCardClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is BackupConfig config)
            {
                _ = NavigationService.NavigateTo("Manager", ManagerNavigationParameter.ForConfig(config.Id));
            }
        }

        private void OnSortModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Settings == null || SortCombo == null) return;

            if (SortCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                ViewModel.SetSortMode(tag);
            }
        }

        private async void OnQuickBackupClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ManagedFolder folder)
            {
                btn.IsEnabled = false;
                try
                {
                    await ViewModel.BackupFolderAsync(folder, "HomePage Quick Backup");
                }
                finally
                {
                    btn.IsEnabled = true;
                }
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplySortSelection();
        }

        private void ApplySortSelection()
        {
            if (SortCombo == null || Settings == null) return;

            foreach (var obj in SortCombo.Items)
            {
                if (obj is ComboBoxItem item && item.Tag is string tag && string.Equals(tag, ViewModel.CurrentSortMode, StringComparison.OrdinalIgnoreCase))
                {
                    SortCombo.SelectedItem = item;
                    return;
                }
            }

            SortCombo.SelectedIndex = 0;
        }

        private async void OnAddConfigFromTemplateClick(object sender, RoutedEventArgs e)
        {
            var resourceLoader = ResourceLoader.GetForViewIndependentUse();
            PluginService.Initialize();

            var configKinds = PluginService.GetAllSupportedConfigKinds(includeEncrypted: true).ToList();

            string preferredTemplateId = string.Empty;
            string draftConfigName = string.Empty;
            string draftOfficialSearch = string.Empty;
            string? preferredType = null;
            string feedbackMessage = string.Empty;
            InfoBarSeverity feedbackSeverity = InfoBarSeverity.Informational;

            while (true)
            {
                var templates = BackupPresetService.GetTemplates()
                    .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                var feedbackBar = new InfoBar
                {
                    IsClosable = true,
                    IsOpen = !string.IsNullOrWhiteSpace(feedbackMessage),
                    Message = feedbackMessage,
                    Severity = feedbackSeverity
                };

                var templateHintText = new TextBlock
                {
                    Text = templates.Count == 0
                        ? I18n.GetString("Template_CreateFrom_Home_NoTemplates")
                        : I18n.GetString("Template_CreateFrom_Home_LocalTemplatesHint"),
                    Opacity = 0.75,
                    TextWrapping = TextWrapping.Wrap
                };

                var templateCombo = new ComboBox
            {
                Header = I18n.GetString("Template_CreateFrom_Home_Template"),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            foreach (var template in templates)
            {
                templateCombo.Items.Add(new ComboBoxItem { Content = template.Name, Tag = template });
            }

                var templateInfoText = new TextBlock
            {
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap
            };

                var warningText = new TextBlock
            {
                Foreground = new SolidColorBrush(Colors.OrangeRed),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };

                var officialSearchBox = new TextBox
                {
                    Header = I18n.GetString("Template_CreateFrom_Home_OfficialSearchHeader"),
                    PlaceholderText = I18n.GetString("Template_CreateFrom_Home_OfficialSearchPlaceholder"),
                    Text = draftOfficialSearch
                };

                var nameBox = new TextBox
            {
                Header = resourceLoader.GetString("HomePage_ConfigNameHeader"),
                PlaceholderText = resourceLoader.GetString("HomePage_ConfigNamePlaceholder"),
                Text = draftConfigName
            };

                var typeCombo = new ComboBox
            {
                Header = resourceLoader.GetString("HomePage_ConfigKindHeader"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                DisplayMemberPath = nameof(PluginConfigKindOption.DisplayName)
            };
            foreach (var kind in configKinds)
            {
                typeCombo.Items.Add(kind);
            }
            AutomationProperties.SetAutomationId(typeCombo, "TemplateConfigKindPicker");
            AutomationProperties.SetName(typeCombo, resourceLoader.GetString("HomePage_ConfigKindHeader"));

                if (!string.IsNullOrWhiteSpace(preferredType))
                {
                    typeCombo.SelectedItem = configKinds.FirstOrDefault(kind =>
                        string.Equals(kind.SelectionValue, preferredType, StringComparison.OrdinalIgnoreCase));
                }

                BackupPreset? GetSelectedTemplate()
                {
                    return (templateCombo.SelectedItem as ComboBoxItem)?.Tag as BackupPreset;
                }

                void RefreshSelection()
                {
                    var selectedTemplate = GetSelectedTemplate();
                    if (selectedTemplate == null)
                    {
                        templateInfoText.Text = templates.Count == 0
                            ? I18n.GetString("Template_CreateFrom_Home_NoTemplates")
                            : string.Empty;
                        warningText.Text = string.Empty;
                        warningText.Visibility = Visibility.Collapsed;
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(nameBox.Text))
                    {
                        nameBox.Text = string.IsNullOrWhiteSpace(selectedTemplate.DefaultConfigName)
                            ? selectedTemplate.Name
                            : selectedTemplate.DefaultConfigName;
                    }

                    var ruleCount = selectedTemplate.PathRules?.Count ?? 0;
                    templateInfoText.Text = I18n.Format(
                        "Template_CreateFrom_Home_TemplateInfo",
                        selectedTemplate.Name,
                        ruleCount.ToString());

                    typeCombo.SelectedItem = configKinds.FirstOrDefault(kind =>
                        string.Equals(kind.Kind.OwnerId, selectedTemplate.Kind.OwnerId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(kind.Kind.KindId, selectedTemplate.Kind.KindId, StringComparison.OrdinalIgnoreCase)
                        && kind.IsEncrypted == selectedTemplate.IsEncrypted)
                        ?? configKinds.FirstOrDefault();

                    var warnings = new List<string>();
                    if (!configKinds.Any(kind =>
                            string.Equals(kind.Kind.OwnerId, selectedTemplate.Kind.OwnerId, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(kind.Kind.KindId, selectedTemplate.Kind.KindId, StringComparison.OrdinalIgnoreCase)
                            && kind.IsEncrypted == selectedTemplate.IsEncrypted))
                    {
                        warnings.Add(I18n.Format(
                            "Template_ConfigKindUnavailable",
                            $"{selectedTemplate.Kind.OwnerId}/{selectedTemplate.Kind.KindId}"));
                    }

                    var missingPluginIds = BackupPresetService.GetMissingRequiredPluginIds(selectedTemplate);
                    if (missingPluginIds.Count > 0)
                    {
                        warnings.Add(I18n.Format("Template_RequiredPluginsMissing", string.Join(", ", missingPluginIds)));
                    }

                    warningText.Text = string.Join(Environment.NewLine, warnings);
                    warningText.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                }

                if (templates.Count > 0)
                {
                    var preferredItem = templateCombo.Items
                        .OfType<ComboBoxItem>()
                        .FirstOrDefault(item => string.Equals((item.Tag as BackupPreset)?.Id, preferredTemplateId, StringComparison.OrdinalIgnoreCase));
                    templateCombo.SelectedItem = preferredItem ?? templateCombo.Items[0];
                }

                templateCombo.SelectionChanged += (_, __) => RefreshSelection();
                RefreshSelection();

                var panel = new StackPanel { Spacing = 12 };
                panel.Children.Add(feedbackBar);
                panel.Children.Add(templateHintText);
                panel.Children.Add(templateCombo);
                panel.Children.Add(templateInfoText);
                panel.Children.Add(warningText);
                panel.Children.Add(officialSearchBox);
                panel.Children.Add(nameBox);
                panel.Children.Add(typeCombo);

                var dialog = new ContentDialog
                {
                    Title = I18n.GetString("Template_CreateFrom_Home_Title"),
                    Content = panel,
                    PrimaryButtonText = resourceLoader.GetString("HomePage_CreateButton"),
                    SecondaryButtonText = I18n.GetString("Template_CreateFrom_Home_SearchOfficial"),
                    CloseButtonText = resourceLoader.GetString("Common_Cancel"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot
                };
                dialog.IsPrimaryButtonEnabled = GetSelectedTemplate() != null;
                templateCombo.SelectionChanged += (_, __) => dialog.IsPrimaryButtonEnabled = GetSelectedTemplate() != null;
                ThemeService.ApplyThemeToDialog(dialog);

                var triggerOfficialSearchByEnter = false;
                officialSearchBox.KeyDown += (_, keyArgs) =>
                {
                    if (keyArgs.Key != Windows.System.VirtualKey.Enter)
                    {
                        return;
                    }

                    keyArgs.Handled = true;
                    triggerOfficialSearchByEnter = true;
                    dialog.Hide();
                };

                var dialogResult = await dialog.ShowAsync();
                draftConfigName = nameBox.Text;
                draftOfficialSearch = officialSearchBox.Text?.Trim() ?? string.Empty;
                preferredType = (typeCombo.SelectedItem as PluginConfigKindOption)?.SelectionValue;
                preferredTemplateId = GetSelectedTemplate()?.Id ?? preferredTemplateId;
                feedbackMessage = string.Empty;

                if (dialogResult == ContentDialogResult.Secondary || triggerOfficialSearchByEnter)
                {
                    var item = await OfficialTemplateDialogService.PickTemplateAsync(
                        this.XamlRoot,
                        I18n.GetString("OfficialTemplates_CreateFromOfficialTitle"),
                        draftOfficialSearch);
                    if (item == null)
                    {
                        continue;
                    }

                    var importResult = await OfficialTemplateImportService.ImportTemplateAsync(this.XamlRoot, item);
                    if (importResult.Canceled)
                    {
                        continue;
                    }

                    feedbackMessage = importResult.Message;
                    feedbackSeverity = importResult.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error;

                    if (importResult.Success && importResult.ImportedTemplate != null)
                    {
                        // 官方模板导入成功后，重新回到当前创建流程并自动选中新模板，减少用户重复操作。
                        preferredTemplateId = importResult.ImportedTemplate.Id;
                    }

                    continue;
                }

                if (dialogResult != ContentDialogResult.Primary)
                {
                    return;
                }

                var selectedTemplateFinal = GetSelectedTemplate();
                if (selectedTemplateFinal == null)
                {
                    feedbackMessage = I18n.GetString("Template_Export_TemplateNotFound");
                    feedbackSeverity = InfoBarSeverity.Warning;
                    continue;
                }

                await CreateConfigFromTemplateAsync(
                    selectedTemplateFinal,
                    nameBox.Text,
                    typeCombo.SelectedItem as PluginConfigKindOption,
                    resourceLoader);
                return;

            }
        }

        private void OnAutoDiscoverGamesClick(object sender, RoutedEventArgs e)
        {
            _ = NavigationService.NavigateTo("GameDiscovery");
        }

        private async Task CreateConfigFromTemplateAsync(
            BackupPreset selectedTemplate,
            string configName,
            PluginConfigKindOption? selectedKind,
            ResourceLoader resourceLoader)
        {
            var applicationMode = BackupPresetApplicationClassifier.Classify(selectedTemplate);
            if (applicationMode == BackupPresetApplicationMode.ProviderTargeted)
            {
                _ = NavigationService.NavigateTo(
                    "GameDiscovery",
                    GameDiscoveryNavigationParameter.ForPreset(selectedTemplate.ShareId, configName));
                return;
            }
            if (applicationMode == BackupPresetApplicationMode.Invalid)
            {
                var invalidDialog = new ContentDialog
                {
                    Title = I18n.GetString("Template_CreateFrom_Home_Title"),
                    Content = I18n.GetString("Template_CreateFrom_Home_InvalidPreset"),
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(invalidDialog);
                await invalidDialog.ShowAsync();
                return;
            }

            var createResult = BackupPresetService.CreateConfigFromTemplate(
                selectedTemplate,
                configName,
                selectedKind);
            if (!createResult.Success || createResult.Config == null)
            {
                var failedDialog = new ContentDialog
                {
                    Title = I18n.GetString("Template_CreateFrom_Home_Title"),
                    Content = createResult.Message,
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(failedDialog);
                await failedDialog.ShowAsync();
                return;
            }

            if (selectedKind is not null)
            {
                // 模板可以覆盖显示类型，但持久化必须同步写入稳定 Config Kind 身份。
                PluginService.ApplyConfigKind(createResult.Config, selectedKind);
            }

            // 模板命中路径后先让用户确认一次，避免“猜错路径但已经落库”的尴尬情况。
            var selectedFolders = await ConfirmTemplateFolderSelectionAsync(
                selectedTemplate,
                createResult.FolderCandidates);
            if (selectedFolders == null)
            {
                return;
            }

            foreach (var folder in selectedFolders)
            {
                createResult.Config.SourceFolders.Add(folder);
            }

            string? encryptionPassword = null;
            if (createResult.Config.IsEncrypted)
            {
                encryptionPassword = await PromptSetPasswordAsync();
                if (encryptionPassword == null)
                {
                    return;
                }
            }

            createResult.Config.SummaryText = resourceLoader.GetString("HomePage_NewConfigSummary");
            ConfigService.CurrentConfig.BackupConfigs.Add(createResult.Config);
            ConfigService.Save();
            await TryInitializeNativeHistoryAsync(createResult.Config);

            if (createResult.Config.IsEncrypted && !string.IsNullOrEmpty(encryptionPassword))
            {
                EncryptionService.StorePassword(createResult.Config.Id, encryptionPassword);
            }

            var finalMessage = BuildTemplateCreationMessage(createResult, createResult.Config.SourceFolders.Count);
            if (!string.IsNullOrWhiteSpace(finalMessage))
            {
                var infoDialog = new ContentDialog
                {
                    Content = finalMessage,
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(infoDialog);
                await infoDialog.ShowAsync();
            }

            _ = NavigationService.NavigateTo("Manager", ManagerNavigationParameter.ForConfig(createResult.Config.Id));
        }

        private async System.Threading.Tasks.Task<List<ManagedFolder>?> ConfirmTemplateFolderSelectionAsync(
            BackupPreset template,
            IReadOnlyList<BackupPresetService.TemplateFolderCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return new List<ManagedFolder>();
            }

            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock
            {
                Text = I18n.Format(
                    "Template_CreateFrom_Home_SelectFoldersDesc",
                    template.Name,
                    candidates.Count.ToString(CultureInfo.CurrentCulture)),
                TextWrapping = TextWrapping.Wrap
            });

            if (!candidates.Any(c => c.IsSelectedByDefault))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = I18n.GetString("Template_CreateFrom_Home_SelectFoldersHint"),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Colors.OrangeRed),
                    FontSize = 12
                });
            }

            // 这里故意把“自动勾选”和“仅建议”放在同一个确认框里，
            // 让用户能顺手二次筛一遍，而不是被迫回到配置页里返工。
            var listPanel = new StackPanel { Spacing = 10 };
            var checkboxEntries = new List<(CheckBox Box, BackupPresetService.TemplateFolderCandidate Candidate)>();
            foreach (var candidate in candidates
                .OrderByDescending(c => c.IsSelectedByDefault)
                .ThenByDescending(c => c.Confidence)
                .ThenBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                var checkBox = new CheckBox
                {
                    Content = string.IsNullOrWhiteSpace(candidate.DisplayName) ? candidate.Path : candidate.DisplayName,
                    IsChecked = candidate.IsSelectedByDefault
                };

                var badgeText = candidate.IsSelectedByDefault
                    ? I18n.GetString("Template_CreateFrom_Home_FolderCandidateAuto")
                    : I18n.GetString("Template_CreateFrom_Home_FolderCandidateSuggested");

                var itemPanel = new StackPanel { Spacing = 2 };
                itemPanel.Children.Add(checkBox);
                itemPanel.Children.Add(new TextBlock
                {
                    Text = I18n.Format(
                        "Template_CreateFrom_Home_FolderCandidateMeta",
                        candidate.RuleName,
                        candidate.Confidence.ToString("P0", CultureInfo.CurrentCulture),
                        badgeText),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Colors.Gray),
                    FontSize = 12,
                    Margin = new Thickness(28, 0, 0, 0)
                });
                itemPanel.Children.Add(new TextBlock
                {
                    Text = candidate.Path,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Colors.Gray),
                    FontSize = 12,
                    Margin = new Thickness(28, 0, 0, 0)
                });
                if (!string.IsNullOrWhiteSpace(candidate.MarkerSummary))
                {
                    itemPanel.Children.Add(new TextBlock
                    {
                        Text = candidate.MarkerSummary,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Colors.Gray),
                        FontSize = 12,
                        Margin = new Thickness(28, 0, 0, 0)
                    });
                }

                listPanel.Children.Add(itemPanel);
                checkboxEntries.Add((checkBox, candidate));
            }

            panel.Children.Add(new ScrollViewer
            {
                Content = listPanel,
                MaxHeight = 360
            });

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("Template_CreateFrom_Home_SelectFoldersTitle"),
                Content = panel,
                PrimaryButtonText = I18n.GetString("Common_Confirm"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return null;
            }

            return checkboxEntries
                .Where(entry => entry.Box.IsChecked == true)
                .Select(entry => new ManagedFolder
                {
                    Path = entry.Candidate.Path,
                    DisplayName = entry.Candidate.DisplayName,
                    Description = I18n.GetString("Template_AutoDiscoveredFolderDescription"),
                    CoverImagePath = ResolveFolderCoverImagePath(entry.Candidate.Path)
                })
                .ToList();
        }

        private static string ResolveFolderCoverImagePath(string? folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return string.Empty;
            }

            try
            {
                var potentialIcon = Path.Combine(folderPath, "icon.png");
                return File.Exists(potentialIcon) ? potentialIcon : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string BuildTemplateCreationMessage(
            BackupPresetService.CreateConfigFromTemplateResult createResult,
            int selectedFolderCount)
        {
            if (createResult == null)
            {
                return string.Empty;
            }

            if (createResult.FolderCandidates == null || createResult.FolderCandidates.Count == 0)
            {
                return createResult.Message;
            }

            return I18n.Format(
                "Template_CreateFrom_Home_SelectionSummary",
                selectedFolderCount.ToString(CultureInfo.CurrentCulture),
                createResult.FolderCandidates.Count.ToString(CultureInfo.CurrentCulture));
        }

        private async void OnAddConfigClick(SplitButton sender, SplitButtonClickEventArgs args)
        {
            var resourceLoader = ResourceLoader.GetForViewIndependentUse();
            PluginService.Initialize();

            var stack = new StackPanel { Spacing = 16 };
            var nameBox = new TextBox
            {
                Header = resourceLoader.GetString("HomePage_ConfigNameHeader"),
                PlaceholderText = resourceLoader.GetString("HomePage_ConfigNamePlaceholder")
            };

            var configKinds = PluginService.GetAllSupportedConfigKinds(includeEncrypted: true).ToList();
            var typeCombo = new ComboBox
            {
                Header = resourceLoader.GetString("HomePage_ConfigKindHeader"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                DisplayMemberPath = nameof(PluginConfigKindOption.DisplayName)
            };
            foreach (var kind in configKinds) typeCombo.Items.Add(kind);
            typeCombo.SelectedIndex = 0;
            AutomationProperties.SetAutomationId(typeCombo, "NewConfigKindPicker");
            AutomationProperties.SetName(typeCombo, resourceLoader.GetString("HomePage_ConfigKindHeader"));

            var typeDesc = new TextBlock
            {
                Text = resourceLoader.GetString("HomePage_ConfigKindDesc"),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap
            };

            // 图标选择保留 Host 的统一图标目录，插件只负责声明稳定的配置类型身份。
            var iconGrid = new GridView { SelectionMode = ListViewSelectionMode.Single, Height = 180 };
            foreach (var icon in IconCatalog.ConfigIconGlyphs) iconGrid.Items.Add(icon);
            iconGrid.SelectedIndex = 0;

            iconGrid.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                @"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
                    <Border Width='40' Height='40' CornerRadius='4' Background='{ThemeResource LayerFillColorDefaultBrush}'>
                        <FontIcon Glyph='{Binding}' FontSize='20' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                    </Border>
                  </DataTemplate>");

            var batchCreateToggle = new ToggleSwitch
            {
                Header = resourceLoader.GetString("HomePage_PluginBatchCreateHeader"),
                OffContent = resourceLoader.GetString("HomePage_PluginBatchCreateOff"),
                OnContent = resourceLoader.GetString("HomePage_PluginBatchCreateOn"),
                IsOn = false
            };
            AutomationProperties.SetAutomationId(batchCreateToggle, "PluginBatchCreateToggle");
            var batchStatus = new TextBlock
            {
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap
            };

            stack.Children.Add(nameBox);
            stack.Children.Add(typeCombo);
            stack.Children.Add(typeDesc);
            stack.Children.Add(batchCreateToggle);
            stack.Children.Add(batchStatus);
            stack.Children.Add(new TextBlock { Text = resourceLoader.GetString("HomePage_SelectIcon"), Style = (Style)Application.Current.Resources["BaseTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) });
            stack.Children.Add(iconGrid);

            ContentDialog dialog = new ContentDialog
            {
                Title = resourceLoader.GetString("HomePage_NewConfigDialogTitle"),
                Content = stack,
                PrimaryButtonText = resourceLoader.GetString("HomePage_CreateButton"),
                CloseButtonText = resourceLoader.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);

            PluginBatchProviderAvailability? batchAvailability = null;
            void RefreshDialogState()
            {
                var selectedKind = typeCombo.SelectedItem as PluginConfigKindOption;
                typeDesc.Text = selectedKind?.Description ?? resourceLoader.GetString("HomePage_ConfigKindDesc");
                batchAvailability = GameDiscoveryProviderFactory.GetPluginBatchAvailability(
                    selectedKind?.RequiredPluginId);
                batchCreateToggle.IsEnabled = batchAvailability.IsAvailable;
                if (!batchAvailability.IsAvailable)
                {
                    batchCreateToggle.IsOn = false;
                }
                batchStatus.Text = batchAvailability.Message;

                var isBatch = batchCreateToggle.IsOn && batchAvailability.IsAvailable;
                nameBox.IsEnabled = !isBatch;
                iconGrid.IsEnabled = !isBatch;
                dialog.PrimaryButtonText = resourceLoader.GetString(
                    isBatch ? "HomePage_PluginBatchCreateContinue" : "HomePage_CreateButton");
            }

            typeCombo.SelectionChanged += (_, __) => RefreshDialogState();
            batchCreateToggle.Toggled += (_, __) => RefreshDialogState();
            RefreshDialogState();

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var selectedKind = typeCombo.SelectedItem as PluginConfigKindOption
                    ?? configKinds.First();

                if (batchCreateToggle.IsOn)
                {
                    batchAvailability = GameDiscoveryProviderFactory.GetPluginBatchAvailability(
                        selectedKind.RequiredPluginId);
                    if (!batchAvailability.IsAvailable)
                    {
                        var unavailable = new ContentDialog
                        {
                            Title = resourceLoader.GetString("HomePage_PluginBatchCreateFailedTitle"),
                            Content = batchAvailability.Message,
                            CloseButtonText = resourceLoader.GetString("Common_Ok"),
                            XamlRoot = this.XamlRoot
                        };
                        ThemeService.ApplyThemeToDialog(unavailable);
                        await unavailable.ShowAsync();
                        return;
                    }

                    var rootFolderPath = await PickFolderPathAsync(
                        resourceLoader.GetString("HomePage_PluginBatchCreatePickRootTitle"),
                        "FolderRewind.HomePage.PluginBatch.Root");
                    if (string.IsNullOrWhiteSpace(rootFolderPath))
                    {
                        return;
                    }

                    _ = NavigationService.NavigateTo(
                        "GameDiscovery",
                        GameDiscoveryNavigationParameter.ForPluginBatch(
                            selectedKind.RequiredPluginId,
                            selectedKind.CreateReference(),
                            rootFolderPath));
                    return;
                }

                if (string.IsNullOrWhiteSpace(nameBox.Text)) return;

                bool isEncrypted = selectedKind.IsEncrypted;

                string? encryptionPassword = null;
                if (isEncrypted)
                {
                    encryptionPassword = await PromptSetPasswordAsync();
                    if (encryptionPassword == null) return; // 用户取消密码设置时，不创建半成品配置。
                }

                var selectedIcon = iconGrid.SelectedItem as string ?? IconCatalog.DefaultConfigIconGlyph;
                var newConfig = new BackupConfig
                {
                    Name = nameBox.Text,
                    IconGlyph = selectedIcon,
                    IsEncrypted = isEncrypted,
                    DestinationPath = ConfigService.BuildDefaultDestinationPath(nameBox.Text),
                    SummaryText = resourceLoader.GetString("HomePage_NewConfigSummary"),
                    Cloud = new CloudSettings
                    {
                        RemoteBasePath = ConfigService.GetRecommendedDefaultCloudRemoteBasePath()
                    }
                };
                PluginService.ApplyConfigKind(newConfig, selectedKind);

                ConfigService.CurrentConfig.BackupConfigs.Add(newConfig);
                ConfigService.Save();
                await TryInitializeNativeHistoryAsync(newConfig);

                if (isEncrypted && !string.IsNullOrEmpty(encryptionPassword))
                {
                    EncryptionService.StorePassword(newConfig.Id, encryptionPassword);
                }

                _ = NavigationService.NavigateTo("Manager", ManagerNavigationParameter.ForConfig(newConfig.Id));
            }
        }

        private static async Task TryInitializeNativeHistoryAsync(BackupConfig config)
        {
            try
            {
                _ = await NativeHistoryCoreGateway.EnsureReadyAsync(config);
            }
            catch (Exception ex)
            {
                LogService.LogError(
                    I18n.Format("History_NativeInitializationFailed", config.Name, ex.Message),
                    nameof(HomePage),
                    ex);
                NotificationService.ShowError(
                    I18n.Format("History_NativeInitializationFailed", config.Name, ex.Message));
            }
        }

        /// <summary>
        /// 弹出设置密码的对话框，返回用户输入的密码。如果用户取消，则返回 null。
        /// </summary>
        private async System.Threading.Tasks.Task<string?> PromptSetPasswordAsync()
        {
            var resourceLoader = ResourceLoader.GetForViewIndependentUse();

            var passwordBox = new PasswordBox
            {
                PlaceholderText = resourceLoader.GetString("Encryption_SetPasswordPlaceholder")
            };
            var confirmBox = new PasswordBox
            {
                PlaceholderText = resourceLoader.GetString("Encryption_ConfirmPasswordPlaceholder")
            };
            var warningText = new TextBlock
            {
                Text = resourceLoader.GetString("Encryption_PasswordWarning"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0)
            };
            var errorText = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red),
                FontSize = 12,
                Visibility = Visibility.Collapsed
            };

            var stack = new StackPanel { Spacing = 12 };
            stack.Children.Add(new TextBlock
            {
                Text = resourceLoader.GetString("Encryption_SetPasswordDesc"),
                TextWrapping = TextWrapping.Wrap
            });
            stack.Children.Add(passwordBox);
            stack.Children.Add(confirmBox);
            stack.Children.Add(warningText);
            stack.Children.Add(errorText);

            var dialog = new ContentDialog
            {
                Title = resourceLoader.GetString("Encryption_SetPasswordTitle"),
                Content = stack,
                PrimaryButtonText = resourceLoader.GetString("Common_Ok"),
                CloseButtonText = resourceLoader.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);

            while (true)
            {
                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary) return null;

                if (string.IsNullOrEmpty(passwordBox.Password))
                {
                    errorText.Text = resourceLoader.GetString("Encryption_PasswordEmpty");
                    errorText.Visibility = Visibility.Visible;
                    continue;
                }

                if (passwordBox.Password != confirmBox.Password)
                {
                    errorText.Text = resourceLoader.GetString("Encryption_PasswordMismatch");
                    errorText.Visibility = Visibility.Visible;
                    continue;
                }

                return passwordBox.Password;
            }
        }

        private static Task<string?> PickFolderPathAsync(string title, string settingsIdentifier)
        {
            return MainWindowService.PickFolderPathAsync(
                title,
                settingsIdentifier,
                MainWindowService.SuggestedPickerLocation.ComputerFolder);
        }

        #region Context Menu Handlers

        private async void OnBackupAllFoldersClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is BackupConfig config)
            {
                if (config.SourceFolders == null || config.SourceFolders.Count == 0)
                {
                    var resourceLoader = ResourceLoader.GetForViewIndependentUse();
                    var dialog = new ContentDialog
                    {
                        Title = resourceLoader.GetString("HomePage_ContextMenu_NoFolders_Title"),
                        Content = resourceLoader.GetString("HomePage_ContextMenu_NoFolders_Content"),
                        CloseButtonText = resourceLoader.GetString("Common_Ok"),
                        XamlRoot = this.XamlRoot
                    };
                    ThemeService.ApplyThemeToDialog(dialog);
                    await dialog.ShowAsync();
                    return;
                }

                await ViewModel.BackupAllFoldersAsync(config, "HomePage Batch Backup");
            }
        }

        // 
        private void OnOpenDestinationClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is BackupConfig config)
            {
                ViewModel.TryOpenDestination(config);
            }
        }

        // 
        private void OnEditConfigClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is BackupConfig config)
            {
                _ = NavigationService.NavigateTo("Manager", ManagerNavigationParameter.ForConfig(config.Id));
            }
        }

        private async void OnDeleteConfigClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is BackupConfig config)
            {
                var resourceLoader = ResourceLoader.GetForViewIndependentUse();

                var dialog = new ContentDialog
                {
                    Title = resourceLoader.GetString("HomePage_ContextMenu_DeleteConfirm_Title"),
                    Content = string.Format(resourceLoader.GetString("HomePage_ContextMenu_DeleteConfirm_Content"), config.Name),
                    PrimaryButtonText = resourceLoader.GetString("HomePage_ContextMenu_DeleteConfirm_Delete"),
                    CloseButtonText = resourceLoader.GetString("Common_Cancel"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(dialog);

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    await ViewModel.DeleteConfigAsync(config);
                }
            }
        }

        #endregion
    }
}

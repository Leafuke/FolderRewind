using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using FolderRewind.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.Resources;
using Windows.Storage;
using Windows.Storage.Pickers;

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

        // 鐐瑰嚮鏀惰棌椤瑰崱鐗?-> 璺宠浆鍒扮鐞嗛〉骞堕€変腑璇ユ枃浠跺す
        private void OnFavoriteCardClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ManagedFolder folder)
            {
                // 闇€瑕佹壘鍒板畠灞炰簬鍝釜 Config锛屾墠鑳藉鑸?
                var parentConfig = ViewModel.FindParentConfig(folder);
                if (parentConfig != null)
                {
                    // 璺宠浆鍒扮鐞嗛〉骞惰嚜鍔ㄩ€変腑璇ユ枃浠跺す
                    _ = NavigationService.NavigateTo("Manager", ManagerNavigationParameter.ForFolder(parentConfig.Id, folder.Path));
                }
            }
        }

        // 鐐瑰嚮閰嶇疆鍗＄墖 -> 璺宠浆鍒扮鐞嗛〉
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

        // 蹇€熷浠芥寜閽偣鍑?
        private async void OnQuickBackupClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ManagedFolder folder)
            {
                // 杩欓噷淇濈暀鎸夐挳闃叉姈锛岄伩鍏嶇敤鎴疯繛缁偣鍑昏Е鍙戝娆″悓鐩綍澶囦唤銆?
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

            var configTypes = PluginService.GetAllSupportedConfigTypes().ToList();
            if (!configTypes.Contains("Default", StringComparer.OrdinalIgnoreCase))
            {
                configTypes.Insert(0, "Default");
            }
            if (!configTypes.Contains("Encrypted", StringComparer.OrdinalIgnoreCase))
            {
                var defaultIndex = configTypes.FindIndex(t => string.Equals(t, "Default", StringComparison.OrdinalIgnoreCase));
                configTypes.Insert(defaultIndex >= 0 ? defaultIndex + 1 : configTypes.Count, "Encrypted");
            }

            string preferredTemplateId = string.Empty;
            string draftConfigName = string.Empty;
            string draftOfficialSearch = string.Empty;
            string? preferredType = null;
            string feedbackMessage = string.Empty;
            InfoBarSeverity feedbackSeverity = InfoBarSeverity.Informational;

            while (true)
            {
                var templates = TemplateService.GetTemplates()
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
                Header = resourceLoader.GetString("HomePage_ConfigTypeHeader"),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            foreach (var type in configTypes)
            {
                typeCombo.Items.Add(type);
            }

                if (!string.IsNullOrWhiteSpace(preferredType))
                {
                    typeCombo.SelectedItem = configTypes.FirstOrDefault(t => string.Equals(t, preferredType, StringComparison.OrdinalIgnoreCase));
                }

                ConfigTemplate? GetSelectedTemplate()
                {
                    return (templateCombo.SelectedItem as ComboBoxItem)?.Tag as ConfigTemplate;
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

                    var typeToSelect = string.IsNullOrWhiteSpace(selectedTemplate.BaseConfigType)
                        ? "Default"
                        : selectedTemplate.BaseConfigType;
                    if (!configTypes.Contains(typeToSelect, StringComparer.OrdinalIgnoreCase))
                    {
                        typeToSelect = "Default";
                    }

                    typeCombo.SelectedItem = configTypes.FirstOrDefault(t => string.Equals(t, typeToSelect, StringComparison.OrdinalIgnoreCase));

                    var warnings = new List<string>();
                    if (!TemplateService.IsConfigTypeAvailable(selectedTemplate.BaseConfigType, out var reason)
                        && !string.IsNullOrWhiteSpace(reason))
                    {
                        warnings.Add(reason);
                    }

                    var missingPluginIds = TemplateService.GetMissingRequiredPluginIds(selectedTemplate);
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
                        .FirstOrDefault(item => string.Equals((item.Tag as ConfigTemplate)?.Id, preferredTemplateId, StringComparison.OrdinalIgnoreCase));
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
                preferredType = typeCombo.SelectedItem as string;
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

                await CreateConfigFromTemplateAsync(selectedTemplateFinal, nameBox.Text, typeCombo.SelectedItem as string, resourceLoader);
                return;

            }
        }

        private async Task CreateConfigFromTemplateAsync(
            ConfigTemplate selectedTemplate,
            string configName,
            string? selectedType,
            ResourceLoader resourceLoader)
        {
            var createResult = TemplateService.CreateConfigFromTemplate(selectedTemplate, configName, selectedType);
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
            ConfigTemplate template,
            IReadOnlyList<TemplateService.TemplateFolderCandidate> candidates)
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
            var checkboxEntries = new List<(CheckBox Box, TemplateService.TemplateFolderCandidate Candidate)>();
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
            TemplateService.CreateConfigFromTemplateResult createResult,
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

        // 娣诲姞閰嶇疆閫昏緫
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

            // 閰嶇疆绫诲瀷锛堝惈鎻掍欢鎵╁睍绫诲瀷 + 鍐呯疆鍔犲瘑绫诲瀷锛?
            var configTypes = PluginService.GetAllSupportedConfigTypes().ToList();
            // 纭繚 "Default" 濮嬬粓瀛樺湪锛堟彃浠剁郴缁熸湭鍚敤鏃跺垪琛ㄥ彲鑳戒负绌猴級
            if (!configTypes.Contains("Default", StringComparer.OrdinalIgnoreCase))
            {
                configTypes.Insert(0, "Default");
            }
            // 纭繚 "Encrypted" 浣滀负鍐呯疆绫诲瀷鍑虹幇鍦?"Default" 涔嬪悗
            if (!configTypes.Contains("Encrypted", StringComparer.OrdinalIgnoreCase))
            {
                int defaultIdx = configTypes.FindIndex(t => string.Equals(t, "Default", StringComparison.OrdinalIgnoreCase));
                configTypes.Insert(defaultIdx >= 0 ? defaultIdx + 1 : configTypes.Count, "Encrypted");
            }
            var typeCombo = new ComboBox
            {
                Header = resourceLoader.GetString("HomePage_ConfigTypeHeader"),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            foreach (var t in configTypes) typeCombo.Items.Add(t);
            typeCombo.SelectedIndex = 0;

            var typeDesc = new TextBlock
            {
                Text = resourceLoader.GetString("HomePage_ConfigTypeDesc"),
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap
            };

            // 鎻掍欢鎵归噺鍒涘缓锛堢敤浜?Minecraft 鎵弿 .minecraft/versions/*/saves 绛夛級
            var batchCreateToggle = new ToggleSwitch
            {
                Header = resourceLoader.GetString("HomePage_PluginBatchCreateHeader"),
                OffContent = resourceLoader.GetString("HomePage_PluginBatchCreateOff"),
                OnContent = resourceLoader.GetString("HomePage_PluginBatchCreateOn"),
                IsOn = false
            };

            void RefreshBatchToggleState()
            {
                var selectedType = typeCombo.SelectedItem as string;
                var canBatch = !string.IsNullOrWhiteSpace(selectedType) && !string.Equals(selectedType, "Default", StringComparison.OrdinalIgnoreCase);
                batchCreateToggle.IsEnabled = canBatch;
                if (!canBatch) batchCreateToggle.IsOn = false;

                // 鎵归噺鍒涘缓妯″紡涓嬶紝鍚嶇О鐢辨彃浠剁敓鎴?
                nameBox.IsEnabled = !batchCreateToggle.IsOn;
            }

            typeCombo.SelectionChanged += (_, __) => RefreshBatchToggleState();
            batchCreateToggle.Toggled += (_, __) => RefreshBatchToggleState();
            RefreshBatchToggleState();

            // 鍥炬爣閫夋嫨鍣?
            var iconGrid = new GridView { SelectionMode = ListViewSelectionMode.Single, Height = 180 };
            foreach (var icon in IconCatalog.ConfigIconGlyphs) iconGrid.Items.Add(icon);
            iconGrid.SelectedIndex = 0;

            iconGrid.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                @"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
                    <Border Width='40' Height='40' CornerRadius='4' Background='{ThemeResource LayerFillColorDefaultBrush}'>
                        <FontIcon Glyph='{Binding}' FontSize='20' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                    </Border>
                  </DataTemplate>");

            stack.Children.Add(nameBox);
            stack.Children.Add(typeCombo);
            stack.Children.Add(typeDesc);
            stack.Children.Add(batchCreateToggle);
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

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var selectedType = typeCombo.SelectedItem as string;
                if (string.IsNullOrWhiteSpace(selectedType)) selectedType = "Default";

                if (batchCreateToggle.IsOn)
                {
                    var rootFolder = await PickFolderAsync(resourceLoader.GetString("HomePage_PluginBatchCreatePickRootTitle"));
                    if (rootFolder == null) return;

                    var result = PluginService.InvokeCreateConfigs(rootFolder.Path, selectedType);
                    if (!result.Handled || result.CreatedConfigs == null || result.CreatedConfigs.Count == 0)
                    {
                        var failed = new ContentDialog
                        {
                            Title = resourceLoader.GetString("HomePage_PluginBatchCreateFailedTitle"),
                            Content = string.IsNullOrWhiteSpace(result.Message)
                                ? resourceLoader.GetString("HomePage_PluginBatchCreateFailedContent")
                                : result.Message,
                            CloseButtonText = resourceLoader.GetString("Common_Ok"),
                            XamlRoot = this.XamlRoot
                        };
                        ThemeService.ApplyThemeToDialog(failed);
                        await failed.ShowAsync();
                        return;
                    }

                    // 璁╃敤鎴蜂竴娆℃€ч€夋嫨鐩爣鐩綍锛堝彲閫夛紱涓嶉€夊垯绋嶅悗鍦ㄩ厤缃缃噷濉級
                    var destFolder = await PickFolderAsync(resourceLoader.GetString("HomePage_PluginBatchCreatePickDestinationTitle"));
                    var destPath = destFolder?.Path;

                    foreach (var c in result.CreatedConfigs)
                    {
                        // 鍏滃簳锛氱‘淇?ConfigType
                        if (string.IsNullOrWhiteSpace(c.ConfigType)) c.ConfigType = selectedType;
                        c.DestinationPath = !string.IsNullOrWhiteSpace(destPath)
                            ? Path.Combine(destPath, c.Name ?? string.Empty)
                            : ConfigService.BuildDefaultDestinationPath(c.Name);
                        c.SummaryText = resourceLoader.GetString("HomePage_NewConfigSummary");
                        ConfigService.CurrentConfig.BackupConfigs.Add(c);
                    }

                    ConfigService.Save();
                    _ = NavigationService.NavigateTo("Manager", ManagerNavigationParameter.ForConfig(result.CreatedConfigs[0].Id));
                    return;
                }

                if (string.IsNullOrWhiteSpace(nameBox.Text)) return;

                bool isEncrypted = string.Equals(selectedType, "Encrypted", StringComparison.OrdinalIgnoreCase);

                // 濡傛灉鏄姞瀵嗙被鍨嬶紝寮瑰嚭瀵嗙爜璁剧疆瀵硅瘽妗?
                string? encryptionPassword = null;
                if (isEncrypted)
                {
                    encryptionPassword = await PromptSetPasswordAsync();
                    if (encryptionPassword == null) return; // 鐢ㄦ埛鍙栨秷
                }

                var selectedIcon = iconGrid.SelectedItem as string ?? IconCatalog.DefaultConfigIconGlyph;
                var newConfig = new BackupConfig
                {
                    Name = nameBox.Text,
                    IconGlyph = selectedIcon,
                    ConfigType = isEncrypted ? "Default" : selectedType, // 鍔犲瘑閰嶇疆鐨勫簳灞傜被鍨嬩粛涓?Default
                    IsEncrypted = isEncrypted,
                    DestinationPath = ConfigService.BuildDefaultDestinationPath(nameBox.Text),
                    SummaryText = resourceLoader.GetString("HomePage_NewConfigSummary")
                };

                ConfigService.CurrentConfig.BackupConfigs.Add(newConfig);
                ConfigService.Save();

                // 瀛樺偍鍔犲瘑瀵嗙爜锛堝湪閰嶇疆淇濆瓨鍚庯紝鍥犱负闇€瑕?config.Id锛?
                if (isEncrypted && !string.IsNullOrEmpty(encryptionPassword))
                {
                    EncryptionService.StorePassword(newConfig.Id, encryptionPassword);
                }

                _ = NavigationService.NavigateTo("Manager", ManagerNavigationParameter.ForConfig(newConfig.Id));
            }
        }

        /// <summary>
        /// 寮瑰嚭璁剧疆鍔犲瘑瀵嗙爜鐨勫璇濇锛屽寘鍚?瀵嗙爜涓€鏃﹁缃棤娉曟洿鏀?鐨勮鍛婃彁绀恒€?
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

        private async System.Threading.Tasks.Task<StorageFolder?> PickFolderAsync(string title)
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add("*");

            if (!string.IsNullOrWhiteSpace(title))
            {
                picker.SettingsIdentifier = "FolderRewind.HomePage.Picker";
            }
            MainWindowService.InitializePicker(picker);

            return await picker.PickSingleFolderAsync();
        }

        #region Context Menu Handlers

        // 鍙抽敭鐐瑰嚮閰嶇疆鍗＄墖
        private void OnConfigCardRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {

        }

        // 澶囦唤閰嶇疆涓殑鎵€鏈夋枃浠跺す
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

        // 鎵撳紑鐩爣鏂囦欢澶?
        private void OnOpenDestinationClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is BackupConfig config)
            {
                ViewModel.TryOpenDestination(config);
            }
        }

        // 缂栬緫閰嶇疆锛堣烦杞埌绠＄悊椤碉級
        private void OnEditConfigClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is BackupConfig config)
            {
                _ = NavigationService.NavigateTo("Manager", ManagerNavigationParameter.ForConfig(config.Id));
            }
        }

        // 鍒犻櫎閰嶇疆
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

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    ViewModel.DeleteConfig(config);
                }
            }
        }

        #endregion
    }
}

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

        // 点击收藏项卡片 -> 跳转到管理页并选中该文件夹
        private void OnFavoriteCardClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ManagedFolder folder)
            {
                // 需要找到它属于哪个 Config，才能导航
                var parentConfig = ViewModel.FindParentConfig(folder);
                if (parentConfig != null)
                {
                    // 跳转到管理页并自动选中该文件夹
                    _ = NavigationService.NavigateTo("Manager", ManagerNavigationParameter.ForFolder(parentConfig.Id, folder.Path));
                }
            }
        }

        // 点击配置卡片 -> 跳转到管理页
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

        // 快速备份按钮点击
        private async void OnQuickBackupClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ManagedFolder folder)
            {
                // 这里保留按钮防抖，避免用户连续点击触发多次同目录备份。
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

            var templates = TemplateService.GetTemplates()
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (templates.Count == 0)
            {
                var emptyDialog = new ContentDialog
                {
                    Title = I18n.GetString("Template_CreateFrom_Home_Title"),
                    Content = I18n.GetString("Template_CreateFrom_Home_NoTemplates"),
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(emptyDialog);
                await emptyDialog.ShowAsync();
                return;
            }

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

            var templateCombo = new ComboBox
            {
                Header = I18n.GetString("Template_CreateFrom_Home_Template"),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            foreach (var template in templates)
            {
                templateCombo.Items.Add(new ComboBoxItem { Content = template.Name, Tag = template });
            }
            templateCombo.SelectedIndex = 0;

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

            var nameBox = new TextBox
            {
                Header = resourceLoader.GetString("HomePage_ConfigNameHeader"),
                PlaceholderText = resourceLoader.GetString("HomePage_ConfigNamePlaceholder")
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

            ConfigTemplate? GetSelectedTemplate()
            {
                return (templateCombo.SelectedItem as ComboBoxItem)?.Tag as ConfigTemplate;
            }

            void RefreshSelection()
            {
                var selectedTemplate = GetSelectedTemplate();
                if (selectedTemplate == null)
                {
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

            templateCombo.SelectionChanged += (_, __) => RefreshSelection();
            RefreshSelection();

            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(templateCombo);
            panel.Children.Add(templateInfoText);
            panel.Children.Add(warningText);
            panel.Children.Add(nameBox);
            panel.Children.Add(typeCombo);

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("Template_CreateFrom_Home_Title"),
                Content = panel,
                PrimaryButtonText = resourceLoader.GetString("HomePage_CreateButton"),
                CloseButtonText = resourceLoader.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var selectedTemplateFinal = GetSelectedTemplate();
            if (selectedTemplateFinal == null)
            {
                return;
            }

            var selectedType = typeCombo.SelectedItem as string;
            var createResult = TemplateService.CreateConfigFromTemplate(selectedTemplateFinal, nameBox.Text, selectedType);
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

            // 模板命中路径后先给用户做一次确认，避免“猜错路径但已经落库”的尴尬场面。
            var selectedFolders = await ConfirmTemplateFolderSelectionAsync(
                selectedTemplateFinal,
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

            // 这里故意把“自动勾选”和“仅建议”混在同一个确认框里，
            // 让用户能顺手二次筛一遍，而不是被迫去配置页里返工。
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
                    Description = I18n.GetString("Template_AutoDiscoveredFolderDescription")
                })
                .ToList();
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

        // 添加配置逻辑
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

            // 配置类型（含插件扩展类型 + 内置加密类型）
            var configTypes = PluginService.GetAllSupportedConfigTypes().ToList();
            // 确保 "Default" 始终存在（插件系统未启用时列表可能为空）
            if (!configTypes.Contains("Default", StringComparer.OrdinalIgnoreCase))
            {
                configTypes.Insert(0, "Default");
            }
            // 确保 "Encrypted" 作为内置类型出现在 "Default" 之后
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

            // 插件批量创建（用于 Minecraft 扫描 .minecraft/versions/*/saves 等）
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

                // 批量创建模式下，名称由插件生成
                nameBox.IsEnabled = !batchCreateToggle.IsOn;
            }

            typeCombo.SelectionChanged += (_, __) => RefreshBatchToggleState();
            batchCreateToggle.Toggled += (_, __) => RefreshBatchToggleState();
            RefreshBatchToggleState();

            // 图标选择器
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

                    // 让用户一次性选择目标目录（可选；不选则稍后在配置设置里填）
                    var destFolder = await PickFolderAsync(resourceLoader.GetString("HomePage_PluginBatchCreatePickDestinationTitle"));
                    var destPath = destFolder?.Path;

                    foreach (var c in result.CreatedConfigs)
                    {
                        // 兜底：确保 ConfigType
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

                // 如果是加密类型，弹出密码设置对话框
                string? encryptionPassword = null;
                if (isEncrypted)
                {
                    encryptionPassword = await PromptSetPasswordAsync();
                    if (encryptionPassword == null) return; // 用户取消
                }

                var selectedIcon = iconGrid.SelectedItem as string ?? IconCatalog.DefaultConfigIconGlyph;
                var newConfig = new BackupConfig
                {
                    Name = nameBox.Text,
                    IconGlyph = selectedIcon,
                    ConfigType = isEncrypted ? "Default" : selectedType, // 加密配置的底层类型仍为 Default
                    IsEncrypted = isEncrypted,
                    DestinationPath = ConfigService.BuildDefaultDestinationPath(nameBox.Text),
                    SummaryText = resourceLoader.GetString("HomePage_NewConfigSummary")
                };

                ConfigService.CurrentConfig.BackupConfigs.Add(newConfig);
                ConfigService.Save();

                // 存储加密密码（在配置保存后，因为需要 config.Id）
                if (isEncrypted && !string.IsNullOrEmpty(encryptionPassword))
                {
                    EncryptionService.StorePassword(newConfig.Id, encryptionPassword);
                }

                _ = NavigationService.NavigateTo("Manager", ManagerNavigationParameter.ForConfig(newConfig.Id));
            }
        }

        /// <summary>
        /// 弹出设置加密密码的对话框，包含"密码一旦设置无法更改"的警告提示。
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

        // 右键点击配置卡片
        private void OnConfigCardRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {

        }

        // 备份配置中的所有文件夹
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

        // 打开目标文件夹
        private void OnOpenDestinationClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is BackupConfig config)
            {
                ViewModel.TryOpenDestination(config);
            }
        }

        // 编辑配置（跳转到管理页）
        private void OnEditConfigClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is BackupConfig config)
            {
                _ = NavigationService.NavigateTo("Manager", ManagerNavigationParameter.ForConfig(config.Id));
            }
        }

        // 删除配置
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

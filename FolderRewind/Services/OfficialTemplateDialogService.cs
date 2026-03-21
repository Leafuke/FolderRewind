using FolderRewind.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services
{
    internal static class OfficialTemplateDialogService
    {
        public static async System.Threading.Tasks.Task<RemoteTemplateIndexItem?> PickTemplateAsync(
            XamlRoot xamlRoot,
            string title,
            string? initialSearch = null)
        {
            var fetchResult = await OfficialTemplateService.GetIndexAsync();
            if (!fetchResult.Success || fetchResult.Templates.Count == 0)
            {
                await ShowMessageAsync(
                    xamlRoot,
                    title,
                    string.IsNullOrWhiteSpace(fetchResult.Message)
                        ? I18n.GetString("OfficialTemplates_FetchIndexFailed")
                        : fetchResult.Message);
                return null;
            }

            var searchBox = new TextBox
            {
                Header = I18n.GetString("OfficialTemplates_SearchHeader"),
                PlaceholderText = I18n.GetString("OfficialTemplates_SearchPlaceholder"),
                Text = initialSearch ?? string.Empty
            };

            var templateCombo = new ComboBox
            {
                Header = I18n.GetString("OfficialTemplates_TemplateHeader"),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var sourceText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(fetchResult.Message)
                    ? I18n.Format("OfficialTemplates_SourceLabel", fetchResult.SourceDisplayName)
                    : I18n.Format("OfficialTemplates_SourceLabelWithHint", fetchResult.SourceDisplayName, fetchResult.Message),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75
            };

            var detailText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8
            };

            List<RemoteTemplateIndexItem> currentItems = new();

            void RebuildItems()
            {
                var keyword = searchBox.Text?.Trim() ?? string.Empty;
                currentItems = fetchResult.Templates
                    .Where(item => MatchesKeyword(item, keyword))
                    .ToList();

                templateCombo.Items.Clear();
                foreach (var item in currentItems)
                {
                    templateCombo.Items.Add(new ComboBoxItem
                    {
                        Content = BuildComboText(item),
                        Tag = item
                    });
                }

                templateCombo.SelectedIndex = templateCombo.Items.Count > 0 ? 0 : -1;
                RefreshDetails();
            }

            void RefreshDetails()
            {
                var item = (templateCombo.SelectedItem as ComboBoxItem)?.Tag as RemoteTemplateIndexItem;
                if (item == null)
                {
                    detailText.Text = I18n.GetString("OfficialTemplates_NoSearchResults");
                    return;
                }

                detailText.Text = BuildDetailText(item);
            }

            searchBox.TextChanged += (_, __) => RebuildItems();
            templateCombo.SelectionChanged += (_, __) => RefreshDetails();
            RebuildItems();

            var leftPanel = new StackPanel { Spacing = 12 };
            leftPanel.Children.Add(sourceText);
            leftPanel.Children.Add(searchBox);
            leftPanel.Children.Add(templateCombo);

            var detailPanel = new StackPanel { Spacing = 8 };
            detailPanel.Children.Add(new TextBlock
            {
                Text = I18n.GetString("OfficialTemplates_TemplateHeader"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            detailPanel.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = detailText,
                MinHeight = 260,
                MaxHeight = 420
            });

            var panel = new Grid
            {
                MinWidth = 760,
                MaxWidth = 980,
                ColumnSpacing = 16
            };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.Children.Add(leftPanel);
            Grid.SetColumn(detailPanel, 1);
            panel.Children.Add(detailPanel);

            var dialog = new ContentDialog
            {
                Title = title,
                Content = panel,
                PrimaryButtonText = I18n.GetString("Common_Confirm"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            if (await TemplateDialogCoordinatorService.ShowAsync(dialog, xamlRoot) != ContentDialogResult.Primary)
            {
                return null;
            }

            return (templateCombo.SelectedItem as ComboBoxItem)?.Tag as RemoteTemplateIndexItem;
        }

        public static async System.Threading.Tasks.Task<RemoteTemplateIndexItem?> PromptShareCodeAsync(XamlRoot xamlRoot)
        {
            var inputBox = new TextBox
            {
                Header = I18n.GetString("OfficialTemplates_ShareCodeHeader"),
                PlaceholderText = I18n.GetString("OfficialTemplates_ShareCodePlaceholder"),
                CharacterCasing = CharacterCasing.Upper,
                MaxLength = 5
            };
            var content = new StackPanel
            {
                Spacing = 12,
                MinWidth = 420,
                Children =
                {
                    inputBox
                }
            };

            while (true)
            {
                var dialog = new ContentDialog
                {
                    Title = I18n.GetString("OfficialTemplates_UseByShareCodeTitle"),
                    Content = content,
                    PrimaryButtonText = I18n.GetString("Common_Confirm"),
                    CloseButtonText = I18n.GetString("Common_Cancel"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = xamlRoot
                };

                if (await TemplateDialogCoordinatorService.ShowAsync(dialog, xamlRoot) != ContentDialogResult.Primary)
                {
                    return null;
                }

                var shareCode = (inputBox.Text ?? string.Empty).Trim().ToUpperInvariant();
                if (!OfficialTemplateService.IsValidShareCode(shareCode))
                {
                    await ShowMessageAsync(xamlRoot, I18n.GetString("OfficialTemplates_UseByShareCodeTitle"), I18n.GetString("OfficialTemplates_InvalidShareCode"));
                    continue;
                }

                var fetchResult = await OfficialTemplateService.GetIndexAsync();
                if (!fetchResult.Success)
                {
                    await ShowMessageAsync(xamlRoot, I18n.GetString("OfficialTemplates_UseByShareCodeTitle"), fetchResult.Message);
                    return null;
                }

                if (fetchResult.Templates.Count == 0)
                {
                    await ShowMessageAsync(
                        xamlRoot,
                        I18n.GetString("OfficialTemplates_UseByShareCodeTitle"),
                        string.IsNullOrWhiteSpace(fetchResult.Message)
                            ? I18n.GetString("OfficialTemplates_IndexEmpty")
                            : fetchResult.Message);
                    return null;
                }

                var item = fetchResult.Templates.FirstOrDefault(t => string.Equals(t.ShareCode, shareCode, StringComparison.OrdinalIgnoreCase));
                if (item == null)
                {
                    await ShowMessageAsync(xamlRoot, I18n.GetString("OfficialTemplates_UseByShareCodeTitle"), I18n.GetString("OfficialTemplates_ShareCodeNotFound"));
                    continue;
                }

                return item;
            }
        }

        private static bool MatchesKeyword(RemoteTemplateIndexItem item, string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return !item.IsDisabled;
            }

            if (item.IsDisabled)
            {
                return false;
            }

            return (item.Name?.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ?? false)
                || (item.GameName?.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ?? false)
                || (item.Description?.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ?? false)
                || (item.Author?.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ?? false)
                || (item.ShareCode?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
                || (item.SteamAppId?.ToString()?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        private static string BuildComboText(RemoteTemplateIndexItem item)
        {
            return string.IsNullOrWhiteSpace(item.ShareCode)
                ? item.DisplayName
                : $"{item.DisplayName} [{item.ShareCode}]";
        }

        private static string BuildDetailText(RemoteTemplateIndexItem item)
        {
            var lines = new List<string>
            {
                I18n.Format("OfficialTemplates_DetailName", item.Name),
                I18n.Format("OfficialTemplates_DetailGame", string.IsNullOrWhiteSpace(item.GameName) ? "-" : item.GameName),
                I18n.Format("OfficialTemplates_DetailShareCode", item.ShareCode),
                I18n.Format("OfficialTemplates_DetailSteamAppId", item.SteamAppId?.ToString() ?? "-"),
                I18n.Format("OfficialTemplates_DetailAuthor", string.IsNullOrWhiteSpace(item.Author) ? I18n.GetString("Template_Submission_AuthorAnonymous") : item.Author),
                I18n.Format("OfficialTemplates_DetailVersion", string.IsNullOrWhiteSpace(item.Version) ? "-" : item.Version)
            };

            if (!string.IsNullOrWhiteSpace(item.Description))
            {
                lines.Add(string.Empty);
                lines.Add(item.Description);
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static async System.Threading.Tasks.Task ShowMessageAsync(XamlRoot xamlRoot, string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = I18n.GetString("Common_Ok"),
                XamlRoot = xamlRoot
            };
            await TemplateDialogCoordinatorService.ShowAsync(dialog, xamlRoot);
        }
    }
}

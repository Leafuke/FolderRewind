using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace FolderRewind.Views
{
    public sealed partial class TemplateManagerDialog : ContentDialog
    {
        public TemplateManagerDialogViewModel ViewModel { get; } = new();

        public TemplateManagerDialog()
        {
            InitializeComponent();
            DataContext = ViewModel;
            XamlRoot = MainWindowService.GetXamlRoot();
            ThemeService.ApplyThemeToDialog(this);
        }

        private void OnSaveTemplateClick(object sender, RoutedEventArgs e)
        {
            ViewModel.SaveTemplate();
        }

        private void OnSaveRulesClick(object sender, RoutedEventArgs e)
        {
            ViewModel.SaveRules();
        }

        private void OnAddRuleClick(object sender, RoutedEventArgs e)
        {
            ViewModel.AddRule();
        }

        private void OnRemoveRuleClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string ruleId)
            {
                ViewModel.RemoveRule(ruleId);
            }
        }

        private void OnDuplicateTemplateClick(object sender, RoutedEventArgs e)
        {
            ViewModel.DuplicateTemplate();
        }

        private void OnDeleteTemplateClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ShowDeleteConfirm();
        }

        private void OnConfirmDeleteTemplateClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ConfirmDeleteTemplate();
        }

        private void OnCancelDeleteTemplateClick(object sender, RoutedEventArgs e)
        {
            ViewModel.CancelDeleteTemplate();
        }

        private async void OnExportTemplateClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedTemplate == null)
            {
                ViewModel.ExportSelectedTemplate(string.Empty);
                return;
            }

            var filePath = await MainWindowService.PickSaveFilePathAsync(
                string.Empty,
                "FolderRewind.TemplateManager.ExportTemplate",
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["JSON"] = new ReadOnlyCollection<string>(new[] { ".json" })
                },
                ViewModel.GetSuggestedExportFileName(),
                MainWindowService.SuggestedPickerLocation.DocumentsLibrary);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            ViewModel.ExportSelectedTemplate(filePath);
        }

        private void OnRefreshPreviewClick(object sender, RoutedEventArgs e)
        {
            ViewModel.RefreshPreview();
        }
    }
}

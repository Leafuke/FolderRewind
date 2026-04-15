using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using Windows.Storage.Pickers;

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

            var picker = new FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            picker.SuggestedFileName = ViewModel.GetSuggestedExportFileName();
            MainWindowService.InitializePicker(picker);

            var file = await picker.PickSaveFileAsync();
            if (file == null)
            {
                return;
            }

            ViewModel.ExportSelectedTemplate(file.Path);
        }

        private void OnRefreshPreviewClick(object sender, RoutedEventArgs e)
        {
            ViewModel.RefreshPreview();
        }
    }
}

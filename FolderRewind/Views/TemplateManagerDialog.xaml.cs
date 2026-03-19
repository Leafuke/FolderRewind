using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Windows.Storage.Pickers;

namespace FolderRewind.Views
{
    public sealed partial class TemplateManagerDialog : ContentDialog, INotifyPropertyChanged
    {
        public ObservableCollection<ConfigTemplate> TemplatesView { get; } = new();
        public ObservableCollection<TemplateRulePreviewItem> PreviewItems { get; } = new();

        public bool HasSelection => GetSelectedTemplate() != null;
        public bool IsPreviewEmpty => PreviewItems.Count == 0;
        public bool IsTemplateListEmpty => TemplatesView.Count == 0;

        private readonly List<ConfigTemplate> _allTemplates = new();
        private string _selectedTemplateId = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public TemplateManagerDialog()
        {
            this.InitializeComponent();
            this.XamlRoot = MainWindowService.GetXamlRoot();
            ThemeService.ApplyThemeToDialog(this);

            ReloadTemplates();
        }

        private void ReloadTemplates(string? preferredTemplateId = null)
        {
            _allTemplates.Clear();
            _allTemplates.AddRange(TemplateService.GetTemplates()
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase));

            ApplyFilter(preferredTemplateId);
            RaiseStatePropertiesChanged();
        }

        private void ApplyFilter(string? preferredTemplateId = null)
        {
            var keyword = SearchBox.Text?.Trim() ?? string.Empty;

            IEnumerable<ConfigTemplate> filtered = _allTemplates;
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                filtered = filtered.Where(t =>
                    (t.Name?.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ?? false)
                    || (t.Author?.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ?? false)
                    || (t.BaseConfigType?.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ?? false));
            }

            TemplatesView.Clear();
            foreach (var template in filtered)
            {
                TemplatesView.Add(template);
            }

            var targetId = preferredTemplateId;
            if (string.IsNullOrWhiteSpace(targetId) && !string.IsNullOrWhiteSpace(_selectedTemplateId))
            {
                targetId = _selectedTemplateId;
            }

            if (!string.IsNullOrWhiteSpace(targetId))
            {
                var target = TemplatesView.FirstOrDefault(t => string.Equals(t.Id, targetId, StringComparison.OrdinalIgnoreCase));
                if (target != null)
                {
                    TemplateListView.SelectedItem = target;
                }
                else
                {
                    TemplateListView.SelectedItem = TemplatesView.FirstOrDefault();
                }
            }
            else
            {
                TemplateListView.SelectedItem = TemplatesView.FirstOrDefault();
            }

            if (TemplateListView.SelectedItem == null)
            {
                _selectedTemplateId = string.Empty;
                ClearDetail();
            }

            RaiseStatePropertiesChanged();
        }

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter(_selectedTemplateId);
        }

        private void OnTemplateSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            HideDeleteConfirm();

            var selected = GetSelectedTemplate();
            if (selected == null)
            {
                _selectedTemplateId = string.Empty;
                ClearDetail();
                RaiseStatePropertiesChanged();
                return;
            }

            _selectedTemplateId = selected.Id;
            TemplateNameBox.Text = selected.Name;
            TemplateAuthorBox.Text = selected.Author;
            TemplateDescriptionBox.Text = selected.Description;

            var updatedText = selected.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
            var ruleCount = selected.PathRules?.Count ?? 0;
            TemplateMetaText.Text = I18n.Format(
                "TemplateManagerDialog_TemplateMetaFormat",
                selected.BaseConfigType,
                updatedText,
                ruleCount.ToString(CultureInfo.CurrentCulture));

            RefreshPreview();
            RaiseStatePropertiesChanged();
        }

        private void OnSaveTemplateClick(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedTemplate();
            if (selected == null)
            {
                ShowFeedback(I18n.GetString("Template_Manager_SelectFirst"), InfoBarSeverity.Warning);
                return;
            }

            var ok = TemplateService.UpdateTemplateMetadata(
                selected.Id,
                TemplateNameBox.Text,
                TemplateAuthorBox.Text,
                TemplateDescriptionBox.Text,
                out var message);

            ShowFeedback(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            if (!ok)
            {
                return;
            }

            ReloadTemplates(selected.Id);
        }

        private void OnDuplicateTemplateClick(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedTemplate();
            if (selected == null)
            {
                ShowFeedback(I18n.GetString("Template_Manager_SelectFirst"), InfoBarSeverity.Warning);
                return;
            }

            var result = TemplateService.DuplicateTemplate(selected.Id);
            ShowFeedback(result.Message, result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            if (!result.Success || result.Template == null)
            {
                return;
            }

            ReloadTemplates(result.Template.Id);
        }

        private void OnDeleteTemplateClick(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedTemplate();
            if (selected == null)
            {
                ShowFeedback(I18n.GetString("Template_Manager_SelectFirst"), InfoBarSeverity.Warning);
                return;
            }

            DeleteConfirmText.Text = I18n.Format("Template_Manager_DeleteConfirm", selected.Name);
            DeleteConfirmPanel.Visibility = Visibility.Visible;
        }

        private void OnConfirmDeleteTemplateClick(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedTemplate();
            if (selected == null)
            {
                HideDeleteConfirm();
                ShowFeedback(I18n.GetString("Template_Manager_SelectFirst"), InfoBarSeverity.Warning);
                return;
            }

            var ok = TemplateService.DeleteTemplate(selected.Id, out var message);
            HideDeleteConfirm();
            ShowFeedback(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            if (!ok)
            {
                return;
            }

            PreviewItems.Clear();
            PreviewSummaryText.Text = I18n.GetString("TemplateManagerDialog_PreviewSummary.Text");
            ReloadTemplates();
        }

        private void OnCancelDeleteTemplateClick(object sender, RoutedEventArgs e)
        {
            HideDeleteConfirm();
        }

        private async void OnExportTemplateClick(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedTemplate();
            if (selected == null)
            {
                ShowFeedback(I18n.GetString("Template_Manager_SelectFirst"), InfoBarSeverity.Warning);
                return;
            }

            var picker = new FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            picker.SuggestedFileName = $"FolderRewind_template_{SanitizeFileName(selected.Name)}";
            MainWindowService.InitializePicker(picker);

            var file = await picker.PickSaveFileAsync();
            if (file == null)
            {
                return;
            }

            var ok = TemplateService.ExportTemplate(selected.Id, file.Path, out var message);
            ShowFeedback(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }

        private void OnRefreshPreviewClick(object sender, RoutedEventArgs e)
        {
            RefreshPreview();
        }

        private void RefreshPreview()
        {
            PreviewItems.Clear();

            var selected = GetSelectedTemplate();
            if (selected == null)
            {
                PreviewSummaryText.Text = I18n.GetString("Template_Manager_SelectFirst");
                RaiseStatePropertiesChanged();
                return;
            }

            var result = TemplateService.PreviewTemplateRules(selected.Id);
            PreviewSummaryText.Text = result.Message;
            foreach (var item in result.Items)
            {
                PreviewItems.Add(item);
            }

            RaiseStatePropertiesChanged();
        }

        private ConfigTemplate? GetSelectedTemplate()
        {
            return TemplateListView?.SelectedItem as ConfigTemplate;
        }

        private void ClearDetail()
        {
            TemplateNameBox.Text = string.Empty;
            TemplateAuthorBox.Text = string.Empty;
            TemplateDescriptionBox.Text = string.Empty;
            TemplateMetaText.Text = I18n.GetString("TemplateManagerDialog_TemplateMeta.Text");
            PreviewItems.Clear();
            PreviewSummaryText.Text = I18n.GetString("TemplateManagerDialog_PreviewSummary.Text");
            HideDeleteConfirm();
        }

        private void HideDeleteConfirm()
        {
            DeleteConfirmPanel.Visibility = Visibility.Collapsed;
        }

        private void RaiseStatePropertiesChanged()
        {
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(IsPreviewEmpty));
            OnPropertyChanged(nameof(IsTemplateListEmpty));
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void ShowFeedback(string message, InfoBarSeverity severity)
        {
            FeedbackBar.Severity = severity;
            FeedbackBar.Message = message;
            FeedbackBar.IsOpen = !string.IsNullOrWhiteSpace(message);
        }

        private static string SanitizeFileName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "template";
            }

            var sanitized = name;
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                sanitized = sanitized.Replace(c, '_');
            }

            return string.IsNullOrWhiteSpace(sanitized) ? "template" : sanitized;
        }
    }
}

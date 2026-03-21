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
    public sealed class EditableTemplateRuleItem : ObservableObject
    {
        private string _id = Guid.NewGuid().ToString("N");
        private string _name = string.Empty;
        private string _displayPath = string.Empty;
        private string _confidenceText = "0.80";
        private bool _autoAdd = true;

        public string Id { get => _id; set => SetProperty(ref _id, value ?? string.Empty); }
        public string Name { get => _name; set => SetProperty(ref _name, value ?? string.Empty); }
        public string DisplayPath { get => _displayPath; set => SetProperty(ref _displayPath, value ?? string.Empty); }
        public string ConfidenceText { get => _confidenceText; set => SetProperty(ref _confidenceText, value ?? "0.80"); }
        public bool AutoAdd { get => _autoAdd; set => SetProperty(ref _autoAdd, value); }

        public double GetConfidence()
        {
            return double.TryParse(ConfidenceText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? Math.Clamp(value, 0.0, 1.0)
                : 0.8;
        }
    }

    public sealed partial class TemplateManagerDialog : ContentDialog, INotifyPropertyChanged
    {
        public ObservableCollection<ConfigTemplate> TemplatesView { get; } = new();
        public ObservableCollection<TemplateRulePreviewItem> PreviewItems { get; } = new();
        public ObservableCollection<EditableTemplateRuleItem> EditablePathRules { get; } = new();

        public bool HasSelection => GetSelectedTemplate() != null;
        public bool IsPreviewEmpty => PreviewItems.Count == 0;
        public bool IsTemplateListEmpty => TemplatesView.Count == 0;
        public bool IsRuleListEmpty => EditablePathRules.Count == 0;

        private readonly List<ConfigTemplate> _allTemplates = new();
        private string _selectedTemplateId = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public TemplateManagerDialog()
        {
            InitializeComponent();
            XamlRoot = MainWindowService.GetXamlRoot();
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

            var targetId = !string.IsNullOrWhiteSpace(preferredTemplateId) ? preferredTemplateId : _selectedTemplateId;
            TemplateListView.SelectedItem = !string.IsNullOrWhiteSpace(targetId)
                ? TemplatesView.FirstOrDefault(t => string.Equals(t.Id, targetId, StringComparison.OrdinalIgnoreCase)) ?? TemplatesView.FirstOrDefault()
                : TemplatesView.FirstOrDefault();

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

            ReloadEditableRules(selected);
            RefreshPreview();
            RaiseStatePropertiesChanged();
        }

        private void ReloadEditableRules(ConfigTemplate selected)
        {
            EditablePathRules.Clear();
            foreach (var item in TemplateService.BuildRuleEditItems(selected))
            {
                EditablePathRules.Add(new EditableTemplateRuleItem
                {
                    Id = item.Id,
                    Name = item.Name,
                    DisplayPath = item.DisplayPath,
                    ConfidenceText = item.Confidence.ToString("0.00", CultureInfo.InvariantCulture),
                    AutoAdd = item.AutoAdd
                });
            }

            RaiseStatePropertiesChanged();
        }

        private void OnSaveTemplateClick(object sender, RoutedEventArgs e)
        {
            try
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

                if (!ok)
                {
                    ShowFeedback(message, InfoBarSeverity.Error);
                    return;
                }

                var rulesSaved = SaveCurrentRules(selected.Id, out var rulesMessage);
                if (!rulesSaved)
                {
                    ShowFeedback(rulesMessage, InfoBarSeverity.Error);
                    return;
                }

                ShowFeedback(message, InfoBarSeverity.Success);
                ReloadTemplates(selected.Id);
            }
            catch (Exception ex)
            {
                LogService.Log($"Template manager save failed: {ex.Message}", LogLevel.Error);
                ShowFeedback(ex.Message, InfoBarSeverity.Error);
            }
        }

        private void OnSaveRulesClick(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedTemplate();
            if (selected == null)
            {
                ShowFeedback(I18n.GetString("Template_Manager_SelectFirst"), InfoBarSeverity.Warning);
                return;
            }

            var ok = SaveCurrentRules(selected.Id, out var message);
            ShowFeedback(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            if (!ok)
            {
                return;
            }

            ReloadTemplates(selected.Id);
        }

        private bool SaveCurrentRules(string templateId, out string message)
        {
            var items = EditablePathRules.Select(rule => new TemplateService.TemplateRuleEditItem
            {
                Id = rule.Id,
                Name = rule.Name,
                DisplayPath = rule.DisplayPath,
                Confidence = rule.GetConfidence(),
                AutoAdd = rule.AutoAdd
            }).ToList();

            return TemplateService.UpdateTemplatePathRules(templateId, items, out message);
        }

        private void OnAddRuleClick(object sender, RoutedEventArgs e)
        {
            EditablePathRules.Add(new EditableTemplateRuleItem
            {
                Name = I18n.GetString("Template_Preview_UnnamedRule"),
                DisplayPath = "{Documents}",
                ConfidenceText = "0.80"
            });
            RaiseStatePropertiesChanged();
        }

        private void OnRemoveRuleClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string id)
            {
                return;
            }

            var item = EditablePathRules.FirstOrDefault(rule => string.Equals(rule.Id, id, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                EditablePathRules.Remove(item);
                RaiseStatePropertiesChanged();
            }
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
            EditablePathRules.Clear();
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
            EditablePathRules.Clear();
            PreviewItems.Clear();
            PreviewSummaryText.Text = I18n.GetString("TemplateManagerDialog_PreviewSummary.Text");
            HideDeleteConfirm();
            RaiseStatePropertiesChanged();
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
            OnPropertyChanged(nameof(IsRuleListEmpty));
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

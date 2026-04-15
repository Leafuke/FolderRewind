using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace FolderRewind.ViewModels
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

    public sealed class TemplateManagerDialogViewModel : ViewModelBase
    {
        private readonly List<ConfigTemplate> _allTemplates = new();

        private string _searchText = string.Empty;
        private ConfigTemplate? _selectedTemplate;
        private string _selectedTemplateId = string.Empty;
        private string _templateName = string.Empty;
        private string _templateAuthor = string.Empty;
        private string _templateDescription = string.Empty;
        private string _templateMetaText = I18n.GetString("TemplateManagerDialog_TemplateMeta.Text");
        private string _previewSummaryText = I18n.GetString("TemplateManagerDialog_PreviewSummary.Text");
        private bool _isDeleteConfirmVisible;
        private string _deleteConfirmText = string.Empty;
        private bool _isFeedbackOpen;
        private string _feedbackMessage = string.Empty;
        private InfoBarSeverity _feedbackSeverity = InfoBarSeverity.Informational;

        public ObservableCollection<ConfigTemplate> TemplatesView { get; } = new();
        public ObservableCollection<TemplateRulePreviewItem> PreviewItems { get; } = new();
        public ObservableCollection<EditableTemplateRuleItem> EditablePathRules { get; } = new();

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value ?? string.Empty))
                {
                    ApplyFilter();
                }
            }
        }

        public ConfigTemplate? SelectedTemplate
        {
            get => _selectedTemplate;
            set
            {
                if (SetProperty(ref _selectedTemplate, value))
                {
                    OnSelectedTemplateChanged();
                }
            }
        }

        public string TemplateName
        {
            get => _templateName;
            set => SetProperty(ref _templateName, value ?? string.Empty);
        }

        public string TemplateAuthor
        {
            get => _templateAuthor;
            set => SetProperty(ref _templateAuthor, value ?? string.Empty);
        }

        public string TemplateDescription
        {
            get => _templateDescription;
            set => SetProperty(ref _templateDescription, value ?? string.Empty);
        }

        public string TemplateMetaText
        {
            get => _templateMetaText;
            private set => SetProperty(ref _templateMetaText, value ?? string.Empty);
        }

        public string PreviewSummaryText
        {
            get => _previewSummaryText;
            private set => SetProperty(ref _previewSummaryText, value ?? string.Empty);
        }

        public bool IsDeleteConfirmVisible
        {
            get => _isDeleteConfirmVisible;
            private set => SetProperty(ref _isDeleteConfirmVisible, value);
        }

        public string DeleteConfirmText
        {
            get => _deleteConfirmText;
            private set => SetProperty(ref _deleteConfirmText, value ?? string.Empty);
        }

        public bool IsFeedbackOpen
        {
            get => _isFeedbackOpen;
            set => SetProperty(ref _isFeedbackOpen, value);
        }

        public string FeedbackMessage
        {
            get => _feedbackMessage;
            private set => SetProperty(ref _feedbackMessage, value ?? string.Empty);
        }

        public InfoBarSeverity FeedbackSeverity
        {
            get => _feedbackSeverity;
            private set => SetProperty(ref _feedbackSeverity, value);
        }

        public bool HasSelection => SelectedTemplate != null;
        public bool IsPreviewEmpty => PreviewItems.Count == 0;
        public bool IsTemplateListEmpty => TemplatesView.Count == 0;
        public bool IsRuleListEmpty => EditablePathRules.Count == 0;

        public TemplateManagerDialogViewModel()
        {
            ReloadTemplates();
        }

        public void ReloadTemplates(string? preferredTemplateId = null)
        {
            _allTemplates.Clear();
            _allTemplates.AddRange(TemplateService.GetTemplates()
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase));

            ApplyFilter(preferredTemplateId);
        }

        public bool SaveTemplate()
        {
            try
            {
                if (SelectedTemplate == null)
                {
                    SetFeedback(I18n.GetString("Template_Manager_SelectFirst"), InfoBarSeverity.Warning);
                    return false;
                }

                var ok = TemplateService.UpdateTemplateMetadata(
                    SelectedTemplate.Id,
                    TemplateName,
                    TemplateAuthor,
                    TemplateDescription,
                    out var message);

                if (!ok)
                {
                    SetFeedback(message, InfoBarSeverity.Error);
                    return false;
                }

                // 先存元数据再存规则，失败时反馈会更接近用户刚改动的那一块。
                var rulesSaved = SaveRulesInternal(SelectedTemplate.Id, out var rulesMessage);
                if (!rulesSaved)
                {
                    SetFeedback(rulesMessage, InfoBarSeverity.Error);
                    return false;
                }

                SetFeedback(message, InfoBarSeverity.Success);
                ReloadTemplates(SelectedTemplate.Id);
                return true;
            }
            catch (Exception ex)
            {
                LogService.LogError($"Template manager save failed: {ex.Message}", nameof(TemplateManagerDialogViewModel), ex);
                SetFeedback(ex.Message, InfoBarSeverity.Error);
                return false;
            }
        }

        public bool SaveRules()
        {
            if (SelectedTemplate == null)
            {
                SetFeedback(I18n.GetString("Template_Manager_SelectFirst"), InfoBarSeverity.Warning);
                return false;
            }

            var ok = SaveRulesInternal(SelectedTemplate.Id, out var message);
            SetFeedback(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            if (ok)
            {
                ReloadTemplates(SelectedTemplate.Id);
            }

            return ok;
        }

        public void AddRule()
        {
            EditablePathRules.Add(new EditableTemplateRuleItem
            {
                Name = I18n.GetString("Template_Preview_UnnamedRule"),
                DisplayPath = "{Documents}",
                ConfidenceText = "0.80",
                AutoAdd = true
            });

            RaiseStatePropertiesChanged();
        }

        public void RemoveRule(string? ruleId)
        {
            if (string.IsNullOrWhiteSpace(ruleId))
            {
                return;
            }

            var item = EditablePathRules.FirstOrDefault(rule => string.Equals(rule.Id, ruleId, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                return;
            }

            EditablePathRules.Remove(item);
            RaiseStatePropertiesChanged();
        }

        public bool DuplicateTemplate()
        {
            if (SelectedTemplate == null)
            {
                SetFeedback(I18n.GetString("Template_Manager_SelectFirst"), InfoBarSeverity.Warning);
                return false;
            }

            var result = TemplateService.DuplicateTemplate(SelectedTemplate.Id);
            SetFeedback(result.Message, result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            if (!result.Success || result.Template == null)
            {
                return false;
            }

            ReloadTemplates(result.Template.Id);
            return true;
        }

        public void ShowDeleteConfirm()
        {
            if (SelectedTemplate == null)
            {
                SetFeedback(I18n.GetString("Template_Manager_SelectFirst"), InfoBarSeverity.Warning);
                return;
            }

            DeleteConfirmText = I18n.Format("Template_Manager_DeleteConfirm", SelectedTemplate.Name);
            IsDeleteConfirmVisible = true;
        }

        public bool ConfirmDeleteTemplate()
        {
            if (SelectedTemplate == null)
            {
                HideDeleteConfirm();
                SetFeedback(I18n.GetString("Template_Manager_SelectFirst"), InfoBarSeverity.Warning);
                return false;
            }

            var ok = TemplateService.DeleteTemplate(SelectedTemplate.Id, out var message);
            HideDeleteConfirm();
            SetFeedback(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            if (!ok)
            {
                return false;
            }

            PreviewItems.Clear();
            EditablePathRules.Clear();
            PreviewSummaryText = I18n.GetString("TemplateManagerDialog_PreviewSummary.Text");
            ReloadTemplates();
            return true;
        }

        public void CancelDeleteTemplate()
        {
            HideDeleteConfirm();
        }

        public bool ExportSelectedTemplate(string path)
        {
            if (SelectedTemplate == null)
            {
                SetFeedback(I18n.GetString("Template_Manager_SelectFirst"), InfoBarSeverity.Warning);
                return false;
            }

            var ok = TemplateService.ExportTemplate(SelectedTemplate.Id, path, out var message);
            SetFeedback(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            return ok;
        }

        public string GetSuggestedExportFileName()
        {
            var selectedTemplateName = SelectedTemplate?.Name;
            if (string.IsNullOrWhiteSpace(selectedTemplateName))
            {
                return "FolderRewind_template_template";
            }

            var sanitized = selectedTemplateName;
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            {
                sanitized = sanitized.Replace(c, '_');
            }

            return $"FolderRewind_template_{(string.IsNullOrWhiteSpace(sanitized) ? "template" : sanitized)}";
        }

        public void RefreshPreview()
        {
            PreviewItems.Clear();

            if (SelectedTemplate == null)
            {
                PreviewSummaryText = I18n.GetString("Template_Manager_SelectFirst");
                RaiseStatePropertiesChanged();
                return;
            }

            var result = TemplateService.PreviewTemplateRules(SelectedTemplate.Id);
            PreviewSummaryText = result.Message;
            foreach (var item in result.Items)
            {
                PreviewItems.Add(item);
            }

            RaiseStatePropertiesChanged();
        }

        private void ApplyFilter(string? preferredTemplateId = null)
        {
            var keyword = SearchText.Trim();

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

            var targetId = !string.IsNullOrWhiteSpace(preferredTemplateId)
                ? preferredTemplateId
                : _selectedTemplateId;

            // 刷新列表时尽量保持原选中项，避免用户编辑中被“跳选中”。
            SelectedTemplate = !string.IsNullOrWhiteSpace(targetId)
                ? TemplatesView.FirstOrDefault(t => string.Equals(t.Id, targetId, StringComparison.OrdinalIgnoreCase)) ?? TemplatesView.FirstOrDefault()
                : TemplatesView.FirstOrDefault();

            if (SelectedTemplate == null)
            {
                _selectedTemplateId = string.Empty;
                ClearDetail();
            }

            RaiseStatePropertiesChanged();
        }

        private void OnSelectedTemplateChanged()
        {
            HideDeleteConfirm();

            if (SelectedTemplate == null)
            {
                _selectedTemplateId = string.Empty;
                ClearDetail();
                RaiseStatePropertiesChanged();
                return;
            }

            _selectedTemplateId = SelectedTemplate.Id;
            TemplateName = SelectedTemplate.Name;
            TemplateAuthor = SelectedTemplate.Author;
            TemplateDescription = SelectedTemplate.Description;

            var updatedText = SelectedTemplate.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
            var ruleCount = SelectedTemplate.PathRules?.Count ?? 0;
            TemplateMetaText = I18n.Format(
                "TemplateManagerDialog_TemplateMetaFormat",
                SelectedTemplate.BaseConfigType,
                updatedText,
                ruleCount.ToString(CultureInfo.CurrentCulture));

            ReloadEditableRules(SelectedTemplate);
            RefreshPreview();
            RaiseStatePropertiesChanged();
        }

        private void ReloadEditableRules(ConfigTemplate selectedTemplate)
        {
            EditablePathRules.Clear();
            foreach (var item in TemplateService.BuildRuleEditItems(selectedTemplate))
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

        private bool SaveRulesInternal(string templateId, out string message)
        {
            // EditablePathRules 是 UI 编辑态；提交前统一映射成服务层的 DTO。
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

        private void ClearDetail()
        {
            TemplateName = string.Empty;
            TemplateAuthor = string.Empty;
            TemplateDescription = string.Empty;
            TemplateMetaText = I18n.GetString("TemplateManagerDialog_TemplateMeta.Text");
            EditablePathRules.Clear();
            PreviewItems.Clear();
            PreviewSummaryText = I18n.GetString("TemplateManagerDialog_PreviewSummary.Text");
            HideDeleteConfirm();
            RaiseStatePropertiesChanged();
        }

        private void HideDeleteConfirm()
        {
            IsDeleteConfirmVisible = false;
        }

        private void RaiseStatePropertiesChanged()
        {
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(IsPreviewEmpty));
            OnPropertyChanged(nameof(IsTemplateListEmpty));
            OnPropertyChanged(nameof(IsRuleListEmpty));
        }

        private void SetFeedback(string message, InfoBarSeverity severity)
        {
            FeedbackSeverity = severity;
            FeedbackMessage = message;
            IsFeedbackOpen = !string.IsNullOrWhiteSpace(message);
        }
    }
}

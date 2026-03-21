using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FolderRewind.Views
{
    public enum TemplateSubmissionDialogAction
    {
        None,
        ExportPackage,
        SubmitToGitHub
    }

    public sealed partial class TemplateSubmissionDialog : ContentDialog
    {
        private readonly List<ConfigTemplate> _templates = new();

        public TemplateSubmissionDialogAction RequestedAction { get; private set; }

        public ConfigTemplate? SelectedTemplate { get; private set; }

        public string SelectedGameName => GameNameBox.Text?.Trim() ?? string.Empty;

        public TemplateSubmissionDialog()
        {
            InitializeComponent();

            PrimaryButtonText = I18n.GetString("TemplateSubmissionDialog_ExportPackage");
            SecondaryButtonText = I18n.GetString("TemplateSubmissionDialog_SubmitToGitHub");
            CloseButtonText = I18n.GetString("Common_Cancel");

            PrimaryButtonClick += OnPrimaryButtonClick;
            SecondaryButtonClick += OnSecondaryButtonClick;
            Loaded += OnLoaded;

            LoadTemplates();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            await RefreshGitHubAuthStatusAsync(validateToken: false);
        }

        private void LoadTemplates()
        {
            _templates.Clear();
            _templates.AddRange(TemplateService.GetTemplates()
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase));

            TemplateComboBox.Items.Clear();
            foreach (var template in _templates)
            {
                TemplateComboBox.Items.Add(new ComboBoxItem
                {
                    Content = template.Name,
                    Tag = template
                });
            }

            if (_templates.Count > 0)
            {
                TemplateComboBox.SelectedIndex = 0;
            }
            else
            {
                RefreshSelectedTemplateMetadata();
            }

            IsPrimaryButtonEnabled = _templates.Count > 0;
            IsSecondaryButtonEnabled = _templates.Count > 0 && GitHubOAuthService.IsConfigured(out _);
        }

        private void RefreshSelectedTemplateMetadata()
        {
            if (GetSelectedTemplate() is not ConfigTemplate selected)
            {
                GameNameBox.Text = string.Empty;
                TemplateMetaTextBlock.Text = I18n.GetString("TemplateSubmissionDialog_TemplateMeta.Text");
                return;
            }

            GameNameBox.Text = selected.GameName ?? string.Empty;
            TemplateMetaTextBlock.Text = I18n.Format(
                "TemplateSubmissionDialog_TemplateMetaFormat",
                string.IsNullOrWhiteSpace(selected.Author) ? I18n.GetString("Template_Submission_AuthorAnonymous") : selected.Author,
                string.IsNullOrWhiteSpace(selected.BaseConfigType) ? "Default" : selected.BaseConfigType,
                (selected.PathRules?.Count ?? 0).ToString(CultureInfo.CurrentCulture));
        }

        private async System.Threading.Tasks.Task RefreshGitHubAuthStatusAsync(bool validateToken)
        {
            var state = await GitHubOAuthService.GetAuthenticationStateAsync(validateToken);
            GitHubAuthStatusTextBlock.Text = state.Message;
            IsSecondaryButtonEnabled = _templates.Count > 0 && state.IsConfigured;
            RefreshGitHubStatusButton.IsEnabled = state.IsConfigured;
            GitHubSignOutButton.IsEnabled = state.HasToken;
        }

        private void ShowFeedback(string message, InfoBarSeverity severity)
        {
            FeedbackBar.Severity = severity;
            FeedbackBar.Message = message;
            FeedbackBar.IsOpen = true;
        }

        private ConfigTemplate? GetSelectedTemplate()
        {
            return (TemplateComboBox.SelectedItem as ComboBoxItem)?.Tag as ConfigTemplate;
        }

        private bool TryPrepareAction(ContentDialogButton button, ContentDialogButtonClickEventArgs args)
        {
            SelectedTemplate = GetSelectedTemplate();
            if (SelectedTemplate == null)
            {
                ShowFeedback(I18n.GetString("Template_Export_TemplateNotFound"), InfoBarSeverity.Warning);
                args.Cancel = true;
                return false;
            }

            RequestedAction = button == ContentDialogButton.Primary
                ? TemplateSubmissionDialogAction.ExportPackage
                : TemplateSubmissionDialogAction.SubmitToGitHub;
            return true;
        }

        private void OnTemplateSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshSelectedTemplateMetadata();
        }

        private async void OnGitHubSignOutClick(object sender, RoutedEventArgs e)
        {
            GitHubOAuthService.SignOut();
            ShowFeedback(I18n.GetString("GitHubOAuth_SignedOut"), InfoBarSeverity.Success);
            await RefreshGitHubAuthStatusAsync(validateToken: false);
        }

        private async void OnRefreshGitHubStatusClick(object sender, RoutedEventArgs e)
        {
            await RefreshGitHubAuthStatusAsync(validateToken: true);
        }

        private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            TryPrepareAction(ContentDialogButton.Primary, args);
        }

        private void OnSecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            TryPrepareAction(ContentDialogButton.Secondary, args);
        }
    }
}

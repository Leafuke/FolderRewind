using FolderRewind.Models;
using FolderRewind.Services.Discovery;
using FolderRewind.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed record HomeOfficialTemplateImportResult(
    bool Success,
    bool Canceled,
    string Message,
    BackupPreset? ImportedTemplate);

internal interface IHomeInteractionService
{
    Task ShowMessageAsync(string title, string message, CancellationToken cancellationToken = default);

    Task<bool> ConfirmConfigDeletionAsync(
        string configName,
        CancellationToken cancellationToken = default);

    Task<string?> RequestEncryptionPasswordAsync(CancellationToken cancellationToken = default);

    Task<List<ManagedFolder>?> ConfirmTemplateFoldersAsync(
        BackupPreset template,
        IReadOnlyList<BackupPresetService.TemplateFolderCandidate> candidates,
        CancellationToken cancellationToken = default);

    Task<HomeOfficialTemplateImportResult> PickAndImportOfficialTemplateAsync(
        string searchText,
        CancellationToken cancellationToken = default);

    Task<string?> PickPluginBatchRootAsync(CancellationToken cancellationToken = default);

    void NavigateToManager(string configId);

    void NavigateToGameDiscovery(GameDiscoveryNavigationParameter? parameter = null);

    void NotifyError(string message);
}

internal sealed class HomeInteractionService(Func<XamlRoot?> xamlRootProvider) : IHomeInteractionService
{
    public Task ShowMessageAsync(string title, string message, CancellationToken cancellationToken = default)
        => AppDialogService.Default.ShowMessageAsync(
            title,
            message,
            GetXamlRoot(),
            cancellationToken: cancellationToken);

    public Task<bool> ConfirmConfigDeletionAsync(
        string configName,
        CancellationToken cancellationToken = default)
    {
        var resources = AppResourceLoader.GetForViewIndependentUse();
        return AppDialogService.Default.ConfirmAsync(
            resources.GetString("HomePage_ContextMenu_DeleteConfirm_Title"),
            string.Format(
                CultureInfo.CurrentCulture,
                resources.GetString("HomePage_ContextMenu_DeleteConfirm_Content"),
                configName),
            resources.GetString("HomePage_ContextMenu_DeleteConfirm_Delete"),
            GetXamlRoot(),
            isDestructive: true,
            cancellationToken);
    }

    public async Task<string?> RequestEncryptionPasswordAsync(CancellationToken cancellationToken = default)
    {
        var resources = AppResourceLoader.GetForViewIndependentUse();
        cancellationToken.ThrowIfCancellationRequested();
        var passwordBox = new PasswordBox
        {
            PlaceholderText = resources.GetString("Encryption_SetPasswordPlaceholder")
        };
        var confirmBox = new PasswordBox
        {
            PlaceholderText = resources.GetString("Encryption_ConfirmPasswordPlaceholder")
        };
        AutomationProperties.SetAutomationId(passwordBox, "NewConfigPassword");
        AutomationProperties.SetAutomationId(confirmBox, "NewConfigPasswordConfirm");
        AutomationProperties.SetName(passwordBox, resources.GetString("Encryption_SetPasswordPlaceholder"));
        AutomationProperties.SetName(confirmBox, resources.GetString("Encryption_ConfirmPasswordPlaceholder"));

        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(new TextBlock
        {
            Text = resources.GetString("Encryption_SetPasswordDesc"),
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(passwordBox);
        stack.Children.Add(confirmBox);
        stack.Children.Add(new InfoBar
        {
            Message = resources.GetString("Encryption_PasswordWarning"),
            Severity = InfoBarSeverity.Warning,
            IsOpen = true,
            IsClosable = false
        });

        var dialog = new ContentDialog
        {
            Title = resources.GetString("Encryption_SetPasswordTitle"),
            Content = stack,
            PrimaryButtonText = resources.GetString("Common_Ok"),
            CloseButtonText = resources.GetString("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        var validation = new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            IsClosable = false
        };
        AutomationProperties.SetAutomationId(validation, "NewConfigPasswordValidation");
        stack.Children.Add(validation);
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var empty = string.IsNullOrEmpty(passwordBox.Password);
            var mismatch = !string.Equals(passwordBox.Password, confirmBox.Password, StringComparison.Ordinal);
            if (!empty && !mismatch) return;

            args.Cancel = true;
            validation.Message = resources.GetString(empty ? "Encryption_PasswordEmpty" : "Encryption_PasswordMismatch");
            validation.IsOpen = true;
            (empty ? passwordBox : confirmBox).Focus(FocusState.Programmatic);
        };

        return await AppDialogService.Default.ShowCustomAsync(dialog, GetXamlRoot(), cancellationToken)
            == ContentDialogResult.Primary ? passwordBox.Password : null;
    }

    public async Task<List<ManagedFolder>?> ConfirmTemplateFoldersAsync(
        BackupPreset template,
        IReadOnlyList<BackupPresetService.TemplateFolderCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0)
        {
            return [];
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
        if (!candidates.Any(candidate => candidate.IsSelectedByDefault))
        {
            panel.Children.Add(new InfoBar
            {
                Message = I18n.GetString("Template_CreateFrom_Home_SelectFoldersHint"),
                Severity = InfoBarSeverity.Warning,
                IsOpen = true,
                IsClosable = false
            });
        }

        var listPanel = new StackPanel { Spacing = 10 };
        var entries = new List<(CheckBox Box, BackupPresetService.TemplateFolderCandidate Candidate)>();
        foreach (var candidate in candidates
                     .OrderByDescending(item => item.IsSelectedByDefault)
                     .ThenByDescending(item => item.Confidence)
                     .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var checkBox = new CheckBox
            {
                Content = string.IsNullOrWhiteSpace(candidate.DisplayName)
                    ? candidate.Path
                    : candidate.DisplayName,
                IsChecked = candidate.IsSelectedByDefault
            };
            AutomationProperties.SetAutomationId(checkBox, $"TemplateFolder_{entries.Count}");

            var detail = new StackPanel { Spacing = 2 };
            detail.Children.Add(checkBox);
            detail.Children.Add(CreateCandidateDetailText(I18n.Format(
                "Template_CreateFrom_Home_FolderCandidateMeta",
                candidate.RuleName,
                candidate.Confidence.ToString("P0", CultureInfo.CurrentCulture),
                candidate.IsSelectedByDefault
                    ? I18n.GetString("Template_CreateFrom_Home_FolderCandidateAuto")
                    : I18n.GetString("Template_CreateFrom_Home_FolderCandidateSuggested"))));
            detail.Children.Add(CreateCandidateDetailText(candidate.Path));
            if (!string.IsNullOrWhiteSpace(candidate.MarkerSummary))
            {
                detail.Children.Add(CreateCandidateDetailText(candidate.MarkerSummary));
            }
            listPanel.Children.Add(detail);
            entries.Add((checkBox, candidate));
        }
        panel.Children.Add(new ScrollViewer { Content = listPanel, MaxHeight = 360 });

        var dialog = new ContentDialog
        {
            Title = I18n.GetString("Template_CreateFrom_Home_SelectFoldersTitle"),
            Content = panel,
            PrimaryButtonText = I18n.GetString("Common_Confirm"),
            CloseButtonText = I18n.GetString("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await AppDialogService.Default.ShowCustomAsync(dialog, GetXamlRoot(), cancellationToken)
            != ContentDialogResult.Primary)
        {
            return null;
        }

        return entries
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

    public async Task<HomeOfficialTemplateImportResult> PickAndImportOfficialTemplateAsync(
        string searchText,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = GetRequiredXamlRoot();
        var item = await OfficialTemplateDialogService.PickTemplateAsync(
            root,
            I18n.GetString("OfficialTemplates_CreateFromOfficialTitle"),
            searchText);
        cancellationToken.ThrowIfCancellationRequested();
        if (item is null)
        {
            return new(false, true, string.Empty, null);
        }

        var result = await OfficialTemplateImportService.ImportTemplateAsync(root, item, cancellationToken);
        return new(result.Success, result.Canceled, result.Message, result.ImportedTemplate);
    }

    public async Task<string?> PickPluginBatchRootAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = await MainWindowService.PickFolderPathAsync(
            AppResourceLoader.GetForViewIndependentUse().GetString("HomePage_PluginBatchCreatePickRootTitle"),
            "FolderRewind.HomePage.PluginBatch.Root",
            MainWindowService.SuggestedPickerLocation.ComputerFolder);
        cancellationToken.ThrowIfCancellationRequested();
        return path;
    }

    public void NavigateToManager(string configId)
        => _ = NavigationService.NavigateTo("Manager", ManagerNavigationParameter.ForConfig(configId));

    public void NavigateToGameDiscovery(GameDiscoveryNavigationParameter? parameter = null)
        => _ = NavigationService.NavigateTo("GameDiscovery", parameter);

    public void NotifyError(string message) => NotificationService.ShowError(message);

    private XamlRoot? GetXamlRoot() => xamlRootProvider() ?? MainWindowService.GetXamlRoot();

    private XamlRoot GetRequiredXamlRoot()
        => GetXamlRoot() ?? throw new InvalidOperationException("The home page is not attached to a XamlRoot.");

    private static TextBlock CreateCandidateDetailText(string text)
        => new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Margin = new Thickness(28, 0, 0, 0)
        };

    private static string ResolveFolderCoverImagePath(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return string.Empty;
        }

        try
        {
            var iconPath = Path.Combine(folderPath, "icon.png");
            return File.Exists(iconPath) ? iconPath : string.Empty;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }
}

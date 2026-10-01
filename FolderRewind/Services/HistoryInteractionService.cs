using FolderRewind.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed class HistoryInteractionService(Func<XamlRoot?> xamlRootProvider) : IHistoryInteractionService
{
    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryButtonText,
        bool isDestructive = false,
        CancellationToken cancellationToken = default)
        => UiDispatcherService.RunOnUiAsync(() => AppDialogService.Default.ConfirmAsync(
            title,
            message,
            primaryButtonText,
            GetXamlRoot(),
            isDestructive,
            cancellationToken: cancellationToken));

    public Task<string?> RequestTextAsync(
        string title,
        string message,
        string initialValue = "",
        bool isPassword = false,
        CancellationToken cancellationToken = default)
        => UiDispatcherService.RunOnUiAsync(async () =>
    {
        if (!isPassword)
        {
            return await AppDialogService.Default.RequestTextAsync(
                title,
                message,
                initialValue,
                message,
                xamlRoot: GetXamlRoot(),
                cancellationToken: cancellationToken);
        }

        var passwordBox = new PasswordBox { PlaceholderText = message };
        AutomationProperties.SetAutomationId(passwordBox, "HistoryRestorePassword");
        AutomationProperties.SetName(passwordBox, message);
        var dialog = new ContentDialog
        {
            Title = title,
            Content = passwordBox,
            PrimaryButtonText = I18n.GetString("Common_Ok"),
            CloseButtonText = I18n.GetString("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await AppDialogService.Default.ShowCustomAsync(
            dialog,
            GetXamlRoot(),
            cancellationToken);
        return result == ContentDialogResult.Primary ? passwordBox.Password : null;
    });

    public Task<HistoryInteractionResult> ChooseAsync(
        HistoryChoiceRequest request,
        CancellationToken cancellationToken = default)
        => UiDispatcherService.RunOnUiAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(request);
        var choices = new ComboBox
        {
            ItemsSource = request.Options,
            DisplayMemberPath = nameof(HistoryChoiceOption.DisplayName),
            SelectedValuePath = nameof(HistoryChoiceOption.Value),
            MinWidth = 320
        };
        AutomationProperties.SetAutomationId(choices, "HistoryInteractionChoice");
        AutomationProperties.SetName(choices, request.Title);
        choices.SelectedValue = request.InitialValue;
        if (choices.SelectedIndex < 0 && request.Options.Count > 0)
        {
            choices.SelectedIndex = 0;
        }

        var content = new StackPanel { Spacing = 12, MaxWidth = 480 };
        foreach (var field in request.Fields ?? [])
        {
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(new TextBlock
            {
                Text = field.Label,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
            });
            row.Children.Add(new TextBlock { Text = field.Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            content.Children.Add(row);
        }
        if (!string.IsNullOrWhiteSpace(request.Message))
        {
            content.Children.Add(new TextBlock
            {
                Text = request.Message,
                TextWrapping = TextWrapping.Wrap
            });
        }
        if (request.Options.Count > 0)
        {
            content.Children.Add(choices);
        }

        var dialog = new ContentDialog
        {
            Title = request.Title,
            Content = content,
            PrimaryButtonText = request.PrimaryButtonText,
            SecondaryButtonText = request.SecondaryButtonText ?? string.Empty,
            CloseButtonText = I18n.GetString("Common_Cancel"),
            DefaultButton = request.IsDestructive
                ? ContentDialogButton.Close
                : ContentDialogButton.Primary
        };
        var result = await AppDialogService.Default.ShowCustomAsync(
            dialog,
            GetXamlRoot(),
            cancellationToken);
        return new HistoryInteractionResult(
            result switch
            {
                ContentDialogResult.Primary => HistoryInteractionOutcome.Primary,
                ContentDialogResult.Secondary => HistoryInteractionOutcome.Secondary,
                _ => HistoryInteractionOutcome.Cancelled
            },
            choices.SelectedValue as string);
    });

    public Task<string?> PickFolderAsync(CancellationToken cancellationToken = default)
        => UiDispatcherService.RunOnUiAsync(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = await MainWindowService.PickFolderPathAsync(
            string.Empty,
            "FolderRewind.History.ScanRecover",
            MainWindowService.SuggestedPickerLocation.DocumentsLibrary);
        cancellationToken.ThrowIfCancellationRequested();
        return path;
    });

    public Task OpenCloudSyncAsync(string configId, CancellationToken cancellationToken = default)
        => UiDispatcherService.RunOnUiAsync(async () =>
    {
        var config = ConfigService.CurrentConfig?.BackupConfigs
            .FirstOrDefault(item => string.Equals(item.Id, configId, StringComparison.OrdinalIgnoreCase));
        if (config is null)
        {
            return;
        }

        var dialog = new ConfigCloudSyncDialog(config);
        await AppDialogService.Default.ShowCustomAsync(dialog, GetXamlRoot(), cancellationToken);
    });

    public void Notify(HistoryNotificationKind kind, string message)
    {
        switch (kind)
        {
            case HistoryNotificationKind.Success:
                NotificationService.ShowSuccess(message);
                break;
            case HistoryNotificationKind.Warning:
                NotificationService.ShowWarning(message);
                break;
            case HistoryNotificationKind.Error:
                NotificationService.ShowError(message);
                break;
            default:
                NotificationService.ShowInfo(message);
                break;
        }
    }

    public void NotifyRestoreCompleted(string targetName, bool success, string? detail = null)
        => NotificationService.NotifyRestoreCompleted(targetName, success, detail);

    private XamlRoot? GetXamlRoot() => xamlRootProvider() ?? MainWindowService.GetXamlRoot();
}

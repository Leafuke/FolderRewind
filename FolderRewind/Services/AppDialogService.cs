using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal interface IAppDialogService
{
    Task ShowMessageAsync(
        string title,
        string message,
        XamlRoot? xamlRoot = null,
        string? closeButtonText = null,
        CancellationToken cancellationToken = default);

    Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryButtonText,
        XamlRoot? xamlRoot = null,
        bool isDestructive = false,
        CancellationToken cancellationToken = default);

    Task<string?> RequestTextAsync(
        string title,
        string inputName,
        string? initialValue = null,
        string? placeholderText = null,
        string? primaryButtonText = null,
        bool acceptsReturn = false,
        XamlRoot? xamlRoot = null,
        CancellationToken cancellationToken = default);

    Task<ContentDialogResult> ShowCustomAsync(
        ContentDialog dialog,
        XamlRoot? xamlRoot = null,
        CancellationToken cancellationToken = default);

    Task HideCustomAsync(ContentDialog? dialog);
}

internal sealed class AppDialogService : IAppDialogService
{
    private readonly AsyncOperationQueue _queue = new();

    public static IAppDialogService Default { get; } = new AppDialogService();

    public async Task ShowMessageAsync(
        string title,
        string message,
        XamlRoot? xamlRoot = null,
        string? closeButtonText = null,
        CancellationToken cancellationToken = default)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = closeButtonText ?? I18n.GetString("Common_Ok"),
            DefaultButton = ContentDialogButton.Close
        };

        await ShowCustomAsync(dialog, xamlRoot, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryButtonText,
        XamlRoot? xamlRoot = null,
        bool isDestructive = false,
        CancellationToken cancellationToken = default)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = I18n.GetString("Common_Cancel"),
            DefaultButton = isDestructive ? ContentDialogButton.Close : ContentDialogButton.Primary
        };

        return await ShowCustomAsync(dialog, xamlRoot, cancellationToken).ConfigureAwait(false) == ContentDialogResult.Primary;
    }

    public async Task<string?> RequestTextAsync(
        string title,
        string inputName,
        string? initialValue = null,
        string? placeholderText = null,
        string? primaryButtonText = null,
        bool acceptsReturn = false,
        XamlRoot? xamlRoot = null,
        CancellationToken cancellationToken = default)
    {
        var input = new TextBox
        {
            Text = initialValue ?? string.Empty,
            PlaceholderText = placeholderText ?? string.Empty,
            AcceptsReturn = acceptsReturn,
            TextWrapping = acceptsReturn ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinWidth = 320
        };
        AutomationProperties.SetName(input, inputName);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = input,
            PrimaryButtonText = primaryButtonText ?? I18n.GetString("Common_Ok"),
            CloseButtonText = I18n.GetString("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await ShowCustomAsync(dialog, xamlRoot, cancellationToken).ConfigureAwait(false);
        return result == ContentDialogResult.Primary ? input.Text.Trim() : null;
    }

    public Task<ContentDialogResult> ShowCustomAsync(
        ContentDialog dialog,
        XamlRoot? xamlRoot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        return _queue.EnqueueAsync(
            async queueCancellationToken => await UiDispatcherService.RunOnUiAsync(async () =>
            {
                dialog.XamlRoot ??= xamlRoot ?? MainWindowService.GetXamlRoot();
                ThemeService.ApplyThemeToDialog(dialog);
                queueCancellationToken.ThrowIfCancellationRequested();

                using var cancellationRegistration = queueCancellationToken.Register(
                    static state => _ = HideCustomAsync((ContentDialog)state!),
                    dialog);
                return await dialog.ShowAsync();
            }).ConfigureAwait(false),
            cancellationToken);
    }

    public static Task HideCustomAsync(ContentDialog? dialog)
    {
        if (dialog is null)
        {
            return Task.CompletedTask;
        }

        return UiDispatcherService.RunOnUiAsync(() =>
        {
            try
            {
                dialog.Hide();
            }
            catch (Exception ex)
            {
                LogService.LogWarning($"Failed to hide dialog: {ex.Message}", nameof(AppDialogService));
            }
        });
    }

    Task IAppDialogService.HideCustomAsync(ContentDialog? dialog) => HideCustomAsync(dialog);
}

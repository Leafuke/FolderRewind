using FolderRewind.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static class WindowCloseService
{
    public static WindowCloseController Create(Func<XamlRoot?> root, Action hide, Action close)
        => new(GetRememberedChoice, () => AskAsync(root()), SaveChoiceAsync, hide, close, ex =>
        {
            LogService.LogError("Close interaction failed.", nameof(WindowCloseService), ex);
            NotificationService.ShowError(ex.Message);
        });

    private static WindowCloseChoice? GetRememberedChoice()
    {
        var settings = ConfigService.CurrentConfig?.GlobalSettings;
        if (settings is null) return WindowCloseChoice.Exit;
        if (!settings.RememberCloseBehavior) return null;
        return settings.CloseBehavior == CloseBehavior.MinimizeToTray ? WindowCloseChoice.Hide : WindowCloseChoice.Exit;
    }

    private static Task SaveChoiceAsync(WindowCloseChoice choice)
    {
        var settings = ConfigService.CurrentConfig.GlobalSettings;
        var beforeChoice = settings.CloseBehavior;
        var beforeRemember = settings.RememberCloseBehavior;
        return ConfigEditTransaction.ApplyAsync(() =>
        {
            settings.CloseBehavior = choice == WindowCloseChoice.Hide ? CloseBehavior.MinimizeToTray : CloseBehavior.Exit;
            settings.RememberCloseBehavior = true;
        }, () =>
        {
            settings.CloseBehavior = beforeChoice;
            settings.RememberCloseBehavior = beforeRemember;
        }, () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"));
    }

    private static async Task<WindowCloseAnswer> AskAsync(XamlRoot? root)
    {
        var remember = new CheckBox { Content = I18n.GetString("CloseDialog_Remember") };
        AutomationProperties.SetAutomationId(remember, "CloseRememberChoice");
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = I18n.GetString("CloseDialog_Content"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(remember);
        var result = await AppDialogService.Default.ShowRequestAsync(new()
        {
            Title = I18n.GetString("CloseDialog_Title"), Content = panel,
            PrimaryButtonText = I18n.GetString("CloseDialog_MinimizeToTray"),
            SecondaryButtonText = I18n.GetString("CloseDialog_Exit"),
            CloseButtonText = I18n.GetString("Common_Cancel"), DefaultButton = ContentDialogButton.Primary
        }, root);
        return new(result switch
        {
            ContentDialogResult.Primary => WindowCloseChoice.Hide,
            ContentDialogResult.Secondary => WindowCloseChoice.Exit,
            _ => WindowCloseChoice.Cancel
        }, remember.IsChecked == true);
    }
}

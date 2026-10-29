using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace FolderRewind.Views;

public sealed partial class ConfigSettingsPage
{
    private void OnSettingsPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        SettingsLayout.Margin = PageLayout.Insets(e.NewSize.Width);
        var contentWidth = Math.Max(0, e.NewSize.Width - SettingsLayout.Margin.Left - SettingsLayout.Margin.Right);
        var narrow = contentWidth < 620;
        Grid.SetColumn(SettingsSearchBox, narrow ? 0 : 1);
        Grid.SetRow(SettingsSearchBox, narrow ? 1 : 0);
        Grid.SetColumnSpan(SettingsSearchBox, narrow ? 2 : 1);
        SettingsSearchBox.Width = Math.Min(280, contentWidth);
        Grid.SetRow(DraftButtons, narrow ? 1 : 0);
        Grid.SetColumn(DraftButtons, narrow ? 0 : 1);
        Grid.SetColumnSpan(DraftButtons, narrow ? 2 : 1);
    }

    private void OnSettingCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not SettingsCard card) return;
        var narrow = e.NewSize.Width < 680;
        card.ContentAlignment = narrow ? ContentAlignment.Vertical : ContentAlignment.Right;
        if (card.Content is FrameworkElement content)
            content.Width = narrow ? double.NaN : 320;
    }
}

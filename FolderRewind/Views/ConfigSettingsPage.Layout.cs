using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace FolderRewind.Views;

public sealed partial class ConfigSettingsPage
{
    private void OnSettingsPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        SettingsLayout.Width = Math.Min(1040, Math.Max(0, e.NewSize.Width - 48));
        var narrow = SettingsLayout.Width < 620;
        Grid.SetColumn(SettingsSearchBox, narrow ? 0 : 1);
        Grid.SetRow(SettingsSearchBox, narrow ? 1 : 0);
        SettingsSearchBox.Width = narrow ? double.NaN : 280;
        SettingsHeaderGrid.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 1, GridUnitType.Auto);
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

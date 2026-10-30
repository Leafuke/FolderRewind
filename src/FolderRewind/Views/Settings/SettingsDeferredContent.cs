using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderRewind.Views.Settings;

internal static class SettingsDeferredContent
{
    // Items is an IList<object> and rejects XAML's DeferredElement. A Grid hosts the
    // deferred panel instead; apply the same row styles the Toolkit applies to direct items.
    internal static void ApplyCardStyles(StackPanel? body, SettingsExpander? expander)
    {
        if (body is null || expander?.ItemContainerStyleSelector is not { } selector) return;
        foreach (var child in body.Children)
        {
            if (child is SettingsCard card && card.ReadLocalValue(FrameworkElement.StyleProperty) == DependencyProperty.UnsetValue)
                card.Style = selector.SelectStyle(null, card);
        }
    }
}

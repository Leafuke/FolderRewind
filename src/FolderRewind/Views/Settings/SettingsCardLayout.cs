using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;

namespace FolderRewind.Views;

internal static class SettingsCardLayout
{
    internal static void Apply(object sender, SizeChangedEventArgs args)
    {
        if (sender is not SettingsCard card) return;
        var narrow = args.NewSize.Width < 680;
        var alignment = narrow ? ContentAlignment.Vertical : ContentAlignment.Right;
        if (card.ContentAlignment != alignment) card.ContentAlignment = alignment;
        if (card.Content is FrameworkElement content)
        {
            var width = narrow ? double.NaN : 320;
            if (content.Width != width && !(double.IsNaN(content.Width) && double.IsNaN(width)))
                content.Width = width;
        }
    }
}

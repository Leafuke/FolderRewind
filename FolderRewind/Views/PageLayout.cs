using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderRewind.Views;

/// <summary>Shared page insets; individual pages retain control of their content layout.</summary>
public sealed class PageLayout : DependencyObject
{
    public static readonly DependencyProperty ResponsivePaddingProperty = DependencyProperty.RegisterAttached(
        "ResponsivePadding", typeof(bool), typeof(PageLayout), new PropertyMetadata(false, OnChanged));

    public static bool GetResponsivePadding(DependencyObject value) => (bool)value.GetValue(ResponsivePaddingProperty);
    public static void SetResponsivePadding(DependencyObject value, bool enabled) => value.SetValue(ResponsivePaddingProperty, enabled);
    public static Thickness Insets(double width) => new(width < 640 ? 16 : 24);

    private static void OnChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not Grid grid) return;
        grid.SizeChanged -= OnSizeChanged;
        if (args.NewValue is true)
        {
            grid.SizeChanged += OnSizeChanged;
            grid.Padding = Insets(grid.ActualWidth);
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs args)
        => ((Grid)sender).Padding = Insets(args.NewSize.Width);
}

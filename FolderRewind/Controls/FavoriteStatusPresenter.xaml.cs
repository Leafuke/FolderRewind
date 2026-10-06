using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderRewind.Controls;

public sealed partial class FavoriteStatusPresenter : UserControl
{
    public static readonly DependencyProperty IsFavoriteProperty = DependencyProperty.Register(
        nameof(IsFavorite), typeof(bool), typeof(FavoriteStatusPresenter),
        new PropertyMetadata(false, OnFavoriteChanged));

    public FavoriteStatusPresenter()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdatePresentation();
    }

    public bool IsFavorite
    {
        get => (bool)GetValue(IsFavoriteProperty);
        set => SetValue(IsFavoriteProperty, value);
    }

    private static void OnFavoriteChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((FavoriteStatusPresenter)sender).UpdatePresentation();

    private void UpdatePresentation()
    {
        if (StarIcon is null) return;
        VisualStateManager.GoToState(this, IsFavorite ? "Selected" : "Unselected", useTransitions: false);
    }
}

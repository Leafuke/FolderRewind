using FolderRewind.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FolderRewind.Controls;

public sealed partial class SemanticStatusPresenter : UserControl
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status),
        typeof(SemanticStatus),
        typeof(SemanticStatusPresenter),
        new PropertyMetadata(SemanticStatus.Neutral, OnPresentationChanged));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(SemanticStatusPresenter),
        new PropertyMetadata(string.Empty, OnPresentationChanged));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(SemanticStatusPresenter),
        new PropertyMetadata(string.Empty, OnPresentationChanged));

    public SemanticStatusPresenter()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public SemanticStatus Status
    {
        get => (SemanticStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    private static void OnPresentationChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is SemanticStatusPresenter presenter)
        {
            presenter.UpdatePresentation();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdatePresentation();

    private void UpdatePresentation()
    {
        if (StatusIcon is null || StatusTextBlock is null)
        {
            return;
        }

        StatusIcon.Glyph = string.IsNullOrWhiteSpace(Glyph)
            ? SemanticStatusGlyphs.GetGlyph(Status)
            : Glyph;
        StatusTextBlock.Text = Text ?? string.Empty;
        AutomationProperties.SetName(this, Text ?? string.Empty);
        VisualStateManager.GoToState(this, Status.ToString(), useTransitions: false);
    }
}

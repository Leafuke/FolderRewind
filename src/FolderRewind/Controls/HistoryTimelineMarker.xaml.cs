using FolderRewind.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderRewind.Controls;

public sealed partial class HistoryTimelineMarker : UserControl
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(nameof(Status),
        typeof(SemanticStatus), typeof(HistoryTimelineMarker), new PropertyMetadata(SemanticStatus.Neutral, OnStatusChanged));

    public HistoryTimelineMarker()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateStatus();
    }

    public SemanticStatus Status
    {
        get => (SemanticStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    private static void OnStatusChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((HistoryTimelineMarker)sender).UpdateStatus();

    private void UpdateStatus()
    {
        if (Node is not null) VisualStateManager.GoToState(this, Status.ToString(), false);
    }
}

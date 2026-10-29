using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderRewind.Controls;

public sealed partial class NotificationStatusIcon : UserControl
{
    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(nameof(Severity), typeof(NotificationSeverity),
        typeof(NotificationStatusIcon), new PropertyMetadata(NotificationSeverity.Informational, (d, _) => ((NotificationStatusIcon)d).Update()));
    public NotificationSeverity Severity { get => (NotificationSeverity)GetValue(SeverityProperty); set => SetValue(SeverityProperty, value); }
    public NotificationStatusIcon() { InitializeComponent(); Update(); }
    private void Update()
    {
        if (InfoIcon is null) return;
        SuccessIcon.Visibility = Severity == NotificationSeverity.Success ? Visibility.Visible : Visibility.Collapsed;
        WarningIcon.Visibility = Severity == NotificationSeverity.Warning ? Visibility.Visible : Visibility.Collapsed;
        ErrorIcon.Visibility = Severity == NotificationSeverity.Error ? Visibility.Visible : Visibility.Collapsed;
        InfoIcon.Visibility = Severity == NotificationSeverity.Informational ? Visibility.Visible : Visibility.Collapsed;
    }
}

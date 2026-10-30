using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using System;

namespace FolderRewind.Controls;

public sealed partial class NotificationToastControl : UserControl
{
    public event Action? CloseRequested;
    public event Action? ActionRequested;
    public NotificationToastControl() => InitializeComponent();
    internal void SetShadowReceiver(UIElement receiver)
    {
        ToastShadow.Receivers.Clear();
        ToastShadow.Receivers.Add(receiver);
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
    internal void Render(NotificationEvent item, string actionText, bool hasAction, bool announce)
    {
        StatusIcon.Severity = item.Severity;
        TitleText.Text = NotificationContentFormatter.Title(item.Content, item.Title, I18n.GetString);
        ProjectText.Text = item.Content?.ProjectName ?? "";
        ProjectText.Visibility = ProjectText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(ProjectText, ProjectText.Text);
        SummaryText.Text = NotificationContentFormatter.Summary(item.Content, item.Message, I18n.GetString);
        ActionButton.Content = actionText; ActionButton.Visibility = hasAction ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(this, TitleText.Text + ". " + SummaryText.Text);
        AutomationProperties.SetLiveSetting(this, item.Severity == NotificationSeverity.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        Visibility = Visibility.Visible;
        if (announce) FrameworkElementAutomationPeer.FromElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    private void OnAction(object sender, RoutedEventArgs e) => ActionRequested?.Invoke();
}

using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace FolderRewind.Controls;

public sealed partial class NotificationCenterControl : UserControl
{
    public NotificationCenterViewModel ViewModel { get; } = new();
    public event EventHandler? CloseRequested;
    public NotificationCenterControl() => InitializeComponent();
    public void FocusHeader() => CloseButton.Focus(FocusState.Programmatic);
    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is NotificationItemViewModel item)
        {
            ViewModel.ShowDetails(item.Id);
            DispatcherQueue.TryEnqueue(() => CloseDetailsButton.Focus(FocusState.Programmatic));
        }
    }
    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo) ViewModel.UnreadOnly = combo.SelectedIndex == 1;
    }
}

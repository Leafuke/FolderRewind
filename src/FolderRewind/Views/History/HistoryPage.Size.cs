using FolderRewind.History.Domain;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;

namespace FolderRewind.Views;

public sealed partial class HistoryPage
{
    private Flyout? _sizeFlyout;

    private async void OnShowSizeDetailsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: { } item } element) return;
        VersionId versionId;
        RepresentationId? representationId;
        BackupSizeViewModel details;
        FrameworkElement anchor = element;
        switch (item)
        {
            case NativeHistoryVersionViewItem version:
                versionId = version.VersionId; representationId = version.RepresentationId; details = version.Sizes;
                break;
            case BackupRunSourceViewItem source when source.VersionId is { } id:
                versionId = id; representationId = source.RepresentationId; details = source.Sizes;
                break;
            default: return;
        }
        _sizeFlyout?.Hide();
        using var cancellation = new CancellationTokenSource();
        var flyout = new Flyout
        {
            Content = new ContentControl { Content = details, ContentTemplate = (DataTemplate)Resources["HistorySizeDetailsTemplate"] }
        };
        void OnClosed(object? closedSender, object args) => cancellation.Cancel();
        flyout.Closed += OnClosed;
        _sizeFlyout = flyout;
        try
        {
            flyout.ShowAt(anchor);
            await ViewModel.LoadSizeDetailsAsync(versionId, representationId, details, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { LogService.LogWarning($"Could not open backup size details: {error.Message}", "History"); }
        finally
        {
            flyout.Closed -= OnClosed;
        }
    }
}

#if SPATIAL_WORKSPACE_UI_ACCEPTANCE
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using FolderRewind.ViewModels;
using FolderRewind.Services.Development;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.Views;

// Uses the actual bound controls so production TextChanged/SelectionChanged handlers run.
public sealed partial class SpatialWorkspacePage
{
    internal bool UiAcceptanceOverlayDrained => !_overlayLoading && !_overlayPending && !_overlayTimer.IsEnabled;
    internal UiOverlayObservation CaptureUiAcceptanceOverlays()
        => new(_active, UiAcceptanceOverlayDrained, ViewModel.World?.Id, ViewModel.Layer?.Id,
            ViewModel.CaptureUiAcceptanceOverlayRows(), LeftMap.CaptureUiAcceptanceOverlays(), RightMap.CaptureUiAcceptanceOverlays());

    internal async Task RunRapidUiAcceptanceFiltersAsync()
    {
        var search = UiAcceptanceFind<TextBox>(this).Single(control =>
            AutomationProperties.GetAutomationId(control) == "SpatialWorkspace_Search");
        for (int i = 0; i < 24; i++)
        {
            search.Text = i % 2 == 0 ? "Cell 009" : "Cell 076";
            Filter.SelectedItem = ((IEnumerable<SpatialWorkspaceChoice>)Filter.ItemsSource)
                .Single(choice => choice.Id == (i % 2 == 0 ? "Added" : "Deleted"));
            CategoryFilter.SelectedItem = ViewModel.Categories.Single(choice => choice.Id == (i % 2 == 0 ? "blocks" : "entities"));
            await Task.Delay(20);
        }
        search.Text = "Cell 00234";
        Filter.SelectedItem = ((IEnumerable<SpatialWorkspaceChoice>)Filter.ItemsSource).Single(choice => choice.Id == "Modified");
        CategoryFilter.SelectedItem = ViewModel.Categories.Single(choice => choice.Id == "blocks");
    }

    internal object[] UiAcceptanceWeakTargets() => [this, LeftMap, RightMap];
    internal void ResetUiAcceptanceFilters()
    {
        UiAcceptanceFind<TextBox>(this).Single(control => AutomationProperties.GetAutomationId(control) == "SpatialWorkspace_Search").Text = "";
        Filter.SelectedItem = ((IEnumerable<SpatialWorkspaceChoice>)Filter.ItemsSource).Single(choice => choice.Id == "");
        CategoryFilter.SelectedItem = ViewModel.Categories.Single(choice => choice.Id == "$all");
    }

    private static IEnumerable<T> UiAcceptanceFind<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in UiAcceptanceFind<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
#endif

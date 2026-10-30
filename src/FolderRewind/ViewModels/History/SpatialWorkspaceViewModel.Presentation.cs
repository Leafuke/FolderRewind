using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed partial class SpatialWorkspaceViewModel
{
    private long _rowsRevision;
    private IReadOnlyList<SpatialComparisonCategory> _categories = [];
    public ObservableCollection<SpatialWorkspaceChoice> Categories { get; } = [];
    public string Category { get; set; } = "$default";
    public string CategorySummary { get; private set; } = "";
    public string CategoryBreakdown { get; private set; } = "";
    public bool HasCategorySummary => !string.IsNullOrEmpty(CategorySummary);
    public bool BatchOnlyUnresolved { get; private set; } = true;
    public string SelectionFeedback { get; private set; } = "";
    public bool HasSelectionFeedback => !string.IsNullOrEmpty(SelectionFeedback);
    public bool IsCompare => !IsScope && !IsMerge;
    public bool HasSpatialIndex => !IsScope;
    public bool HasDetails => !string.IsNullOrEmpty(Details);
    public bool CanLocate => SelectedItem?.Row.Locations.Count > 0;
    public string FocusLabel => SelectedItem?.Title ?? "";
    public string LeftLabel => I18n.GetString(IsScope ? "SpatialWorkspace_CurrentSource" : IsMerge ? "SpatialWorkspace_LeftInput" : "SpatialWorkspace_From");
    public string RightLabel => I18n.GetString(IsMerge ? "SpatialWorkspace_RightInput" : "SpatialWorkspace_To");
    public string ModeLegend => I18n.GetString(IsScope ? "SpatialWorkspace_ScopeLegend" : IsMerge ? "SpatialWorkspace_MergeLegend" : "SpatialWorkspace_CompareLegend");
    private string CategoryName(string id) => _categories.FirstOrDefault(c => c.Id == id) is { } c ? PluginV3SpatialPreview.Localize(c.Name) : id;
    private SpatialWorkspaceItem Item(SpatialWorkspaceRow row) => new(row) { Categories = string.Join(" · ", row.Components.Select(CategoryName)) };
    private void SetCategories(SpatialComparisonDescription description)
    {
        _categories = description.Categories;
        Categories.Clear();
        if (_categories.Count > 0) Categories.Add(new("$default", I18n.GetString("SpatialWorkspace_DefaultCategories")));
        Categories.Add(new("$all", I18n.GetString("SpatialWorkspace_AllCategories")));
        foreach (var c in _categories) Categories.Add(new(c.Id, PluginV3SpatialPreview.Localize(c.Name)));
        Categories.Add(new("$files", I18n.GetString("SpatialWorkspace_Files")));
        if (!Categories.Any(c => c.Id == Category)) Category = Categories[0].Id;
        ChoicesChanged?.Invoke();
    }
    private SpatialWorkspaceQuery CurrentQuery() => new(World?.Id ?? "", Layer?.Id ?? "", Search, Filter,
        !IsCompare || Category is "$all" or "$files" || _categories.Count == 0 ? null
            : Category == "$default" ? _categories.Where(c => c.DefaultVisible).Select(c => c.Id).ToArray() : [Category],
        Category == "$files", IsCompare, Category == "$default" ? _categories.Select(c => c.Id).ToArray() : null);
    public async Task SelectOverlayAsync(string[] ids)
    {
        if (IsScope)
        {
            if (Items.FirstOrDefault(i => ids.Contains(i.Id)) is { } item) await SelectItemAsync(item, false);
            return;
        }
        if (_index is null || ids.Length == 0) return;
        var rows = await Task.Run(() => _index.ByIds(ids));
        if (rows is not null) await SelectItemAsync(Item(rows), false);
    }
    public void ShowDetails(string text) { Details = text; Notify(); }
    public void ClearFocus() { SelectedItem = null; Details = ""; Notify(); OverlayChanged?.Invoke(); }
    public async Task ChangeBatchModeAsync(bool onlyUnresolved)
    {
        BatchOnlyUnresolved = onlyUnresolved;
        if (_index is not null) await Task.Run(() => _index.ClearSelection());
        SelectionFeedback = I18n.GetString("SpatialWorkspace_BatchModeChanged");
        Notify(); await ReloadRowsAsync();
    }
}

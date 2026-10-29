using FolderRewind.Models;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderRewind.Views;

public sealed class GameDiscoveryRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate SetTemplate { get; set; } = null!;
    public DataTemplate ResourceTemplate { get; set; } = null!;
    public DataTemplate DraftTemplate { get; set; } = null!;
    public DataTemplate ChangeTemplate { get; set; } = null!;
    protected override DataTemplate SelectTemplateCore(object item) => item switch
    {
        GameDiscoveryBackupSetItem => SetTemplate,
        GameDiscoveryResourceItem => ResourceTemplate,
        GameDiscoveryDraftItem => DraftTemplate,
        DiscoverySourceChange => ChangeTemplate,
        _ => base.SelectTemplateCore(item)
    };
    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

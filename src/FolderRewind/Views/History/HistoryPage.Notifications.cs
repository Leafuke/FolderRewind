using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.Views;

public sealed partial class HistoryPage
{
    private async Task OpenNotificationTargetAsync(NotificationTarget target)
    {
        var config = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(c => c.Id == target.ConfigId);
        var folder = target.FolderId is null ? null : config?.SourceFolders.FirstOrDefault(f => f.Id == target.FolderId);
        if (config is null || target.FolderId is not null && folder is null) { NotificationNavigationService.Unavailable(target); return; }
        ViewModel.CommentFilterText = string.Empty;
        ViewModel.SelectedBranch = null;
        await ApplySelectionFromNavigationAsync(config.Id, target.RunId is null ? folder?.Path : null);
        await ViewModel.SetHistoryViewModeAsync(target.RunId is null ? HistoryViewMode.PerSource : HistoryViewMode.ByRun);
        object? Find() => target.RunId is { } runId ? ViewModel.FilteredRuns.FirstOrDefault(r => r.RunId.ToString() == runId)
            : ViewModel.FilteredHistory.FirstOrDefault(v => v.VersionId.ToString() == target.VersionId);
        if (target.RunId is null && target.VersionId is null) return;
        var item = Find();
        while (item is null && ViewModel.CanLoadMore && _legacyNoticeActive)
        {
            var count = ViewModel.FilteredRuns.Count + ViewModel.FilteredHistory.Count;
            await ViewModel.LoadMoreAsync();
            item = Find();
            if (count == ViewModel.FilteredRuns.Count + ViewModel.FilteredHistory.Count) break;
        }
        if (!_legacyNoticeActive) return;
        if (item is null) { NotificationNavigationService.Unavailable(target); return; }
        var list = target.RunId is null ? HistoryList : RunHistoryList;
        list.ScrollIntoView(item);
        list.UpdateLayout();
        if (list.ContainerFromItem(item) is Control container) container.Focus(FocusState.Programmatic);
        else list.Focus(FocusState.Programmatic);
    }
}

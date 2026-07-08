using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed class FolderDetailsDialogViewModel : ViewModelBase
{
    public ObservableCollection<FolderDetailsSection> Sections { get; } = new();

    public async Task LoadAsync(BackupConfig config, ManagedFolder folder, CancellationToken cancellationToken)
    {
        Sections.Clear();
        foreach (FolderDetailsSection section in FolderDetailsService.BuildBaseSections(config, folder))
        {
            Sections.Add(section);
        }

        var pluginSections = await PluginService.GetFolderDetailsSectionsAsync(config, folder, cancellationToken);
        foreach (FolderDetailsSection section in pluginSections)
        {
            Sections.Add(section);
        }

        string sizeLabel = I18n.GetString("FolderDetailsDialog_Size");
        string fileCountLabel = I18n.GetString("FolderDetailsDialog_FileCount");
        string directoryCountLabel = I18n.GetString("FolderDetailsDialog_DirectoryCount");
        var basicItems = Sections[0].Items;

        try
        {
            var stats = await FolderDetailsService.ComputeStatisticsAsync(folder.Path, cancellationToken);
            SetItemValue(basicItems, sizeLabel, $"{stats.TotalBytes / 1024.0:F2} KB");
            SetItemValue(basicItems, fileCountLabel, stats.FileCount.ToString());
            SetItemValue(basicItems, directoryCountLabel, stats.DirectoryCount.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.LogWarning(
                $"Failed to compute folder details statistics for '{folder.Path}': {ex.Message}",
                nameof(FolderDetailsDialogViewModel));

            SetItemError(basicItems, sizeLabel, ex.Message);
            SetItemError(basicItems, fileCountLabel, ex.Message);
            SetItemError(basicItems, directoryCountLabel, ex.Message);
        }
    }

    private static void SetItemValue(IEnumerable<FolderDetailsItem> items, string label, string value)
    {
        FolderDetailsItem? item = items.FirstOrDefault(candidate => candidate.Label == label);
        if (item == null)
        {
            return;
        }

        item.Value = value;
        item.Description = string.Empty;
        item.IsLoading = false;
        item.IsError = false;
    }

    private static void SetItemError(IEnumerable<FolderDetailsItem> items, string label, string description)
    {
        FolderDetailsItem? item = items.FirstOrDefault(candidate => candidate.Label == label);
        if (item == null)
        {
            return;
        }

        item.Value = I18n.GetString("Common_Failed");
        item.Description = description ?? string.Empty;
        item.IsLoading = false;
        item.IsError = true;
    }
}

using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Globalization;

namespace FolderRewind.ViewModels;

public sealed class FolderRenameDialogViewModel : ViewModelBase
{
    private ManagedFolder? _folder;
    private string _newLeafName = string.Empty;
    private string _impactSummary = string.Empty;
    private string _currentPath = string.Empty;
    private bool _suppressPreviewRefresh;

    public string NewLeafName
    {
        get => _newLeafName;
        set
        {
            if (!SetProperty(ref _newLeafName, value ?? string.Empty))
            {
                return;
            }

            if (!_suppressPreviewRefresh)
            {
                RefreshPreview();
            }
        }
    }

    public string ImpactSummary
    {
        get => _impactSummary;
        private set
        {
            if (SetProperty(ref _impactSummary, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasImpactSummary));
            }
        }
    }

    public string CurrentPath
    {
        get => _currentPath;
        private set => SetProperty(ref _currentPath, value ?? string.Empty);
    }

    public bool HasImpactSummary => !string.IsNullOrWhiteSpace(ImpactSummary);

    public void Load(ManagedFolder folder, FolderRenamePreview preview)
    {
        _folder = folder;
        CurrentPath = string.IsNullOrWhiteSpace(preview?.OldPath)
            ? folder?.Path ?? string.Empty
            : preview.OldPath;

        ApplyPreview(preview ?? new FolderRenamePreview(), usePreviewLeafName: true);
    }

    private void RefreshPreview()
    {
        if (_folder == null)
        {
            return;
        }

        ApplyPreview(FolderRenameService.PreviewRename(_folder, NewLeafName), usePreviewLeafName: false);
    }

    private void ApplyPreview(FolderRenamePreview preview, bool usePreviewLeafName)
    {
        _suppressPreviewRefresh = true;

        try
        {
            if (usePreviewLeafName)
            {
                NewLeafName = preview.NewLeafName ?? string.Empty;
            }

            ImpactSummary = BuildImpactSummary(preview);
        }
        finally
        {
            _suppressPreviewRefresh = false;
        }
    }

    private static string BuildImpactSummary(FolderRenamePreview preview)
    {
        if (UsesChineseUiCulture())
        {
            return preview.IsValid
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    "将更新 {0} 个配置引用和 {1} 条历史记录。",
                    preview.AffectedConfigCount,
                    preview.AffectedHistoryCount)
                : "输入新的文件夹名称后，将在这里预览会被更新的引用。";
        }

        return preview.IsValid
            ? string.Format(
                CultureInfo.CurrentCulture,
                "Updates {0} config reference(s) and {1} history item(s).",
                preview.AffectedConfigCount,
                preview.AffectedHistoryCount)
            : "Enter a new folder name to preview affected references.";
    }

    private static bool UsesChineseUiCulture()
    {
        string cultureName = CultureInfo.CurrentUICulture.Name;
        return cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }
}

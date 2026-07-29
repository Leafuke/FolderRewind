using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Globalization;

namespace FolderRewind.ViewModels;

public sealed class FolderRenameDialogViewModel : ViewModelBase
{
    private ManagedFolder? _folder;
    private FolderRenamePreview _cachedImpact = new();
    private string _newLeafName = string.Empty;
    private string _impactSummary = string.Empty;
    private string _currentPath = string.Empty;
    private string _newPath = string.Empty;
    private string _validationMessage = string.Empty;
    private bool _isValid;
    private bool _suppressValidation;

    public string NewLeafName
    {
        get => _newLeafName;
        set
        {
            if (!SetProperty(ref _newLeafName, value ?? string.Empty))
            {
                return;
            }

            if (!_suppressValidation)
            {
                RefreshValidation();
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

    public string NewPath
    {
        get => _newPath;
        private set => SetProperty(ref _newPath, value ?? string.Empty);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasValidationMessage));
            }
        }
    }

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public bool IsValid
    {
        get => _isValid;
        private set => SetProperty(ref _isValid, value);
    }

    public void Load(ManagedFolder folder, FolderRenamePreview preview)
    {
        _folder = folder;
        _cachedImpact = preview ?? new FolderRenamePreview();
        CurrentPath = string.IsNullOrWhiteSpace(preview?.OldPath)
            ? folder?.Path ?? string.Empty
            : preview.OldPath;
        ImpactSummary = BuildImpactSummary(_cachedImpact);

        _suppressValidation = true;
        NewLeafName = _cachedImpact.NewLeafName ?? string.Empty;
        _suppressValidation = false;
        RefreshValidation();
    }

    private void RefreshValidation()
    {
        if (_folder == null)
        {
            return;
        }

        var validation = FolderRenameService.PreviewRenameWithCachedImpact(
            _folder,
            NewLeafName,
            _cachedImpact);
        IsValid = validation.IsValid;
        ValidationMessage = validation.IsValid ? string.Empty : validation.Message;
        NewPath = validation.NewPath;
    }

    private static string BuildImpactSummary(FolderRenamePreview preview)
    {
        if (UsesChineseUiCulture())
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                "将更新 {0} 个配置引用和 {1} 条历史记录。",
                preview.AffectedConfigCount,
                preview.AffectedHistoryCount);
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            "Updates {0} config reference(s) and {1} history item(s).",
            preview.AffectedConfigCount,
            preview.AffectedHistoryCount);
    }

    private static bool UsesChineseUiCulture()
    {
        string cultureName = CultureInfo.CurrentUICulture.Name;
        return cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }
}

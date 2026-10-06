using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed class SourceScopeRuleItem : FolderRewind.Models.ObservableObject
{
    private string _pattern = string.Empty;
    private string _error = string.Empty;

    public string Pattern
    {
        get => _pattern;
        set
        {
            if (SetProperty(ref _pattern, value ?? string.Empty))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string Error { get => _error; set => SetProperty(ref _error, value ?? string.Empty); }
    public event EventHandler? Changed;
}

public sealed class SourceScopeEditorDialogViewModel : ViewModelBase, IDisposable
{
    private readonly ManagedFolder _folder;
    private readonly SourceScopePreviewController _preview = new();
    private bool _disposed;
    private int _modeSelectedIndex;
    private string _previewText = string.Empty;
    private bool _isPreviewing;

    public SourceScopeEditorDialogViewModel(BackupConfig config, ManagedFolder folder)
    {
        ArgumentNullException.ThrowIfNull(config);
        _folder = folder ?? throw new ArgumentNullException(nameof(folder));
        _preview.StateChanged += OnPreviewStateChanged;
        _modeSelectedIndex = folder.SourceScope?.Mode == BackupSourceScopeMode.Include ? 1 : 0;
        foreach (var pattern in folder.SourceScope?.IncludePatterns ?? new ObservableCollection<string>())
        {
            AddRuleCore(pattern);
        }
        ValidateRules();
    }

    public ObservableCollection<SourceScopeRuleItem> Rules { get; } = new();

    public int ModeSelectedIndex
    {
        get => _modeSelectedIndex;
        set
        {
            var normalized = value == 1 ? 1 : 0;
            if (!SetProperty(ref _modeSelectedIndex, normalized)) return;
            OnPropertyChanged(nameof(IsIncludeMode));
            ValidateRules();
            _ = RefreshPreviewAsync();
        }
    }

    public bool IsIncludeMode => ModeSelectedIndex == 1;
    public string PreviewText { get => _previewText; private set => SetProperty(ref _previewText, value ?? string.Empty); }
    public bool IsPreviewing { get => _isPreviewing; private set => SetProperty(ref _isPreviewing, value); }
    public bool CanSave { get; private set; }

    public void AddRule(string pattern = "**/*.sav")
    {
        if (_disposed) return;
        AddRuleCore(pattern);
        ValidateRules();
        _ = RefreshPreviewAsync();
    }

    private void AddRuleCore(string pattern)
    {
        var item = new SourceScopeRuleItem { Pattern = pattern };
        item.Changed += OnRuleChanged;
        Rules.Add(item);
    }

    public void RemoveRule(SourceScopeRuleItem item)
    {
        if (item == null) return;
        item.Changed -= OnRuleChanged;
        Rules.Remove(item);
        ValidateRules();
        _ = RefreshPreviewAsync();
    }

    public Task RefreshPreviewAsync()
    {
        if (_disposed) return Task.CompletedTask;
        var valid = TryCreateScope(out var scope, out var error);
        return _preview.RefreshAsync(_folder.Path, valid ? scope : null, error);
    }

    public void CancelPreview() => _preview.Cancel();

    private void OnPreviewStateChanged()
    {
        IsPreviewing = _preview.IsPreviewing;
        PreviewText = _preview.State switch
        {
            SourceScopePreviewState.Running => I18n.GetString("SourceScopeEditor_PreviewRunning"),
            SourceScopePreviewState.Invalid => _preview.Error,
            SourceScopePreviewState.Skipped => I18n.GetString("SourceScopeEditor_PreviewBroadRootSkipped"),
            SourceScopePreviewState.Completed => I18n.Format("SourceScopeEditor_PreviewResult", _preview.Result!.FileCount, FormatBytes(_preview.Result.Bytes)),
            SourceScopePreviewState.Failed => I18n.Format("SourceScopeEditor_PreviewFailed", _preview.Error),
            SourceScopePreviewState.Canceled => I18n.GetString("Common_Canceled"),
            _ => string.Empty
        };
    }

    public bool TryCreateScope(out BackupSourceScope scope, out string errorMessage)
    {
        scope = new BackupSourceScope { Mode = IsIncludeMode ? BackupSourceScopeMode.Include : BackupSourceScopeMode.All };
        errorMessage = string.Empty;
        if (!IsIncludeMode)
        {
            return true;
        }
        try
        {
            scope.IncludePatterns = new ObservableCollection<string>(
                BackupSourceScopePatternSet.NormalizeAndValidate(Rules.Select(item => item.Pattern)));
            return true;
        }
        catch (InvalidDataException ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preview.StateChanged -= OnPreviewStateChanged;
        _preview.Dispose();
        foreach (var item in Rules)
        {
            item.Changed -= OnRuleChanged;
        }
    }

    private void OnRuleChanged(object? sender, EventArgs e)
    {
        ValidateRules();
        _ = RefreshPreviewAsync();
    }

    private void ValidateRules()
    {
        foreach (var item in Rules)
        {
            item.Error = ValidatePattern(item.Pattern);
        }
        var canSave = !IsIncludeMode
                      || (Rules.Count is > 0 and <= BackupSourceScopePatternSet.MaximumPatternCount
                          && Rules.All(item => string.IsNullOrWhiteSpace(item.Error)));
        if (CanSave != canSave)
        {
            CanSave = canSave;
            OnPropertyChanged(nameof(CanSave));
        }
    }

    private static string ValidatePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return I18n.GetString("SourceScopeEditor_ErrorEmpty");
        }
        if (pattern.Length > BackupSourceScopePatternSet.MaximumPatternLength)
        {
            return I18n.Format("SourceScopeEditor_ErrorLength", BackupSourceScopePatternSet.MaximumPatternLength);
        }
        if (!BackupSourceScopePatternSet.IsSafeRelativePattern(pattern))
        {
            return I18n.GetString("SourceScopeEditor_ErrorUnsafe");
        }
        try
        {
            BackupSourceScopePatternSet.Compile(new[] { pattern });
            return string.Empty;
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return $"{value:F2} {units[index]}";
    }
}

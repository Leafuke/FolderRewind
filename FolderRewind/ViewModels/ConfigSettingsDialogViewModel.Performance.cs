using FolderRewind.Services;

namespace FolderRewind.ViewModels;

public sealed partial class ConfigSettingsDialogViewModel
{
    private int _performanceChoice = -1;
    private BackupPerformanceValues? _undoPerformance;
    private BackupPerformanceValues? _appliedPerformance;
    private BackupPerformanceValues CurrentPerformance => new(_archive.CpuThreads, _archive.RunCompressionAtLowPriority);
    public int PerformanceChoice
    {
        get => _performanceChoice < 0 ? DerivePerformancePresetIndex() : _performanceChoice;
        set { _performanceChoice = value; OnPropertyChanged(); OnPropertyChanged(nameof(PerformanceChangePreview)); }
    }
    public string PerformanceChangePreview
    {
        get
        {
            var target = BackupPerformancePolicy.Get(PerformanceChoice, _cpuThreadMax, CurrentPerformance);
            return I18n.Format("Performance_Preview", _archive.CpuThreads, target.CpuThreads,
                _archive.RunCompressionAtLowPriority, target.LowPriority);
        }
    }
    public void ApplyPendingPerformance()
    {
        _undoPerformance = CurrentPerformance;
        ApplyPerformancePreset(PerformanceChoice);
        _appliedPerformance = CurrentPerformance;
        OnPropertyChanged(nameof(PerformanceChangePreview));
    }
    public void UndoPerformance()
    {
        if (_undoPerformance is not { } previous || _appliedPerformance != CurrentPerformance) return;
        _archive.CpuThreads = previous.CpuThreads;
        _archive.RunCompressionAtLowPriority = previous.LowPriority;
        _undoPerformance = null;
        _performanceChoice = -1;
        RaisePerformancePresetProperties();
    }
}

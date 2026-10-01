using FolderRewind.Services;
using System.Globalization;

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
            return I18n.Format("Performance_Preview", DisplayThreads(_archive.CpuThreads), DisplayThreads(target.CpuThreads),
                I18n.GetString(_archive.RunCompressionAtLowPriority ? "Performance_Enabled" : "Performance_Disabled"),
                I18n.GetString(target.LowPriority ? "Performance_Enabled" : "Performance_Disabled"));
        }
    }
    private static string DisplayThreads(int threads) => threads == 0 ? I18n.GetString("Performance_DefaultThreads") : threads.ToString(CultureInfo.CurrentCulture);
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

using FolderRewind.Services;
using System;
using System.Globalization;

namespace FolderRewind.ViewModels;

public sealed partial class ConfigSettingsDialogViewModel
{
    private int _performanceChoice = -1;
    private BackupPerformanceSnapshot? _undoPerformance;
    private BackupPerformanceSnapshot? _appliedPerformance;
    private BackupPerformanceFields _changedPerformanceFields;
    private BackupPerformancePreset _undoPerformancePreset;
    private bool _performanceEditInProgress;
    private BackupPerformanceSnapshot CurrentPerformance => new(_archive.CpuThreads, _archive.RunCompressionAtLowPriority, _archive.Method, _archive.CompressionLevel);
    private BackupPerformancePresetDefinition SelectedPerformancePreset => BackupPerformancePolicy.Presets[PerformanceChoice];
    public bool CanApplyPerformancePreset => SelectedPerformancePreset.Id != BackupPerformancePreset.Custom;
    public bool CanUndoPerformance => _undoPerformance is not null && _appliedPerformance is { } applied
        && _changedPerformanceFields != BackupPerformanceFields.None
        && (BackupPerformancePolicy.ChangedFields(applied, CurrentPerformance) & _changedPerformanceFields) == BackupPerformanceFields.None;
    public int PerformanceChoice
    {
        get => _performanceChoice < 0 ? DerivePerformancePresetIndex() : _performanceChoice;
        set
        {
            if (value < 0 || value >= BackupPerformancePolicy.Presets.Count || value == _performanceChoice) return;
            _performanceChoice = value;
            RaisePerformancePresetProperties();
        }
    }
    public string PerformanceChangePreview
    {
        get
        {
            var target = BackupPerformancePolicy.Get(SelectedPerformancePreset.Id, _cpuThreadMax, CurrentPerformance);
            return I18n.Format("Performance_Preview", DisplayThreads(_archive.CpuThreads), DisplayThreads(target.CpuThreads),
                I18n.GetString(_archive.RunCompressionAtLowPriority ? "Performance_Enabled" : "Performance_Disabled"),
                I18n.GetString(target.LowPriority ? "Performance_Enabled" : "Performance_Disabled"),
                _archive.Method, target.Method, _archive.CompressionLevel, target.CompressionLevel);
        }
    }
    private static string DisplayThreads(int threads) => threads == 0 ? I18n.GetString("Performance_DefaultThreads") : threads.ToString(CultureInfo.CurrentCulture);
    public void ApplyPendingPerformance()
    {
        if (!CanApplyPerformancePreset) return;
        var preset = SelectedPerformancePreset.Id;
        var target = BackupPerformancePolicy.Get(preset, _cpuThreadMax, CurrentPerformance);
        var fields = BackupPerformancePolicy.ChangedFields(CurrentPerformance, target);
        if (fields == BackupPerformanceFields.None)
        {
            _lastAppliedPerformancePreset = preset;
            _performanceChoice = -1;
            RaisePerformancePresetProperties();
            return;
        }
        _undoPerformance = CurrentPerformance;
        _undoPerformancePreset = BackupPerformancePolicy.Derive(CurrentPerformance, _cpuThreadMax, _lastAppliedPerformancePreset);
        _lastAppliedPerformancePreset = preset;
        ApplyPerformanceValues(target, fields);
        _appliedPerformance = CurrentPerformance;
        _changedPerformanceFields = BackupPerformancePolicy.ChangedFields(_undoPerformance.Value, CurrentPerformance);
        _performanceChoice = -1;
        RaisePerformancePresetProperties();
    }
    public void UndoPerformance()
    {
        if (!CanUndoPerformance || _undoPerformance is not { } previous) return;
        ApplyPerformanceValues(previous, _changedPerformanceFields);
        _lastAppliedPerformancePreset = _undoPerformancePreset;
        _undoPerformance = null;
        _appliedPerformance = null;
        _changedPerformanceFields = BackupPerformanceFields.None;
        _performanceChoice = -1;
        RaisePerformancePresetProperties();
    }

    private void ApplyPerformanceValues(BackupPerformanceSnapshot target, BackupPerformanceFields fields)
    {
        _performanceEditInProgress = true;
        try
        {
            if (fields.HasFlag(BackupPerformanceFields.Method)) _archive.Method = target.Method;
            // Changing the method can normalize the old level even when both
            // methods happen to use the same intended numeric level.
            if (fields.HasFlag(BackupPerformanceFields.Level) || fields.HasFlag(BackupPerformanceFields.Method))
                _archive.CompressionLevel = target.CompressionLevel;
            if (fields.HasFlag(BackupPerformanceFields.Threads)) _archive.CpuThreads = target.CpuThreads;
            if (fields.HasFlag(BackupPerformanceFields.Priority)) _archive.RunCompressionAtLowPriority = target.LowPriority;
        }
        finally { _performanceEditInProgress = false; }
    }

    private void OnPerformanceFieldEdited(BackupPerformanceFields field)
    {
        if (_performanceEditInProgress) return;
        _performanceChoice = -1;
        if ((_changedPerformanceFields & field) != BackupPerformanceFields.None)
        {
            _undoPerformance = null;
            _appliedPerformance = null;
            _changedPerformanceFields = BackupPerformanceFields.None;
        }
        RaisePerformancePresetProperties();
    }

    private void ResetPerformanceDraftState()
    {
        _performanceChoice = -1;
        _undoPerformance = null;
        _appliedPerformance = null;
        _changedPerformanceFields = BackupPerformanceFields.None;
        _lastAppliedPerformancePreset = BackupPerformancePreset.Custom;
    }
}

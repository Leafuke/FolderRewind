using System;
using System.ComponentModel;

namespace FolderRewind.Services;

// UI-owner-context state: cancellation callbacks can run synchronously during deactivation.
internal sealed class SettingsPresentationLifetime
{
    private int _bindingUpdates;
    public bool IsActive { get; private set; }
    public int Generation { get; private set; }
    public bool CanEdit => IsActive && _bindingUpdates == 0;

    public void Activate()
    {
        if (IsActive) return;
        IsActive = true;
        Generation++;
    }

    public void Deactivate()
    {
        if (!IsActive) return;
        IsActive = false;
        Generation++;
    }

    public bool IsCurrent(int generation) => IsActive && Generation == generation;

    public void UpdateBindings(Action update)
    {
        if (!IsActive || _bindingUpdates != 0) return;
        _bindingUpdates++;
        try { update(); }
        finally { _bindingUpdates--; }
    }
}

// Creation lasts for the cached control's lifetime; collapsing never discards edits.
internal sealed class DeferredSettingsSection : INotifyPropertyChanged
{
    public bool IsCreated { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Create()
    {
        if (IsCreated) return;
        IsCreated = true;
        try { PropertyChanged?.Invoke(this, new(nameof(IsCreated))); }
        catch
        {
            IsCreated = false;
            throw;
        }
    }
}

internal enum SettingsSection
{
    Presets, CoreBehavior, Notifications, Hotkeys, Appearance, Sponsor,
    Runtime, CoreValidation, Diagnostics, Templates, Migration, Plugins, KnotLink, About
}

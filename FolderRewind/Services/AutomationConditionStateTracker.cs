using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

public enum AutomationConditionFileState
{
    Missing = 0,
    Locked = 1,
    Unlocked = 2
}

public enum AutomationConditionTransition
{
    None = 0,
    BecameLocked = 1,
    BecameUnlocked = 2
}

public sealed class AutomationConditionStateTracker
{
    private readonly Dictionary<string, AutomationConditionFileState> _states =
        new(StringComparer.OrdinalIgnoreCase);

    public int Count => _states.Count;

    public AutomationConditionTransition Observe(string key, AutomationConditionFileState currentState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!_states.TryGetValue(key, out var previousState))
        {
            _states[key] = currentState;
            return AutomationConditionTransition.None;
        }

        if (previousState == currentState)
            return AutomationConditionTransition.None;

        _states[key] = currentState;
        if (previousState == AutomationConditionFileState.Locked
            && currentState == AutomationConditionFileState.Unlocked)
            return AutomationConditionTransition.BecameUnlocked;
        if (previousState == AutomationConditionFileState.Unlocked
            && currentState == AutomationConditionFileState.Locked)
            return AutomationConditionTransition.BecameLocked;
        return AutomationConditionTransition.None;
    }

    public void RetainOnly(IReadOnlySet<string> activeKeys)
    {
        ArgumentNullException.ThrowIfNull(activeKeys);
        foreach (var key in _states.Keys.Where(key => !activeKeys.Contains(key)).ToArray())
        {
            _states.Remove(key);
        }
    }

    public void Clear() => _states.Clear();
}

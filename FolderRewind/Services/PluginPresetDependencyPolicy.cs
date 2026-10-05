using System;
using System.Collections.Generic;
using System.Linq;
using FolderRewind.Services.Plugins.V3;

namespace FolderRewind.Services;

internal static class PluginPresetDependencyPolicy
{
    internal static bool CanExecute(IEnumerable<string> dependencies, IEnumerable<PluginPresetStepResult> results)
    {
        var successful = results.Where(r => r.Outcome is PluginPresetStepOutcome.Success or PluginPresetStepOutcome.SuccessWithWarnings)
            .Select(r => r.ActionId).ToHashSet(StringComparer.Ordinal);
        return dependencies.All(successful.Contains);
    }
}

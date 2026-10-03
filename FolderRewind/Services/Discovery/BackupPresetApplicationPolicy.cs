using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services.Discovery;

public enum BackupPresetApplicationMode
{
    Invalid = 0,
    Inline = 1,
    ProviderTargeted = 2
}

internal readonly record struct BackupPresetApplicationSourceFacts(
    bool HasUsableInlineRule,
    bool HasValidProviderReference);

internal static class BackupPresetApplicationPolicy
{
    public static BackupPresetApplicationMode Classify(
        bool hasDirectInlineRule,
        IEnumerable<BackupPresetApplicationSourceFacts>? sources)
    {
        var facts = (sources ?? []).ToList();
        if (hasDirectInlineRule || facts.Any(source => source.HasUsableInlineRule))
        {
            return BackupPresetApplicationMode.Inline;
        }
        return facts.Any(source => source.HasValidProviderReference)
            ? BackupPresetApplicationMode.ProviderTargeted
            : BackupPresetApplicationMode.Invalid;
    }
}

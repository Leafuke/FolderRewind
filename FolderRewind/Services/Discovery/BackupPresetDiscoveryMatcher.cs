using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services.Discovery;

public static class BackupPresetDiscoveryMatcher
{
    public static bool Matches(BackupPreset? preset, GameDefinition? definition)
    {
        if (preset == null || definition == null)
        {
            return false;
        }
        return (preset.DiscoverySources ?? []).Any(source =>
            source?.Kind == BackupPresetDiscoverySourceKind.ProviderReference
            && DiscoveryDefinitionMatchPolicy.Matches(
                source.ProviderId,
                source.DefinitionId,
                definition.ProviderId,
                definition.DefinitionId));
    }

    public static IEnumerable<BackupPreset> FindMatches(
        GameDefinition definition,
        IEnumerable<BackupPreset>? presets)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return (presets ?? Array.Empty<BackupPreset>()).Where(preset => Matches(preset, definition));
    }
}

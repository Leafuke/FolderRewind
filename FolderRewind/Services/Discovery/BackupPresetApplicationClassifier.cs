using FolderRewind.Models;
using System;
using System.Linq;

namespace FolderRewind.Services.Discovery;

public static class BackupPresetApplicationClassifier
{
    public static BackupPresetApplicationMode Classify(BackupPreset? preset)
    {
        if (preset == null)
        {
            return BackupPresetApplicationMode.Invalid;
        }
        return BackupPresetApplicationPolicy.Classify(
            preset.PathRules?.Any(IsUsableInlineRule) == true,
            (preset.DiscoverySources ?? []).Select(source => new BackupPresetApplicationSourceFacts(
                source?.Kind == BackupPresetDiscoverySourceKind.InlinePathRules
                && source.PathRules?.Any(IsUsableInlineRule) == true,
                source?.Kind == BackupPresetDiscoverySourceKind.ProviderReference
                && !string.IsNullOrWhiteSpace(source.ProviderId)
                && !string.IsNullOrWhiteSpace(source.DefinitionId))));
    }

    private static bool IsUsableInlineRule(TemplatePathRule? rule) =>
        rule?.Segments?.Any(segment => segment != null
            && (segment.Type == TemplatePathSegmentType.EnumerateDirectory
                || !string.IsNullOrWhiteSpace(segment.Value))) == true;
}

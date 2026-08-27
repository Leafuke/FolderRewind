using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Artifacts;

/// <summary>
/// Commit 6–16 的临时 Legacy bridge。Native History 只能传入显式 ArtifactId roots；
/// 旧 HistoryItemId 到 ledger.HistoryRoots 的解析集中在这里，并在 Commit 20 删除。
/// </summary>
public static class LegacyHistoryRootAdapter
{
    public static IReadOnlyList<ArtifactId> GetArtifactRoots(
        ArtifactLedgerDocument document,
        IEnumerable<string>? historyItemIds = null)
    {
        ArtifactLedgerValidator.Validate(document);
        if (historyItemIds is null)
        {
            return document.HistoryRoots.Select(root => root.RootArtifactId).ToArray();
        }

        var requested = historyItemIds.ToHashSet(StringComparer.Ordinal);
        var selected = document.HistoryRoots
            .Where(root => requested.Contains(root.HistoryItemId))
            .Select(root => root.RootArtifactId)
            .ToArray();
        if (selected.Length != requested.Count)
        {
            throw new KeyNotFoundException("One or more Legacy History roots are missing.");
        }

        return selected;
    }
}


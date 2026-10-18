using FolderRewind.History.Domain;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.History.Application;

/// <summary>Legacy names are usable only when they identify one logical Version.</summary>
public static class HistoryVersionFileSelector
{
    public static VersionId? FindUnique(IEnumerable<(VersionId VersionId, string? FileName)> entries, string fileName)
    {
        var matches = entries.Where(e => StringComparer.OrdinalIgnoreCase.Equals(e.FileName, fileName))
            .Select(e => e.VersionId).Distinct().Take(2).ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException("[history.ambiguous-backup-name]")
        };
    }
}

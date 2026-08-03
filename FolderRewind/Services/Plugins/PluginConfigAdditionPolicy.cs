using System;
using System.Collections.Generic;
using System.IO;

namespace FolderRewind.Services.Plugins;

internal static class PluginConfigAdditionPolicy
{
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string trimmed = path.Trim();
        try
        {
            return Path.GetFullPath(trimmed)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    public static string ResolveUniqueName(
        string? requestedName,
        ISet<string> usedNames,
        ISet<string> usedDestinationPaths,
        Func<string, string> destinationPathFactory,
        out string destinationPath)
    {
        string baseName = string.IsNullOrWhiteSpace(requestedName)
            ? "Backup"
            : requestedName.Trim();

        int suffix = 1;
        while (true)
        {
            string candidateName = suffix == 1 ? baseName : $"{baseName} ({suffix})";
            string candidateDestination = destinationPathFactory(candidateName);
            string normalizedDestination = NormalizePath(candidateDestination);

            if (!usedNames.Contains(candidateName)
                && !usedDestinationPaths.Contains(normalizedDestination))
            {
                destinationPath = candidateDestination;
                return candidateName;
            }

            suffix++;
        }
    }
}

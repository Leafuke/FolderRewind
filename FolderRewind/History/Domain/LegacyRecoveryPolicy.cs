using System;
using System.IO;

namespace FolderRewind.History.Domain;

public static class LegacyRecoveryPolicy
{
    public const string BoundaryDiagnostic = "Historical deletion boundary is unknown. Export to a new directory or use non-deleting overwrite; create a new full backup for branches, checkout and merge.";

    public static void RequireKnownBoundary(SourceVersion version)
    {
        if (version.BoundaryConfidence != HistoricalBoundaryConfidence.Known)
            throw new InvalidOperationException(BoundaryDiagnostic);
    }

    public static void ValidateOverlay(SourceVersion version, string staging, string target)
    {
        if (version.BoundaryConfidence != HistoricalBoundaryConfidence.Unknown) return;
        foreach (var path in Directory.EnumerateFileSystemEntries(staging, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(staging, path));
            if ((File.Exists(path) && Directory.Exists(destination))
                || (Directory.Exists(path) && File.Exists(destination)))
                throw new IOException("Legacy overwrite would replace a file with a directory or delete a directory. Export to a new directory instead.");
        }
    }
}

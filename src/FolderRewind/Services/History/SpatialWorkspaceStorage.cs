using FolderRewind.Plugin.Runtime.Artifacts;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static class SpatialWorkspaceStorage
{
    internal static Task<OwnedPluginTemporaryStorage> CreateAsync(string logicalRoot, IEnumerable<string> protectedRoots,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // SQLite's Windows journal path must also fit native path limits. Reuse the
        // existing logical-root identity and locked private-owner storage strategy.
        var physicalRoot = OwnedPluginTemporaryStorage.ForLogicalRoot(logicalRoot);
        foreach (var protectedRoot in protectedRoots)
        {
            if (!BackupPathOverlapPolicy.Validate(logicalRoot, protectedRoot).IsSafe
                || !BackupPathOverlapPolicy.Validate(physicalRoot, protectedRoot).IsSafe)
                throw new IOException(I18n.GetString("Preview_HistoryStorageInvalid"));
        }
        return OwnedPluginTemporaryStorage.CreateAsync(physicalRoot, token);
    }
}

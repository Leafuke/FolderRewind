using FolderRewind.History.LocalState;
using FolderRewind.Plugin.Runtime.Artifacts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace FolderRewind.Services;

/// <summary>A complete unpublished index. Disposing it removes only its newly allocated private directory.</summary>
internal sealed class SpatialMergeIndexBuild(SpatialWorkspaceIndex index, MergeChangeSnapshot snapshot, string directory) : IDisposable
{
    internal SpatialWorkspaceIndex Index { get; } = index;
    internal MergeChangeSnapshot Snapshot { get; set; } = snapshot;
    internal string DirectoryPath { get; } = directory;
    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        Index.Dispose();
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
    }
}

internal static class SpatialMergeIndexBuilder
{
    internal static SpatialMergeIndexBuild Build(MergeSessionStore store, MergeSession expected, string ownedRoot,
        Func<MergeChangeRecord, SpatialWorkspaceRow> project, CancellationToken token = default,
        Action<long, int?>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        if (expected.State is not (MergeSessionState.Ready or MergeSessionState.Resolving))
            throw new InvalidOperationException("Only a complete Merge analysis can initialize a spatial index.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownedRoot));
        var directory = Path.GetFullPath(Path.Combine(root, "merge-" + Guid.NewGuid().ToString("N")));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Spatial index escaped its owned root.");
        SpatialWorkspaceIndex? index = null;
        try
        {
            HostArtifactWorkspace.CreatePrivateDirectory(directory);
            index = new SpatialWorkspaceIndex(Path.Combine(directory, "spatial.db"));
            long written = 0; int? total = null;
            var snapshot = store.ExportChangesSnapshot(expected, batch =>
            {
                token.ThrowIfCancellationRequested();
                index.Put(batch.Select(record => { token.ThrowIfCancellationRequested(); return project(record); }), allowReplace: false);
                written += batch.Count;
                progress?.Invoke(written, total);
                token.ThrowIfCancellationRequested();
            }, token, count => { total = count; progress?.Invoke(0, count); });
            token.ThrowIfCancellationRequested();
            if (written != snapshot.Total || index.Counts().Total != snapshot.Total
                || snapshot.SessionId != expected.Id || snapshot.PlanRevision != expected.Plan.Revision || snapshot.Revision != expected.Revision)
                throw new InvalidDataException("Spatial index does not match the fixed Merge snapshot.");
            RequireCurrent(store, expected, snapshot);
            token.ThrowIfCancellationRequested();
            return new(index, snapshot, directory);
        }
        catch
        {
            index?.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            throw;
        }
    }
    internal static void RequireCurrent(MergeSessionStore store, MergeSession expected, MergeChangeSnapshot snapshot)
    {
        var current = store.Load(expected.Id);
        if (current.Id != snapshot.SessionId || current.Plan.Revision != snapshot.PlanRevision || current.Revision != snapshot.Revision
            || current.State != expected.State || current.State is not (MergeSessionState.Ready or MergeSessionState.Resolving))
            throw new MergeChangeSnapshotStaleException();
    }
}

using FolderRewind.History.Domain;
using FolderRewind.History.Merge;
using FolderRewind.Models;
using FolderRewind.Plugin.Runtime.Artifacts;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>Host verifies and owns the fixed input trees; presentation code only holds their leases.</summary>
internal static class SpatialWorkspaceInputPreparation
{
    internal static async Task<(SpatialWorkspaceInput Input, HistoricalPreviewWorkspace Lease)> PrepareHistoryAsync(
        BackupConfig config, Guid folderId, string key, CancellationToken token)
    {
        var history = await NativeHistoryApplicationService.PrepareWorldPreviewAsync(config, folderId, new(Guid.Parse(key)), null, token).ConfigureAwait(false);
        try
        {
            var tree = await MergeTreeManifest.ReadAsync(history.DirectoryPath, _ => true, token).ConfigureAwait(false);
            return (new(key, new(key + "/" + history.RepresentationId + "/" + tree.Digest, history.DirectoryPath, tree.Digest)
                { Coverage = new SpatialWorkspaceCoverage(history.Version, tree) }, history.IsPartial), history);
        }
        catch { await history.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal static async Task<SpatialWorkspaceInput> PrepareMergeAsync(MergeOperationService operations, Guid folderId,
        string key, OwnedPluginTemporaryStorage storage, CancellationToken token)
    {
        var session = operations.Snapshot.Session ?? throw new InvalidOperationException("Merge session is unavailable.");
        var source = operations.Runtime!.MergeSessions.Sources(session).Single(s => s.Plan.SourceId.Value == folderId);
        MergeTreeManifest tree; SourceVersion? version; string root;
        if (key == "result")
        {
            var review = operations.Review ?? throw new InvalidOperationException(I18n.GetString("SpatialWorkspace_ResultExpired"));
            review.RequireIdentity(session, review.Prepared);
            var candidate = review.Prepared.Sources.Single(s => s.Version.SourceId.Value == folderId);
            root = candidate.StagingDirectory; version = candidate.Version;
            tree = await MergeTreeManifest.ReadAsync(root, _ => true, token).ConfigureAwait(false);
            if (tree.Digest != candidate.TreeDigest) throw new InvalidDataException("Prepared Merge tree changed.");
        }
        else
        {
            tree = key switch { "base" => source.Base, "ours" => source.Ours, "theirs" => source.Theirs, _ => throw new ArgumentException("Unknown input.") };
            var checkpoint = key switch { "base" => source.Plan.Base, "ours" => source.Plan.Ours, _ => source.Plan.Theirs };
            version = checkpoint?.VersionId is { } id ? await operations.Runtime.Query.GetVersionAsync(id, token).ConfigureAwait(false) : null;
            root = tree.Files.IsEmpty ? await storage.CreateDirectoryAsync(token).ConfigureAwait(false) : SpatialWorkspaceCoverage.RootOf(tree);
            if (!tree.Files.IsEmpty && (await MergeTreeManifest.ReadAsync(root, _ => true, token).ConfigureAwait(false)).Digest != tree.Digest)
                throw new InvalidDataException("Fixed Merge input changed.");
        }
        if (operations.Snapshot.Session?.Id != session.Id || operations.Snapshot.Session.Plan.Revision != session.Plan.Revision)
            throw new InvalidOperationException(I18n.GetString("SpatialWorkspace_PlanExpired"));
        return new(key, new($"{session.Id}/{session.Plan.Revision}/{key}/{tree.Digest}", root, tree.Digest)
            { Coverage = version is null ? null : new SpatialWorkspaceCoverage(version, tree) }, version?.CaptureScope == CaptureScope.PartialSource);
    }
}

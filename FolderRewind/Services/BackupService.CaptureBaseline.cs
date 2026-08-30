using FolderRewind.History.Application;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static partial class BackupService
{
    /// <summary>
    /// Rebuilds the disposable capture cache after a restore changes the active Workspace.
    /// Exact/Clean restores can immediately participate in SkipIfUnchanged and Smart capture;
    /// Derived/Overwrite states invalidate the cache because no Version exactly represents them.
    /// </summary>
    internal static async Task SynchronizeCaptureBaselinesWithWorkspaceAsync(
        BackupConfig config,
        IEnumerable<SourceId> affectedSources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var affected = affectedSources?.ToHashSet() ?? [];
        if (affected.Count == 0) return;
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var workspace = (await runtime.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Value;
        if (workspace is null) return;
        var representations = await runtime.Query.GetAllRepresentationsAsync(cancellationToken).ConfigureAwait(false);
        var representationsById = representations.ToDictionary(item => item.RepresentationId);
        var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Value;

        foreach (var folder in config.SourceFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(folder.Id, out var sourceGuid) || sourceGuid == Guid.Empty) continue;
            var sourceId = new SourceId(sourceGuid);
            if (!affected.Contains(sourceId)) continue;
            try
            {
                var workspaceBaseline = workspace.SourceBaselines.FirstOrDefault(item => item.SourceId == sourceId);
                if (workspaceBaseline is not
                    {
                        Relation: WorkspaceBaselineRelation.Exact,
                        BaseVersionId: { } versionId
                    }
                    || !Directory.Exists(folder.Path))
                {
                    await RemoveCaptureBaselineAsync(runtime, sourceId, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var local = representations
                    .Where(item => item.VersionId == versionId && item.RestoreStrategy == RestoreStrategy.Exact)
                    .SelectMany(representation => (catalog?.Entries ?? [])
                        .Where(entry => entry.RepresentationId == representation.RepresentationId
                            && entry.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath)
                        .Select(entry => (Representation: representation, Path: entry.Locator.AbsolutePath)))
                    .Where(item => File.Exists(item.Path))
                    .OrderBy(item => RepresentationPreference(item.Representation.Kind))
                    .ThenBy(item => item.Representation.RepresentationId.ToString(), StringComparer.Ordinal)
                    .FirstOrDefault();
                if (local.Representation is null || string.IsNullOrWhiteSpace(local.Path))
                {
                    await RemoveCaptureBaselineAsync(runtime, sourceId, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var currentStates = ScanDirectory(
                    folder.Path,
                    config.Filters,
                    selection: folder.SourceScope);
                var existing = await runtime.CaptureBaselines.LoadAsync(sourceId, cancellationToken).ConfigureAwait(false);
                if (existing is not null
                    && existing.BaseVersionId == versionId
                    && existing.BaseRepresentationId == local.Representation.RepresentationId
                    && string.Equals(existing.PayloadPath, local.Path, StringComparison.OrdinalIgnoreCase)
                    && FileStatesEqual(existing.FileStates, currentStates))
                {
                    continue;
                }

                var smartDepth = CountSmartDepth(local.Representation, representationsById, []);
                await runtime.CaptureBaselines.SaveAsync(
                    sourceId,
                    new SourceCaptureBaselineCandidate(
                        existing?.Revision ?? SourceCaptureBaselineCache.MissingRevision,
                        local.Path,
                        smartDepth,
                        currentStates.ToImmutableSortedDictionary(
                            pair => pair.Key,
                            pair => pair.Value,
                            StringComparer.Ordinal)),
                    versionId,
                    local.Representation,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"[History] Failed to synchronize capture baseline for Source {sourceId}: {ex.Message}", LogLevel.Warning);
                try { await RemoveCaptureBaselineAsync(runtime, sourceId, CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }
        }
    }

    private static async Task RemoveCaptureBaselineAsync(
        HistoryRuntime runtime,
        SourceId sourceId,
        CancellationToken cancellationToken)
    {
        var existing = await runtime.CaptureBaselines.LoadAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            _ = await runtime.CaptureBaselines.RemoveAsync(sourceId, existing.Revision, cancellationToken).ConfigureAwait(false);
    }

    private static bool FileStatesEqual(
        IReadOnlyDictionary<string, SourceCaptureFileState> left,
        IReadOnlyDictionary<string, SourceCaptureFileState> right)
        => left.Count == right.Count
            && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static int CountSmartDepth(
        VersionRepresentation representation,
        IReadOnlyDictionary<RepresentationId, VersionRepresentation> representations,
        HashSet<RepresentationId> visited)
    {
        if (representation.Kind != RepresentationKind.CoreSmartDelta
            || !visited.Add(representation.RepresentationId)) return 0;
        var parentDepth = representation.DependencyRepresentationIds
            .Where(representations.ContainsKey)
            .Select(id => CountSmartDepth(representations[id], representations, visited))
            .DefaultIfEmpty(0)
            .Max();
        return checked(parentDepth + 1);
    }

    private static int RepresentationPreference(RepresentationKind kind) => kind switch
    {
        RepresentationKind.CoreFull => 0,
        RepresentationKind.CoreRolling => 1,
        RepresentationKind.CoreSmartDelta => 2,
        RepresentationKind.LegacyArchive => 3,
        _ => 4
    };
}

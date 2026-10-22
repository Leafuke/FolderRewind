using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Services.Plugins.V3;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static partial class NativeHistoryApplicationService
{
    // This is deliberately separate from ExportVersionAsync: its public destination protection stays strict.
    internal static async Task<HistoricalPreviewWorkspace> PrepareWorldPreviewAsync(BackupConfig expected,
        Guid folderId, VersionId versionId, IProgress<HistoricalPreviewStage>? progress, CancellationToken token)
    {
        var config = ConfigSnapshotMapper.Copy(expected);
        var signature = NativeHistoryConfigLease.Signature(config);
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterAsync(config.Id, token).ConfigureAwait(false);
        await using var guard = await NativeHistoryConfigLease.EnterInsideOperationAsync(config, signature, operation, token).ConfigureAwait(false);
        var origin = PluginV3ModelMapper.ToSnapshot(config);
        _ = origin.Folders.Single(f => f.FolderId == folderId);
        if (!PluginV3SpatialPreview.CanPreview(config))
            throw new HistoricalPreviewUnavailableException(I18n.GetString("Preview_ProviderUnavailable"));
        progress?.Report(HistoricalPreviewStage.Assessing);
        var runtime = await NativeHistoryCoreGateway.EnsureReadableAsync(config, token).ConfigureAwait(false);
        var version = await runtime.Query.GetVersionAsync(versionId, token).ConfigureAwait(false);
        if (version is null || version.SourceId.Value != folderId || !version.ConfigId.Matches(config.Id))
            throw new HistoricalPreviewUnavailableException(I18n.GetString("Export_IdentityChanged"));
        var partial = version.CaptureScope == CaptureScope.PartialSource || version.BoundaryConfidence == HistoricalBoundaryConfidence.Unknown;
        var fidelity = partial ? MaterializationFidelity.Partial : MaterializationFidelity.Exact;
        var engine = HistoryRepresentationRuntimeFactory.Create(config, runtime);
        var graph = await runtime.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        var environment = await BuildEnvironmentAsync(runtime, token).ConfigureAwait(false);
        var assessment = await engine.AssessVersionAsync(versionId, graph, environment, AssessmentDepth.Fast, fidelity, token).ConfigureAwait(false);
        if (assessment.Readiness != HistoryReadiness.Ready || assessment.Selected is null)
            throw new HistoricalPreviewUnavailableException(I18n.GetString(assessment.Readiness == HistoryReadiness.PreparationRequired
                ? "Preview_HistoryDownloadRequired" : "Preview_HistoryUnavailable"), assessment.Readiness == HistoryReadiness.PreparationRequired);
        // Local reads only. This never downloads, creates a History commit, or enters the restore coordinator.
        var root = RequirePreviewRoot(config, runtime.Repository.Paths.RepositoryRoot);
        progress?.Report(HistoricalPreviewStage.Verifying);
        using var read = await engine.LockVersionAsync(versionId, graph, environment, fidelity, token).ConfigureAwait(false);
        OwnedPluginTemporaryStorage? storage = null;
        string? directory = null;
        try
        {
            storage = await OwnedPluginTemporaryStorage.CreateAsync(root, token).ConfigureAwait(false);
            directory = await storage.CreateDirectoryAsync(token).ConfigureAwait(false);
            // Archive listings reserve their decoded closure size; Native uses its evidence/file-view budget.
            read.VerificationContext.ReserveMaterializationSpace(read.RepresentationId, graph, directory, ArtifactSpaceBudget.Shared);
            progress?.Report(HistoricalPreviewStage.Materializing);
            await engine.MaterializeAsync(read.RepresentationId, graph, environment, fidelity, directory, token, read.VerificationContext).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            HostArtifactWorkspace.ValidateOwnedDirectory(directory, token);
            partial |= graph.Single(r => r.RepresentationId == read.RepresentationId).Fidelity != MaterializationFidelity.Exact;
            return new(storage, directory, origin, folderId, version, partial);
        }
        catch
        {
            if (storage is not null) await storage.DeleteAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string RequirePreviewRoot(BackupConfig config, string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(config.DestinationPath) || !Path.IsPathFullyQualified(config.DestinationPath))
            throw new IOException(I18n.GetString("Preview_HistoryStorageInvalid"));
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.DestinationPath));
        var root = Path.GetFullPath(Path.Combine(destination, PreviewStoragePaths.DirectoryName, Guid.Parse(config.Id).ToString("N")));
        var prefix = Path.EndsInDirectorySeparator(destination) ? destination : destination + Path.DirectorySeparatorChar;
        if (!root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException(I18n.GetString("Preview_HistoryStorageInvalid"));
        var protectedRoots = ConfigService.CurrentConfig.BackupConfigs.SelectMany(c => c.SourceFolders.Select(f => f.Path))
            .Append(ConfigService.ConfigDirectory).Append(repositoryRoot).Where(p => !string.IsNullOrWhiteSpace(p));
        foreach (var path in protectedRoots)
            if (!BackupPathOverlapPolicy.Validate(root, path).IsSafe)
                throw new IOException(I18n.GetString("Preview_HistoryStorageInvalid"));
        // CreateAsync also checks every ancestor and creates a private owner ACL.
        return root;
    }
}

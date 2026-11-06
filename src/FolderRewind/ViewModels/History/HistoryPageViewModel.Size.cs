using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed partial class HistoryPageViewModel
{
    private readonly LatestRequestCoordinator _sizeRequests = new();
    private readonly Dictionary<(VersionId, RepresentationId), BackupSizeStatistics> _sizeCache = [];
    private HistoryRuntime? _sizeCacheRuntime;
    private long _sizeCacheSequence = -1;

    public async Task LoadSizeDetailsAsync(VersionId versionId, RepresentationId? representationId,
        BackupSizeViewModel details, CancellationToken token)
    {
        var runtime = _presentationRuntime;
        var config = _currentConfig;
        if (runtime is null || config is null || representationId is not { } representation) return;
        using var request = _sizeRequests.Begin(token);
        var sequence = runtime.ChangeFeed.CurrentSequence;
        if (_sizeCacheRuntime != runtime || _sizeCacheSequence != sequence)
        {
            _sizeCache.Clear(); _sizeCacheRuntime = runtime; _sizeCacheSequence = sequence;
        }
        if (_sizeCache.TryGetValue((versionId, representation), out var cached))
        {
            details.Update(new(details.Statistics.AddedStoredBytes, cached.LogicalContentBytes, cached.RestoreDependencyBytes));
        }
        if (details.BackupType != HistoryBackupType.Semantic && details.Statistics.LogicalContentBytes is not null
            && details.Statistics.RestoreDependencyBytes is not null) return;
        details.IsLoading = true;
        try
        {
            // Freeze the already available credential on the UI side; statistics never prompt or download.
            var archive = new SevenZipHistoryArchiveBackend(config, freezeInput: true);
            if (details.BackupType == HistoryBackupType.Semantic)
            {
                var type = await Task.Run(async () =>
                {
                    var version = await runtime.Query.GetVersionAsync(versionId, request.Token).ConfigureAwait(false);
                    var node = (await runtime.Query.GetAllRepresentationsAsync(request.Token).ConfigureAwait(false))
                        .SingleOrDefault(item => item.RepresentationId == representation && item.VersionId == versionId);
                    return version is not null && node is not null
                        ? await NativeArtifactHostService.ReadBackupTypeAsync(config, version, node, archive, request.Token).ConfigureAwait(false)
                        : HistoryBackupType.Semantic;
                }, request.Token);
                if (!request.IsCurrent || !_isActive || runtime != _presentationRuntime || runtime.ChangeFeed.CurrentSequence != sequence) return;
                details.UpdateBackupType(type);
            }
            if (details.Statistics.LogicalContentBytes is not null && details.Statistics.RestoreDependencyBytes is not null) return;
            var reader = new HistoryBackupSizeService(runtime, async (node, path, cancellation) =>
            {
                var verified = await archive.VerifyAsync(node, path, cancellation).ConfigureAwait(false);
                return verified.Success && archive.TryGetListing(path, out var listing) && listing is not null
                    ? listing.Files.Where(pair => !pair.Key.StartsWith(BackupService.InternalRestoreMarkerDirectoryName + "/", StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(pair => pair.Key, pair => pair.Value.Size, StringComparer.OrdinalIgnoreCase) : null;
            }, (version, node, cancellation) => NativeArtifactHostService.ReadBackupSizeAsync(config, version, node, archive, cancellation));
            var result = await Task.Run(() => reader.ReadAsync(versionId, representation, details.Statistics, request.Token), request.Token);
            if (!request.IsCurrent || !_isActive || runtime != _presentationRuntime || runtime.ChangeFeed.CurrentSequence != sequence) return;
            // Unknown observations may become readable after a credential is supplied; do not cache them.
            if (result.LogicalContentBytes is not null && result.RestoreDependencyBytes is not null)
            {
                if (_sizeCache.Count >= 128) _sizeCache.Clear();
                _sizeCache[(versionId, representation)] = result;
            }
            details.Update(result);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception error) { LogService.LogWarning($"Could not read backup size details: {error.Message}", "History"); }
        finally { details.IsLoading = false; }
    }
}

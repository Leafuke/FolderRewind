using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Operations;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Plugins.V3;

internal sealed class PluginV3SpatialPreview
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _calls = [];
    private bool _closed;
    private Task? _closeTask;
    private readonly ConfigSnapshot _origin;
    private readonly FolderSnapshot _originFolder;
    public SpatialPreviewSource Source { get; }
    public string CameraIdentity { get; }
    public bool IsHistorical { get; }
    public string OriginalPath => _originFolder.Path;
    private PluginV3SpatialPreview(SpatialPreviewSource source, ConfigSnapshot origin, string? versionIdentity = null)
    {
        Source = source; _origin = origin; IsHistorical = versionIdentity is not null;
        _originFolder = origin.Folders.Single(f => f.FolderId == source.Folder.FolderId);
        CameraIdentity = _originFolder.Path + (versionIdentity is null ? "" : "|history:" + versionIdentity);
    }
    public static PluginV3SpatialPreview Open(string configId, Guid folderId)
    {
        var config = ConfigService.CurrentConfig.BackupConfigs.Single(c => c.Id == configId);
        var snapshot = PluginV3ModelMapper.ToSnapshot(config);
        return new(new(snapshot, snapshot.Folders.Single(f => f.FolderId == folderId), Guid.NewGuid()), snapshot);
    }
    public static PluginV3SpatialPreview Open(HistoricalPreviewWorkspace workspace)
    {
        var origin = workspace.Origin;
        var folder = origin.Folders.Single(f => f.FolderId == workspace.FolderId) with { Path = workspace.DirectoryPath };
        var mapped = origin with { Folders = Array.AsReadOnly(origin.Folders.Select(f => f.FolderId == folder.FolderId ? folder : f).ToArray()) };
        return new(new(mapped, folder, Guid.NewGuid()) { IsPartial = workspace.IsPartial }, origin, workspace.Version.VersionId.ToString());
    }
    public static bool IsCurrent(ConfigSnapshot origin, Guid folderId)
    {
        var config = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(c => c.Id == origin.ConfigId);
        if (config is null) return false;
        var current = PluginV3ModelMapper.ToSnapshot(config);
        var folder = origin.Folders.Single(f => f.FolderId == folderId);
        return current.Revision == origin.Revision && current.Kind == origin.Kind
            && current.Folders.Any(f => f.FolderId == folder.FolderId && f.Path == folder.Path);
    }
    public static bool CanPreview(BackupConfig config)
    {
        var kind = PluginV3ModelMapper.ToKind(config);
        using var lease = PluginV3RuntimeService.Runtime.TryAcquire<ISpatialPreviewCapability>(new(kind.OwnerId.Value), c => c.Kind == kind);
        return lease is not null;
    }
    public bool IsCurrent() => IsCurrent(_origin, _originFolder.FolderId);
    public Task<SpatialPreviewDescription> DescribeAsync(CancellationToken token)
        => RunAsync((d, ct) => d.DescribeAsync(Source, ct), token);
    public Task<SpatialPreviewTile> RenderAsync(SpatialPreviewTileRequest request, CancellationToken token)
        => RunAsync((d, ct) => d.RenderAsync(request, ct), token);
    public Task<SpatialPreviewPoint> InspectAsync(SpatialPreviewPointRequest request, CancellationToken token)
        => RunAsync((d, ct) => d.InspectAsync(request, ct), token);
    public Task<SpatialPreviewNavigation> GetNavigationTargetsAsync(CancellationToken token)
        => RunAsync((d, ct) => d.GetNavigationTargetsAsync(Source, ct), token);
    private Task<T> RunAsync<T>(Func<SpatialPreviewDispatcher, CancellationToken, ValueTask<T>> action, CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!IsCurrent()) throw new InvalidOperationException(I18n.GetString("Preview_SourceChanged"));
            var task = Task.Run(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
                using var scope = NativeHostMutationContext.EnterCoordinatorCallback();
                return await action(new(PluginV3RuntimeService.Runtime), linked.Token).ConfigureAwait(false);
            }, token);
            _calls.Add(task);
            _ = task.ContinueWith(t => { lock (_gate) _calls.Remove(t); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }
    public Task CloseAsync()
    {
        lock (_gate)
        {
            if (_closeTask is not null) return _closeTask;
            _closed = true;
            _lifetime.Cancel();
            return _closeTask = CloseCoreAsync(_calls.ToArray());
        }
    }
    private async Task CloseCoreAsync(Task[] calls)
    {
        // Drain initialization too: a late Describe must not resurrect a generation after cleanup.
        try { await Task.WhenAll(calls).ConfigureAwait(false); } catch { /* Original callers observe errors. */ }
        using var scope = NativeHostMutationContext.EnterCoordinatorCallback();
        try { await new SpatialPreviewDispatcher(PluginV3RuntimeService.Runtime).CloseAsync(Source).ConfigureAwait(false); }
        finally { _lifetime.Dispose(); }
    }
    public static string Localize(LocalizedText value) => I18n.PickBest(value.Translations, value.Default) ?? value.Default;
}

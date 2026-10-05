using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Operations;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Plugins.V3;

internal sealed class PluginV3SpatialPreview(SpatialPreviewSource source)
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _calls = [];
    private bool _closed;
    private Task? _closeTask;
    public SpatialPreviewSource Source { get; } = source;
    public static PluginV3SpatialPreview Open(string configId, Guid folderId)
    {
        var config = ConfigService.CurrentConfig.BackupConfigs.Single(c => c.Id == configId);
        var snapshot = PluginV3ModelMapper.ToSnapshot(config);
        return new(new(snapshot, snapshot.Folders.Single(f => f.FolderId == folderId), Guid.NewGuid()));
    }
    public static bool CanPreview(BackupConfig config)
    {
        var kind = PluginV3ModelMapper.ToKind(config);
        using var lease = PluginV3RuntimeService.Runtime.TryAcquire<ISpatialPreviewCapability>(new(kind.OwnerId.Value), c => c.Kind == kind);
        return lease is not null;
    }
    public bool IsCurrent()
    {
        var config = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(c => c.Id == Source.Config.ConfigId);
        if (config is null) return false;
        var current = PluginV3ModelMapper.ToSnapshot(config);
        return current.Revision == Source.Config.Revision && current.Kind == Source.Config.Kind
            && current.Folders.Any(f => f.FolderId == Source.Folder.FolderId && f.Path == Source.Folder.Path);
    }
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

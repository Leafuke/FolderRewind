using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Operations;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Plugins.V3;

internal sealed class PluginV3SpatialPreview(SpatialPreviewSource source)
{
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
        => RunAsync(d => d.DescribeAsync(Source, token), token);
    public Task<SpatialPreviewTile> RenderAsync(SpatialPreviewTileRequest request, CancellationToken token)
        => RunAsync(d => d.RenderAsync(request, token), token);
    public Task<SpatialPreviewPoint> InspectAsync(SpatialPreviewPointRequest request, CancellationToken token)
        => RunAsync(d => d.InspectAsync(request, token), token);
    private Task<T> RunAsync<T>(Func<SpatialPreviewDispatcher, ValueTask<T>> action, CancellationToken token)
    {
        if (!IsCurrent()) throw new InvalidOperationException(I18n.GetString("Preview_SourceChanged"));
        return Task.Run(async () =>
        {
            using var scope = NativeHostMutationContext.EnterCoordinatorCallback();
            return await action(new(PluginV3RuntimeService.Runtime)).ConfigureAwait(false);
        }, token);
    }
    public static string Localize(LocalizedText value) => I18n.PickBest(value.Translations, value.Default) ?? value.Default;
}

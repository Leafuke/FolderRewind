#if NATIVE_ARTIFACT_ACCEPTANCE
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Development;

// Acceptance-only capability: no filesystem, cloud, configuration writes, or game data.
internal static class PerformanceFakePreview
{
    internal static PluginId Id { get; } = new("folderrewind.performance");
    internal static ConfigKindRef Kind { get; } = new(new(Id.Value), "preview");
    private static readonly ConcurrentDictionary<Guid, byte> Generations = new();
    private static long _renders, _closes;
    internal static (int Generations, long Renders, long Closes) Statistics
        => (Generations.Count, Interlocked.Read(ref _renders), Interlocked.Read(ref _closes));

    internal static async Task ActivateAsync(ConfigSnapshot snapshot, CancellationToken token = default)
    {
        if (!AppRuntimeInfo.IsMsiDistribution
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_DATA_ROOT")))
            throw new InvalidOperationException("The performance provider requires an isolated MSI acceptance process.");
        if (snapshot.Kind != Kind) throw new ArgumentException("Unexpected performance fixture kind.", nameof(snapshot));
        var result = await PluginV3RuntimeService.Runtime.ActivateAsync(new(Id, () => new Provider(),
            new(Id, new Dictionary<string, JsonElement>()), [snapshot], new NoHostServices(snapshot), new NoWrites()), token);
        if (!result.Success) throw new InvalidOperationException("Could not activate the performance preview provider: "
            + string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + string.Join(", ", d.Arguments.Select(p => p.Key + "=" + p.Value)))));
    }

    internal static async Task DeactivateAsync(CancellationToken token = default)
    {
        var result = await PluginV3RuntimeService.Runtime.DeactivateAsync(Id, token);
        if (!result.Success) throw new InvalidOperationException("Performance preview provider did not drain.");
    }

    private static LocalizedText Text(string value) => new(value, new Dictionary<string, string>());

    private sealed class Provider : IFolderRewindPlugin, IFixedSpatialPreviewCapability, ISpatialComparisonCapability
    {
        private static readonly byte[] Pixels = MakePixels();
        public ConfigKindRef Kind => PerformanceFakePreview.Kind;
        public ValueTask<PluginActivationResult> ActivateAsync(IPluginActivationContext context, CancellationToken token)
        {
            context.RegisterCapability<IPluginCapability>(this);
            return ValueTask.FromResult(PluginActivationResult.Empty);
        }
        public ValueTask DeactivateAsync(CancellationToken token)
        {
            Generations.Clear();
            return ValueTask.CompletedTask;
        }
        public ValueTask<SpatialPreviewDescription> DescribeAsync(SpatialPreviewSource source, PluginInvocationContext context)
        {
            context.OperationCancellation.ThrowIfCancellationRequested();
            Generations.TryAdd(source.Generation, 0);
            return ValueTask.FromResult(new SpatialPreviewDescription(
                [new("layer", Text("Acceptance raster"), new(-32768, -32768, 32768, 32768), 16, 512, null)],
                Text("X"), Text("Z"), []) { Worlds = [new("world", Text("Acceptance world"))] });
        }
        public async ValueTask<SpatialPreviewTile> RenderAsync(SpatialPreviewTileRequest request, PluginInvocationContext context)
        {
            await Task.Delay(4, context.OperationCancellation);
            Generations.TryAdd(request.Source.Generation, 0);
            Interlocked.Increment(ref _renders);
            return new(256, 256, Pixels, []) { IsFinal = true, ContentState = SpatialPreviewContentState.HasData };
        }
        public ValueTask<SpatialPreviewPoint> InspectAsync(SpatialPreviewPointRequest request, PluginInvocationContext context)
        {
            context.OperationCancellation.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SpatialPreviewPoint([], []));
        }
        public ValueTask CloseAsync(SpatialPreviewSource source, PluginInvocationContext context)
        {
            if (Generations.TryRemove(source.Generation, out _)) Interlocked.Increment(ref _closes);
            return ValueTask.CompletedTask;
        }
        public ValueTask<SpatialComparisonDescription> DescribeComparisonAsync(SpatialComparisonRequest request, PluginInvocationContext context)
            => ValueTask.FromResult(new SpatialComparisonDescription([new("blocks", Text("Blocks"), true)]));
        public ValueTask<SpatialComparisonPage> CompareAsync(SpatialComparisonRequest request, PluginInvocationContext context)
        {
            context.OperationCancellation.ThrowIfCancellationRequested();
            int offset = request.Cursor is null ? 0 : int.Parse(request.Cursor, CultureInfo.InvariantCulture);
            const int total = 100_000;
            int count = Math.Min(Math.Clamp(request.Count, 1, 500), Math.Max(0, total - offset));
            var rows = Enumerable.Range(offset, count).Select(i => new SpatialChange("change-" + i,
                Text("Cell " + i), SpatialChangeKind.Modified,
                new("world", "layer", new((i % 1000) * 16, (i / 1000) * 16, (i % 1000 + 1) * 16, (i / 1000 + 1) * 16)),
                ["blocks"], Text("Deterministic acceptance change"))).ToArray();
            return ValueTask.FromResult(new SpatialComparisonPage(rows,
                offset + count < total ? (offset + count).ToString(CultureInfo.InvariantCulture) : null, offset + count));
        }
        public ValueTask CloseComparisonAsync(Guid comparisonId, PluginInvocationContext context) => ValueTask.CompletedTask;
        private static byte[] MakePixels()
        {
            var result = new byte[256 * 256 * 4];
            for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                int p = (y * 256 + x) * 4;
                result[p] = (byte)(80 + x / 4); result[p + 1] = (byte)(100 + y / 4);
                result[p + 2] = (byte)((x / 32 + y / 32) % 2 == 0 ? 60 : 110); result[p + 3] = 255;
            }
            return result;
        }
    }
    private sealed class NoWrites : IPluginActivationStore
    {
        public ValueTask CommitAsync(PluginActivationCommit commit, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (commit.ProviderStatePatches.Count != 0) throw new InvalidOperationException("Acceptance provider must not write state.");
            return ValueTask.CompletedTask;
        }
    }
    private sealed class NoHostServices(ConfigSnapshot snapshot) : IPluginHostServices
    {
        public IReadOnlyConfigQueryService Configs { get; } = new ConfigQuery(snapshot);
        public IBackupRequestService Backups => throw new NotSupportedException();
        public IRestoreRequestService Restores => throw new NotSupportedException();
        public IHistoryQueryService History { get; } = new EmptyHistory();
        public IPluginNotificationService Notifications => throw new NotSupportedException();
        public IKnotLinkHostService KnotLink => throw new NotSupportedException();
        public IPluginDataStore DataStore => throw new NotSupportedException();
        public IPluginTemporaryStorage TemporaryStorage => throw new NotSupportedException();
        public IPluginLogger Logger { get; } = new EmptyLogger();
    }
    private sealed class ConfigQuery(ConfigSnapshot snapshot) : IReadOnlyConfigQueryService
    {
        public ValueTask<ConfigSnapshot?> FindAsync(string id, CancellationToken token)
            => ValueTask.FromResult<ConfigSnapshot?>(id == snapshot.ConfigId ? snapshot : null);
        public ValueTask<IReadOnlyList<ConfigSnapshot>> QueryAsync(ConfigKindRef? kind, CancellationToken token)
            => ValueTask.FromResult<IReadOnlyList<ConfigSnapshot>>(kind is null || kind == snapshot.Kind ? [snapshot] : []);
    }
    private sealed class EmptyHistory : IHistoryQueryService
    {
        public ValueTask<IReadOnlyList<HistoryVersionSnapshot>> QueryAsync(string id, Guid? folderId, CancellationToken token)
            => ValueTask.FromResult<IReadOnlyList<HistoryVersionSnapshot>>([]);
    }
    private sealed class EmptyLogger : IPluginLogger
    {
        public void Log(DiagnosticSeverity severity, string message, Exception? exception = null) { }
    }
}
#endif

#if SPATIAL_WORKSPACE_UI_ACCEPTANCE
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
internal static class SpatialWorkspaceUiProvider
{
    internal static PluginId Id { get; } = new("folderrewind.spatial-ui");
    internal static ConfigKindRef Kind { get; } = new(new(Id.Value), "preview");
    internal static int TotalCount { get; } = ReadTotalCount();
    private static int ReadTotalCount()
    {
        var value = Environment.GetEnvironmentVariable("FOLDERREWIND_SPATIAL_ROWS");
        if (value is null) return 100_000;
        if (int.TryParse(value, out var count) && count is 10_000 or 50_000 or 100_000) return count;
        throw new InvalidOperationException("Spatial measurement rows must be 10000, 50000, or 100000.");
    }
    private static readonly ConcurrentDictionary<Guid, byte> Generations = new();
    private static readonly ConcurrentDictionary<Guid, byte> Comparisons = new();
    private static IReadOnlyDictionary<string, SpatialFixedInput> _fixedInputs = new Dictionary<string, SpatialFixedInput>();
    private static TaskCompletionSource _comparisonBlocked = Signal(), _renderBlocked = Signal();
    private static int _holdComparison, _holdRender;
    private static long _comparisonCloses, _comparisonCancellations, _renderCancellations;
    private static long _renders, _closes;
    internal static (int Generations, long Renders, long Closes) Statistics
        => (Generations.Count, Interlocked.Read(ref _renders), Interlocked.Read(ref _closes));
    internal static (int Active, long Closes, long CanceledCalls, long CanceledRenders) ComparisonStatistics
        => (Comparisons.Count, Interlocked.Read(ref _comparisonCloses),
            Interlocked.Read(ref _comparisonCancellations), Interlocked.Read(ref _renderCancellations));
    internal static Task ComparisonBlocked => _comparisonBlocked.Task;
    internal static Task RenderBlocked => _renderBlocked.Task;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static void Configure(SpatialFixedInput left, SpatialFixedInput right, bool holdComparison, bool holdRender)
    {
        _fixedInputs = new Dictionary<string, SpatialFixedInput>(StringComparer.Ordinal) { [left.Identity] = left, [right.Identity] = right };
        _comparisonBlocked = Signal(); _renderBlocked = Signal();
        Volatile.Write(ref _holdComparison, holdComparison ? 1 : 0);
        Volatile.Write(ref _holdRender, holdRender ? 1 : 0);
    }
    internal static void HoldNextScan(bool render)
    {
        _comparisonBlocked = Signal(); _renderBlocked = Signal();
        Volatile.Write(ref _holdComparison, 1); Volatile.Write(ref _holdRender, render ? 1 : 0);
    }
    private static void RequireFixed(SpatialPreviewSource source)
    {
        if (source.FixedInput is not { } input || !_fixedInputs.TryGetValue(input.Identity, out var expected) ||
            input.RootPath != expected.RootPath || input.ContentDigest != expected.ContentDigest)
            throw new InvalidOperationException("The spatial UI fixture must consume its Host-owned fixed input.");
    }

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

    private sealed class Provider : IFolderRewindPlugin, IFixedSpatialPreviewCapability, ISpatialPreviewDemandCapability, ISpatialComparisonCapability
    {
        private static readonly byte[] Pixels = MakePixels();
        public ConfigKindRef Kind => SpatialWorkspaceUiProvider.Kind;
        public ValueTask UpdateDemandAsync(SpatialPreviewDemand demand, PluginInvocationContext context)
        { RequireFixed(demand.Source); context.OperationCancellation.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public ValueTask<PluginActivationResult> ActivateAsync(IPluginActivationContext context, CancellationToken token)
        {
            context.RegisterCapability<IPluginCapability>(this);
            return ValueTask.FromResult(PluginActivationResult.Empty);
        }
        public ValueTask DeactivateAsync(CancellationToken token)
        {
            if (!Generations.IsEmpty || !Comparisons.IsEmpty)
                throw new InvalidOperationException("Spatial UI sessions have not drained before deactivation.");
            return ValueTask.CompletedTask;
        }
        public ValueTask<SpatialPreviewDescription> DescribeAsync(SpatialPreviewSource source, PluginInvocationContext context)
        {
            RequireFixed(source);
            context.OperationCancellation.ThrowIfCancellationRequested();
            Generations.TryAdd(source.Generation, 0);
            return ValueTask.FromResult(new SpatialPreviewDescription(
                [new("layer", Text("Acceptance raster"), new(-32768, -32768, 32768, 32768), 16, 512, null)],
                Text("X"), Text("Z"), []) { Worlds = [new("world", Text("Acceptance world"))] });
        }
        public async ValueTask<SpatialPreviewTile> RenderAsync(SpatialPreviewTileRequest request, PluginInvocationContext context)
        {
            RequireFixed(request.Source);
            try
            {
                if (Volatile.Read(ref _holdRender) != 0)
                {
                    _renderBlocked.TrySetResult();
                    await Task.Delay(Timeout.Infinite, context.OperationCancellation);
                }
                await Task.Delay(4, context.OperationCancellation);
            }
            catch (OperationCanceledException) { Interlocked.Increment(ref _renderCancellations); throw; }
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
            => ValueTask.FromResult(new SpatialComparisonDescription([new("blocks", Text("Blocks"), true), new("entities", Text("Entities"), true)]));
        public async ValueTask<SpatialComparisonPage> CompareAsync(SpatialComparisonRequest request, PluginInvocationContext context)
        {
            RequireFixed(request.Left); RequireFixed(request.Right);
            context.OperationCancellation.ThrowIfCancellationRequested();
            Comparisons.TryAdd(request.ComparisonId, 0);
            int offset = request.Cursor is null ? 0 : int.Parse(request.Cursor, CultureInfo.InvariantCulture);
            if (offset >= 500 && Volatile.Read(ref _holdComparison) != 0)
            {
                _comparisonBlocked.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, context.OperationCancellation); }
                catch (OperationCanceledException) { Interlocked.Increment(ref _comparisonCancellations); throw; }
            }
            int total = TotalCount;
            int count = Math.Min(Math.Clamp(request.Count, 1, 500), Math.Max(0, total - offset));
            var rows = Enumerable.Range(offset, count).Select(i => new SpatialChange("change-" + i,
                Text("Cell " + i.ToString("D6", CultureInfo.InvariantCulture)), (SpatialChangeKind)(i % 5),
                new("world", "layer", new((i % 1000) * 16, (i / 1000) * 16, (i % 1000 + 1) * 16, (i / 1000 + 1) * 16)),
                [i % 2 == 0 ? "blocks" : "entities"], Text("Deterministic acceptance change"))).ToArray();
            return new SpatialComparisonPage(rows,
                offset + count < total ? (offset + count).ToString(CultureInfo.InvariantCulture) : null, offset + count);
        }
        public ValueTask CloseComparisonAsync(Guid comparisonId, PluginInvocationContext context)
        {
            if (Comparisons.TryRemove(comparisonId, out _)) Interlocked.Increment(ref _comparisonCloses);
            return ValueTask.CompletedTask;
        }
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

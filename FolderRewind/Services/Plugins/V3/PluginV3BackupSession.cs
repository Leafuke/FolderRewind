using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Plugin.Runtime.Operations;

namespace FolderRewind.Services.Plugins.V3;

/// <summary>
/// 负责单个备份源的捕获会话生命周期：一致性租约（Consistency Lease）获取、
/// 稳定读取视图（Stable SourcePath）、版本元数据捕获及会话完成清理。
/// 消费已经冻结的 <see cref="PluginV3BackupSourceResolution"/>，不再重复解析策略或有效边界。
/// </summary>
internal sealed class PluginV3BackupSession : IAsyncDisposable
{
    private readonly PluginV3CaptureLeaseOwner _captureLeaseOwner = new();
    private readonly PluginV3BackupSourceResolution _resolution;
    private readonly List<PluginDiagnostic> _diagnostics;

    public PluginV3BackupSession(PluginV3BackupSourceResolution resolution)
    {
        _resolution = resolution ?? throw new ArgumentNullException(nameof(resolution));
        _diagnostics = resolution.Diagnostics.ToList();
    }

    public PluginV3BackupSourceResolution SourceResolution => _resolution;
    public BackupConfig EffectiveConfig => _resolution.EffectiveConfig;
    public ManagedFolder EffectiveFolder => _resolution.EffectiveFolder;
    public OperationResolution Resolution => _resolution.Resolution;
    public EffectiveSourceBoundarySnapshot EffectiveBoundary => _resolution.EffectiveBoundary;
    public IReadOnlyList<PluginDiagnostic> Diagnostics => _diagnostics;
    public bool IsBlocked => _resolution.IsBlocked;
    public string SourcePath => _captureLeaseOwner.SourcePath ?? EffectiveFolder.Path;

    public static async ValueTask<PluginV3BackupSession> PrepareAsync(
        BackupConfig originalConfig,
        ManagedFolder originalFolder,
        CancellationToken cancellationToken = default)
    {
        var resolution = await PluginV3BackupSourceResolver.ResolveAsync(
            originalConfig,
            originalFolder,
            cancellationToken).ConfigureAwait(false);
        return new PluginV3BackupSession(resolution);
    }

    public static PluginV3BackupSession Create(PluginV3BackupSourceResolution resolution)
        => new(resolution);

    public async ValueTask AcquireConsistencyAsync(CancellationToken cancellationToken = default)
    {
        if (_resolution.PluginId is null || _resolution.ConfigSnapshot is null || _resolution.FolderSnapshot is null) return;
        await _captureLeaseOwner.AcquireAsync(
            PluginV3RuntimeService.Runtime,
            _resolution.PluginId.Value,
            _resolution.ConfigSnapshot,
            _resolution.FolderSnapshot,
            _resolution.Intent,
            _diagnostics,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PluginV3VersionMetadataCaptureResult> CaptureVersionMetadataAsync(
        CancellationToken cancellationToken = default)
    {
        if (_resolution.PluginId is null || _resolution.ConfigSnapshot is null || _resolution.FolderSnapshot is null)
            return PluginV3VersionMetadataCaptureResult.Empty;
        using var lease = PluginV3RuntimeService.Runtime.TryAcquire<IVersionMetadataProviderCapability>(
            _resolution.PluginId.Value,
            capability => capability.Kind == _resolution.ConfigSnapshot.Kind,
            cancellationToken);
        if (lease is null) return PluginV3VersionMetadataCaptureResult.Empty;
        var stableSourcePath = _captureLeaseOwner.StableSourcePath;
        if (stableSourcePath is null)
        {
            var diagnostic = new PluginDiagnostic(
                "plugin.version_metadata_stable_view_unavailable",
                DiagnosticSeverity.Warning,
                "VersionMetadata",
                _resolution.PluginId.Value.Value,
                new Dictionary<string, string>());
            _diagnostics.Add(diagnostic);
            return new PluginV3VersionMetadataCaptureResult([], [ToHistoryDiagnostic(diagnostic)]);
        }

        try
        {
            var result = await lease.Capability.CaptureAsync(
                new VersionMetadataCaptureRequest(
                    _resolution.ConfigSnapshot,
                    _resolution.FolderSnapshot,
                    new StableCaptureSourceView(stableSourcePath)),
                lease.Context).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(result.Snapshots);
            ArgumentNullException.ThrowIfNull(result.Diagnostics);
            if (result.Snapshots.Count > 8)
                throw new InvalidDataException("Version metadata provider exceeded the snapshot count quota.");

            var candidates = new List<VersionMetadataCandidate>();
            var identities = new Dictionary<(string SchemaId, int SchemaVersion), byte[]>();
            foreach (var snapshot in result.Snapshots)
            {
                ArgumentNullException.ThrowIfNull(snapshot);
                // 先用领域构造器验证 identity/payload quota，避免坏 metadata 延迟到权威提交时才阻断备份。
                var validated = VersionMetadataSnapshot.Create(
                    VersionId.New(),
                    _resolution.PluginId.Value.Value,
                    snapshot.SchemaId,
                    snapshot.SchemaVersion,
                    snapshot.Payload,
                    DateTimeOffset.UnixEpoch);
                var canonicalPayload = HistoryPackCodec.Canonicalize(snapshot.Payload);
                var key = (validated.SchemaId, validated.SchemaVersion);
                if (identities.TryGetValue(key, out var existing))
                {
                    if (!existing.AsSpan().SequenceEqual(canonicalPayload))
                        throw new InvalidDataException("Version metadata provider returned conflicting semantic identities.");
                    continue;
                }
                identities.Add(key, canonicalPayload);
                candidates.Add(new VersionMetadataCandidate(
                    _resolution.PluginId.Value.Value,
                    validated.SchemaId,
                    validated.SchemaVersion,
                    snapshot.Payload));
            }

            var normalizedDiagnostics = result.Diagnostics
                .Select(item => item with { Severity = DiagnosticSeverity.Warning })
                .ToArray();
            var diagnostics = normalizedDiagnostics.Select(ToHistoryDiagnostic).ToArray();
            _diagnostics.AddRange(normalizedDiagnostics);
            return new PluginV3VersionMetadataCaptureResult(candidates, diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var pluginDiagnostic = new PluginDiagnostic(
                "plugin.version_metadata_capture_failed",
                DiagnosticSeverity.Warning,
                "VersionMetadata",
                _resolution.PluginId.Value.Value,
                new Dictionary<string, string> { ["message"] = ex.Message });
            _diagnostics.Add(pluginDiagnostic);
            return new PluginV3VersionMetadataCaptureResult(
                [],
                [ToHistoryDiagnostic(pluginDiagnostic)]);
        }
    }

    public ValueTask CompleteCaptureAsync() => _captureLeaseOwner.CompleteAsync();

    public async ValueTask DisposeAsync() => await CompleteCaptureAsync().ConfigureAwait(false);

    private static HistoryDiagnostic ToHistoryDiagnostic(PluginDiagnostic diagnostic)
        => new(
            diagnostic.Code,
            HistoryDiagnosticSeverity.Warning,
            diagnostic.Arguments.GetValueOrDefault("message") ?? diagnostic.Code);

    private sealed class StableCaptureSourceView : IVersionMetadataSourceView
    {
        private static readonly ArtifactContentHandle Content = new("capture-source");
        private readonly HostArtifactReadService _read;

        public StableCaptureSourceView(string stableSourcePath)
            => _read = new HostArtifactReadService(
                new Dictionary<ArtifactContentHandle, string> { [Content] = stableSourcePath });

        public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
            => _read.OpenReadAsync(Content, relativePath, cancellationToken);
    }
}

internal sealed record PluginV3VersionMetadataCaptureResult(
    IReadOnlyList<VersionMetadataCandidate> Candidates,
    IReadOnlyList<HistoryDiagnostic> Diagnostics)
{
    public static PluginV3VersionMetadataCaptureResult Empty { get; } = new([], []);
}

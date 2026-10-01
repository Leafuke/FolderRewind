using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static partial class NativeHistoryApplicationService
{
    internal sealed record VersionRestorePreview(string ConfigSignature, SourceVersion Version, VersionAssessment Assessment);

    public static async Task<VersionRestorePreview> PreviewVersionRestoreAsync(BackupConfig config, ManagedFolder source,
        VersionId versionId, CancellationToken token = default)
    {
        var signature = NativeHistoryConfigLease.Signature(config);
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        var version = await runtime.Query.GetVersionAsync(versionId, token).ConfigureAwait(false);
        if (version is null || version.SourceId.Value != System.Guid.Parse(source.Id))
            throw new System.InvalidOperationException(I18n.GetString("Export_IdentityChanged"));
        var assessment = await PreviewExportAsync(config, versionId, token).ConfigureAwait(false);
        return new(signature, version, assessment);
    }

    public static async Task<VersionAssessment> PreviewExportAsync(BackupConfig config, VersionId versionId,
        CancellationToken token = default)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        var version = await runtime.Query.GetVersionAsync(versionId, token).ConfigureAwait(false)
            ?? throw new System.InvalidOperationException(I18n.GetString("Export_IdentityChanged"));
        var engine = CreateExportRuntime(config);
        return await engine.AssessVersionAsync(versionId,
            await runtime.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false),
            await BuildEnvironmentAsync(runtime, token).ConfigureAwait(false), AssessmentDepth.Fast,
            version.CaptureScope == CaptureScope.PartialSource ? MaterializationFidelity.Partial : MaterializationFidelity.Exact, token).ConfigureAwait(false);
    }

    public static async Task ExportVersionAsync(BackupConfig config, ManagedFolder source, VersionId versionId,
        string destination, CancellationToken token = default)
    {
        var signature = NativeHistoryConfigLease.Signature(config);
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterAsync(config.Id, token).ConfigureAwait(false);
        await using var guard = await NativeHistoryConfigLease.EnterInsideOperationAsync(config, signature, operation, token).ConfigureAwait(false);
        var version = await runtime.Query.GetVersionAsync(versionId, token).ConfigureAwait(false);
        if (version is null || version.SourceId.Value != System.Guid.Parse(source.Id))
            throw new System.InvalidOperationException(I18n.GetString("Export_IdentityChanged"));
        var roots = ConfigService.CurrentConfig.BackupConfigs.SelectMany(c => c.SourceFolders.Select(s => s.Path).Append(c.DestinationPath))
            .Where(p => !string.IsNullOrWhiteSpace(p)).Append(ConfigService.ConfigDirectory)
            .Append(runtime.Repository.Paths.RepositoryRoot).ToArray();
        await new HistoryVersionExportService(CreateExportRuntime(config)).ExportAsync(versionId,
            await runtime.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false),
            await BuildEnvironmentAsync(runtime, token).ConfigureAwait(false), destination, roots, token,
            version.CaptureScope == CaptureScope.PartialSource ? MaterializationFidelity.Partial : MaterializationFidelity.Exact).ConfigureAwait(false);
    }

    private static RepresentationRuntime CreateExportRuntime(BackupConfig config)
    {
        var archive = new SevenZipHistoryArchiveBackend(config);
        return new([new CoreArchiveRepresentationHandler(archive), new SmartDeltaRepresentationHandler(archive)]);
    }
}

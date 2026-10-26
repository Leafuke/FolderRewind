using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.Application;
using FolderRewind.Models;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static partial class BackupService
{
    private static SourceCaptureInput ReadCaptureInput(string root, BackupConfig config, ManagedFolder folder,
        SourceId source, EffectiveSourceBoundarySnapshot boundary, SourceCaptureBaseline? baseline, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var timing = HistoryOperationDiagnostics.Current?.Measure("source-inventory");
            var states = ScanDirectory(root, config.Filters, originalSourcePath: folder.Path,
                selection: folder.SourceScope, cancellationToken: token);
            var binding = new HistoryRestoreSourceBinding(source, root, boundary) { LogicalDirectory = folder.Path };
            var directories = SourceDirectorySnapshot.Read(root,
                FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(binding), states.Keys, token,
                enforceLimits: NativeArtifactHostService.IsIncrementalPolicy(config), skipLinks: true,
                includeDirectory: FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(binding, isDirectory: true));
            return new(root, states, directories, baseline, boundary.Fingerprint);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { throw new CaptureInputException(root, error, "scan"); }
    }

    private static async Task<SourceCaptureReadLease> PrepareCoreInputAsync(SourceCaptureInput input,
        IEnumerable<string> paths, BackupConfig config, CancellationToken token, bool full = false)
    {
        var view = await input.PrepareForModeAsync(paths, config.Archive.SourceReadMode, protect: false,
            CaptureTemporaryRoot, token, includeDirectories: full).ConfigureAwait(false);
        // Optional metadata guards do not make the entire live source a stable view.
        view.TryProtectMetadata(input.MetadataPaths);
        LogCaptureInput(view, view.IsCopy ? "copy" : "direct", config.Id);
        return view;
    }

    private static string CaptureTemporaryRoot => Path.Combine(Path.GetTempPath(), "FolderRewind", "capture-inputs");
    private static void LogCaptureInput(SourceCaptureReadLease input, string mode, string configId)
        => Log($"[CaptureInput] config={configId}; source={input.OriginalSourcePath}; mode={mode}; files={input.ReadPaths.Count}; copiedBytes={input.CopiedBytes}", LogLevel.Info);

    private static async Task<SourceCaptureResult> AddCaptureMetadataAsync(SourceCaptureResult result,
        SourceCaptureInput? input, PluginV3BackupSession session, HistoryRuntime history, CancellationToken token)
    {
        var paths = session.MetadataPaths;
        if (input is null || paths.Count == 0 || paths.Any(path => !input.Files.ContainsKey(path))) return result;
        if (input.View is { } view && view.CanReadMetadata(paths))
        {
            var metadata = await session.CaptureVersionMetadataAsync(token, sourceView: view).ConfigureAwait(false);
            return result.WithVersionMetadata(metadata.Candidates, metadata.Diagnostics);
        }
        if (input.Baseline is { } baseline && paths.All(path =>
            baseline.FileStates.TryGetValue(path, out var before) && before == input.Files[path])
            && result.RepresentationCandidate?.Kind is RepresentationKind.CoreSmartDelta or RepresentationKind.PluginArtifact or RepresentationKind.CoreRolling)
        {
            try
            {
                var metadata = await history.Query.GetVersionMetadataSnapshotsAsync(baseline.BaseVersionId, token).ConfigureAwait(false);
                return result.WithVersionMetadata(metadata.Select(m => new VersionMetadataCandidate(
                    m.ProducerPluginId, m.SchemaId, m.SchemaVersion, m.Payload)), []);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            { return result.WithVersionMetadata([], [new HistoryDiagnostic("plugin.version_metadata_capture_failed", HistoryDiagnosticSeverity.Warning, error.Message)]); }
        }
        return result;
    }
}

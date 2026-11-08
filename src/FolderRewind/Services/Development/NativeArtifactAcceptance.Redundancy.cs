#if NATIVE_ARTIFACT_ACCEPTANCE
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Merge;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FolderRewind.Services.Development;

internal static partial class NativeArtifactAcceptance
{
    // Runs only against the isolated request workspace, after the normal capture path created it.
    private static async Task<int> RunRedundancyDataAsync(BackupConfig config, string root)
    {
        var source = config.SourceFolders.Single().Path;
        if (!Path.GetFullPath(source).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Redundancy data fixture escaped its owned workspace.");
        var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config);
        var reports = new List<object>();
        var deleted = Path.Combine(source, "redundancy-delete.fixture");
        await File.WriteAllTextAsync(deleted, "real world deletion witness");
        await CaptureAndVerify("before-delete", checkpoint: false);
        string? changedRegion = null;
        int deletedSlot = -1;
        foreach (var region in Directory.EnumerateFiles(source, "r.*.*.mca", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using var file = new FileStream(region, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (file.Length < 8192) continue;
            var locations = new byte[4096]; file.ReadExactly(locations);
            var slot = Enumerable.Range(0, 1024).FirstOrDefault(i => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(locations.AsSpan(i * 4, 4)) != 0, -1);
            if (slot < 0) continue;
            file.Position = slot * 4; file.Write(new byte[4]);
            file.Position = 4096 + slot * 4; file.Write(new byte[4]);
            file.Flush(flushToDisk: true);
            changedRegion = Path.GetRelativePath(source, region); deletedSlot = slot;
            break;
        }
        if (changedRegion is null) throw new InvalidDataException("Real world has no populated region slot for deletion acceptance.");
        File.Delete(deleted);
        await CaptureAndVerify("file-and-chunk-delete", checkpoint: false);
        await CaptureAndVerify("checkpoint", checkpoint: true);
        await File.WriteAllTextAsync(Path.Combine(root, "redundancy-data-result.json"), JsonSerializer.Serialize(new
        { Success = true, ChangedRegion = changedRegion, DeletedSlot = deletedSlot, Checks = reports }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;

        async Task CaptureAndVerify(string name, bool checkpoint)
        {
            if (checkpoint) await BackupService.CreateSemanticCheckpointAsync(config, default);
            else if (!(await BackupService.BackupConfigAsync(config)).CreatedNewArchive) throw new IOException("Real world capture was not committed: " + name);
            var workspace = (await history.WorkspaceStore.LoadAsync()).Value!;
            var version = workspace.SourceBaselines.Single().BaseVersionId!.Value;
            var representations = await history.Query.GetAllRepresentationsAsync();
            var representation = representations.Single(r => r.VersionId == version);
            var restored = Path.Combine(root, "redundancy-restored-" + name);
            var engine = HistoryRepresentationRuntimeFactory.Create(config, history, new SevenZipHistoryArchiveBackend(config));
            await engine.MaterializeAsync(representation.RepresentationId, representations,
                await NativeHistoryApplicationService.BuildEnvironmentAsync(history, default), MaterializationFidelity.Exact, restored);
            await VerifyCapturedTreeAsync(history, representation, source, restored);
            var manifest = await MergeTreeManifest.ReadAsync(restored, _ => true, default);
            var manifestPath = Path.Combine(root, "redundancy-manifest-" + name + ".json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            reports.Add(new { Name = name, VersionId = version.ToString(), representation.Kind, manifest.Digest, Manifest = manifestPath, Files = manifest.Files.Count });
        }
    }
}
#endif

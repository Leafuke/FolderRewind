using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryMigrationBundleTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize() { _root = Path.Combine(Path.GetTempPath(), "FolderRewindMigrationBundles", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { Directory.Delete(_root, true); }
    private async Task<(HistoryRuntime Runtime, HistoryMigrationExportProject Project, string Payload, string Bundle)> FixtureAsync(bool embedded)
    {
        var id = new HistoryConfigId(Guid.NewGuid().ToString("N")); var source = SourceId.New();
        var runtime = new HistoryRuntime(new FileHistoryRepository(id, new(Path.Combine(_root, "history")))); await runtime.InitializeAsync();
        var backup = Path.Combine(_root, "backup"); Directory.CreateDirectory(backup); var payload = Path.Combine(backup, "original.7z");
        File.WriteAllBytes(payload, "original-archive-bytes"u8.ToArray());
        var version = new SourceVersion(VersionId.New(), id, source, [], DateTimeOffset.UtcNow, null, CaptureScope.FullSource, CaptureOutcome.Recovered,
            [], new("source", "source"), null, HistoryProvenance.Native("test"));
        var representation = new VersionRepresentation(RepresentationId.New(), version.VersionId, RepresentationKind.CoreFull, "7z", [],
            MaterializationFidelity.Exact, null, null, new Dictionary<string, string> { ["fileName"] = "original.7z" });
        var codec = new HistoryPackCodec(); await runtime.Repository.CommitAsync(new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            [codec.CreateObject(version), codec.CreateObject(representation)])); await runtime.EnsureIndexCurrentAsync();
        await runtime.WorkspaceStore.SaveAsync(new(id, 0, [new(source, version.VersionId, WorkspaceBaselineRelation.Exact)]), -1);
        await runtime.LocalReplicaCatalogStore.SaveAsync(new(id, 0, [new(representation.RepresentationId, LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(payload), DateTimeOffset.UtcNow)]), -1);
        var project = new HistoryMigrationExportProject(new(id, "project", [new(source, "source", "source")], runtime), backup, new Archive());
        var bundle = Path.Combine(_root, "migration.frhistory"); await new HistoryMigrationBundleService().ExportAsync([project], bundle, "test", null, embedded);
        return (runtime, project, payload, bundle);
    }
    [TestMethod] public async Task ExternalSnapshotKeepsBindingsAndContinuationAndImportsImmutableFacts()
    {
        var fixture = await FixtureAsync(false); await using var runtime = fixture.Runtime; var service = new HistoryMigrationBundleService();
        var preview = await service.PreviewAsync(fixture.Bundle); Assert.AreEqual(3, preview.History.FormatVersion); Assert.IsFalse(preview.IncludesPayloads);
        Assert.HasCount(1, preview.History.Projects.Single().Migration!.Replicas); Assert.HasCount(1, preview.History.Projects.Single().Migration!.Continuation);
        var result = await new HistoryRepositoryBatchTransferService().ImportAsync(fixture.Bundle, Path.Combine(_root, "fresh"), expectedFileSha256: preview.History.FileSha256);
        Assert.AreEqual(HistoryTransferCommitStatus.Committed, result.Projects.Single().CommitStatus);
        using var zip = ZipFile.OpenRead(fixture.Bundle); Assert.IsFalse(zip.Entries.Any(e => e.FullName.StartsWith("payloads/", StringComparison.Ordinal)));
    }
    [TestMethod] public async Task EmbeddedSnapshotRestoresAfterTheOriginalArchiveIsRemovedAndRecoversInterruptedInstall()
    {
        var fixture = await FixtureAsync(true); await using var runtime = fixture.Runtime; var service = new HistoryMigrationBundleService();
        var preview = await service.PreviewAsync(fixture.Bundle); File.Delete(fixture.Payload);
        var target = Path.Combine(_root, "relocated"); var config = Path.Combine(_root, "fresh");
        service.PayloadCheckpoint = stage => { if (stage == "payload-installed") throw new IOException("interrupted installation"); };
        await Assert.ThrowsExactlyAsync<IOException>(() => service.InstallPayloadsAsync(fixture.Bundle, preview, runtime.ConfigId, config, target));
        service.PayloadCheckpoint = null; await service.RecoverPayloadsAsync(config, runtime.ConfigId, target);
        Assert.AreEqual("original-archive-bytes", File.ReadAllText(Path.Combine(target, "original.7z")));
        await service.InstallPayloadsAsync(fixture.Bundle, preview, runtime.ConfigId, config, target);
        Assert.AreEqual("original-archive-bytes", File.ReadAllText(Path.Combine(target, "original.7z")));
    }
    [TestMethod] public async Task DamagedEmbeddedPayloadAndUnknownEntriesAreRejectedBeforeImport()
    {
        var fixture = await FixtureAsync(true); await using var runtime = fixture.Runtime;
        using (var zip = ZipFile.Open(fixture.Bundle, ZipArchiveMode.Update))
        { var payload = zip.Entries.Single(e => e.FullName.StartsWith("payloads/", StringComparison.Ordinal)); var name = payload.FullName; payload.Delete(); using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("damaged"); }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new HistoryMigrationBundleService().PreviewAsync(fixture.Bundle));
        var untouched = Path.Combine(_root, "untouched");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new HistoryRepositoryBatchTransferService().ImportAsync(fixture.Bundle, untouched));
        Assert.IsFalse(Directory.Exists(untouched));
    }
    [TestMethod] public async Task ConfigurationSnapshotContainsOnlyTheSelectedProjectAndIsHashBound()
    {
        var fixture = await FixtureAsync(false); await using var runtime = fixture.Runtime;
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Legacy196", "config.json")))!.AsObject();
        var project = configuration["BackupConfigs"]!.AsArray()[0]!.DeepClone(); project["Id"] = runtime.ConfigId.Value;
        var folder = project["SourceFolders"]!.AsArray()[0]!.DeepClone(); folder["Id"] = fixture.Project.History.Sources.Single().SourceId.Value.ToString();
        project["SourceFolders"] = new JsonArray(folder);
        configuration["BackupConfigs"] = new JsonArray(project);
        var bytes = System.Text.Encoding.UTF8.GetBytes(configuration.ToJsonString());
        var service = new HistoryMigrationBundleService(); await service.ExportAsync([fixture.Project], fixture.Bundle, "test", bytes, false);
        CollectionAssert.AreEqual(bytes, (await service.PreviewAsync(fixture.Bundle)).ConfigurationUtf8!);
        using (var zip = ZipFile.Open(fixture.Bundle, ZipArchiveMode.Update))
        { zip.GetEntry("configuration.json")!.Delete(); using var writer = new StreamWriter(zip.CreateEntry("configuration.json").Open()); writer.Write("{}"); }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.PreviewAsync(fixture.Bundle));
    }
    [TestMethod] public async Task CompleteExportRejectsMissingPayloadAndConflictingDestinationBytes()
    {
        var fixture = await FixtureAsync(true); await using var runtime = fixture.Runtime; var service = new HistoryMigrationBundleService();
        var preview = await service.PreviewAsync(fixture.Bundle); var target = Path.Combine(_root, "conflict"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "original.7z"), "existing-user-bytes");
        await Assert.ThrowsExactlyAsync<IOException>(() => service.InstallPayloadsAsync(fixture.Bundle, preview, runtime.ConfigId, Path.Combine(_root, "fresh"), target));
        Assert.AreEqual("existing-user-bytes", File.ReadAllText(Path.Combine(target, "original.7z")));
        File.Delete(fixture.Payload);
        await Assert.ThrowsExactlyAsync<IOException>(() => service.ExportAsync([fixture.Project], Path.Combine(_root, "incomplete.frhistory"), "test", null, true));
        Assert.IsFalse(File.Exists(Path.Combine(_root, "incomplete.frhistory")));
    }
    [TestMethod] public async Task OversizedMetadataIsRejectedBeforeCreatingARepository()
    {
        var path = Path.Combine(_root, "oversized.frhistory");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var stream = zip.CreateEntry("manifest.json", CompressionLevel.Fastest).Open())
        { var buffer = new byte[1024 * 1024]; for (var i = 0; i < 65; i++) stream.Write(buffer); }
        var destination = Path.Combine(_root, "untouched");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new HistoryRepositoryBatchTransferService().ImportAsync(path, destination));
        Assert.IsFalse(Directory.Exists(destination));
    }
    [TestMethod] public async Task CanceledExportPreservesPreviousFileAndNeverPublishesAPartialSnapshot()
    {
        var fixture = await FixtureAsync(false); await using var runtime = fixture.Runtime; var before = File.ReadAllBytes(fixture.Bundle);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new HistoryMigrationBundleService().ExportAsync([fixture.Project], fixture.Bundle, "test", null, true, token: cancel.Token));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.Bundle));
    }
    private sealed class Archive : IArchiveRepresentationBackend
    {
        public ValueTask<PayloadVerificationResult> VerifyAsync(VersionRepresentation representation, string path, CancellationToken token) => ValueTask.FromResult(new PayloadVerificationResult(true, "test", ""));
        public ValueTask MaterializeAsync(IReadOnlyList<ArchiveMaterializationInput> inputs, string staging, CancellationToken token) => ValueTask.CompletedTask;
    }
}

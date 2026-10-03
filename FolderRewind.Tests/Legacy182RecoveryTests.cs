using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Legacy;
using FolderRewind.History.LocalState;
using FolderRewind.History.Migration;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using FolderRewind.History.Retention;
using FolderRewind.Services;
using System.Diagnostics;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class Legacy182RecoveryTests
{
    private string _root = null!;
    private readonly SourceId _source = SourceId.New();
    private readonly HistoryConfigId _config = new(Guid.NewGuid().ToString("N"));
    [TestInitialize] public void Initialize() { _root = Path.Combine(Path.GetTempPath(), "FolderRewindLegacy182", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private string BackupDirectory => Path.Combine(_root, "backups", "Save");
    private LegacyMigrationSourceSnapshot Source => new(_source, Path.Combine(_root, "source"), "Save", BackupDirectory);
    private LegacyHistoryEntrySnapshot Entry(string file, string type = "Smart") => new(_source, Source.OriginalPath, "Save", file,
        new DateTime(2026, 1, 1), type, "comment", true, false, true, "remote/old");
    private LegacySmartRecordSnapshot[] Records =>
    [
        new(_source, "full.7z", "", "full.7z", "Full", [], [], [], ["a.txt", "gone.txt", "switch"]),
        new(_source, "delta.7z", "full.7z", "full.7z", "Smart", ["switch/new.txt"], ["a.txt"], ["gone.txt", "switch"], ["a.txt", "switch/new.txt"])
    ];
    private LegacyHistoryMigrationInput Input(IEnumerable<LegacySmartRecordSnapshot>? records = null) => new(_root, _config, [Source], [Entry("delta.7z")], records ?? Records);

    [TestMethod]
    [DataRow("7z")]
    [DataRow("zip")]
    public async Task Tagged182WriterFixturesRestoreEveryExpectedByteAndAbsence(string format)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Legacy182", format);
        Assert.IsTrue(File.Exists(Path.Combine(directory, "expected.json")), "Released-source fixtures must be present; do not skip this gate.");
        var records = LegacySmartMetadataReader.Read([(_source, directory)]);
        var source = Source with { ArchiveDirectory = directory };
        var input = new LegacyHistoryMigrationInput(_root, _config, [source], [Entry("smart2." + format)], records);
        var build = new LegacyHistoryMigrationBuilder().Build(input);
        var graph = Objects<VersionRepresentation>(build);
        var root = graph.Single(r => r.RepresentationSpecificMetadata.ContainsKey(LegacySmartPlan.OwnersKey));
        var environment = new RepresentationEnvironment(build.LocalReplicaCatalog.Entries, [], []);
        var engine = new RepresentationRuntime([new CoreArchiveRepresentationHandler(Backend())]);
        var target = Path.Combine(_root, "released-result");
        await engine.MaterializeAsync(root.RepresentationId, graph, environment, MaterializationFidelity.Partial, target);
        using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "expected.json")));
        var expectedFiles = expected.RootElement.GetProperty("files").EnumerateObject().ToArray();
        Assert.HasCount(expectedFiles.Length, Directory.GetFiles(target, "*", SearchOption.AllDirectories));
        foreach (var file in expectedFiles)
            Assert.AreEqual(file.Value.GetString(), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(Path.Combine(target, file.Name)))).ToLowerInvariant(), file.Name);
        foreach (var absent in expected.RootElement.GetProperty("absent").EnumerateArray())
            Assert.IsFalse(File.Exists(Path.Combine(target, absent.GetString()!)));
    }

    [TestMethod]
    public void MissingDeletionFieldAndCyclicOrConflictingMetadataCannotFabricateACompleteArchive()
    {
        foreach (var records in new[] {
            new[] { Records[0], Records[1] with { DeletedFiles = null } },
            new[] { Records[0], Records[1] with { PreviousBackupFileName = "delta.7z" } },
            new[] { Records[0], Records[1], Records[1] with { FullFileList = [] } },
            new[] { Records[0], Records[1] with { DeletedFiles = ["../escape"] } } })
        {
            Assert.Throws<InvalidDataException>(() => LegacySmartPlan.Build(_source, "delta.7z", records));
            var build = new LegacyHistoryMigrationBuilder(fileExists: _ => true).Build(Input(records));
            Assert.HasCount(1, Objects<SourceVersion>(build));
            Assert.IsEmpty(Objects<VersionRepresentation>(build));
        }
    }

    [TestMethod]
    public void ExplicitEmptyTargetIsValidAndCloudDeclarationsNeverBecomeReplicas()
    {
        var records = new[] { Records[0], Records[1] with { AddedFiles = [], ModifiedFiles = [], DeletedFiles = Records[0].FullFileList, FullFileList = [] } };
        Assert.IsEmpty(LegacySmartPlan.Build(_source, "delta.7z", records).Owners);
        var build = new LegacyHistoryMigrationBuilder(fileExists: _ => true).Build(Input(records));
        Assert.IsEmpty(Objects<StorageReplica>(build));
        Assert.IsTrue(Objects<SourceVersion>(build).All(v => v.BoundaryConfidence == HistoricalBoundaryConfidence.Unknown));
        Assert.IsTrue(Objects<VersionRepresentation>(build).All(r => r.Fidelity != MaterializationFidelity.Exact));
        var exactCopies = Objects<VersionRepresentation>(build).Select(r => new VersionRepresentation(r.RepresentationId,
            r.VersionId, r.Kind, r.Format, r.DependencyRepresentationIds, MaterializationFidelity.Exact, null, null, r.RepresentationSpecificMetadata));
        var admission = HistoryExactCheckpointAdmission.Evaluate(Objects<SourceCheckpoint>(build).Single(),
            Objects<SourceVersion>(build).ToDictionary(v => v.VersionId), exactCopies.ToDictionary(r => r.RepresentationId));
        Assert.AreEqual(HistoryExactCheckpointAdmissionStatus.HistoricalBoundaryUnknown, admission.Status);
    }

    [TestMethod]
    public void CurrentSourceRenameDoesNotRewriteAcceptedSupportFacts()
    {
        var builder = new LegacyHistoryMigrationBuilder(fileExists: _ => true);
        var before = builder.Build(Input());
        var after = builder.Build(new(_root, _config, [Source with { DisplayName = "Renamed", OriginalPath = Path.Combine(_root, "moved") }],
            Input().Entries, Records));
        CollectionAssert.AreEquivalent(before.Packs.SelectMany(p => p.Objects).Select(o => o.PayloadHash).ToArray(),
            after.Packs.SelectMany(p => p.Objects).Select(o => o.PayloadHash).ToArray());
    }

    [TestMethod]
    public void LargeIndexTakeoverDoesNotOpenOrCreateArchiveTrees()
    {
        var entries = Enumerable.Range(0, 1000).Select(i => Entry($"full-{i}.7z", "Full")).ToArray();
        int probes = 0;
        var builder = new LegacyHistoryMigrationBuilder(fileExists: _ => { probes++; return false; });
        var build = builder.Build(new(_root, _config, [Source], entries));
        Assert.HasCount(1000, Objects<SourceVersion>(build));
        Assert.IsEmpty(build.LocalReplicaCatalog.Entries);
        Assert.AreEqual(1000, probes);
        Assert.IsFalse(Directory.Exists(BackupDirectory));
    }

    [TestMethod]
    public void LongReleasedSmartChainIsValidatedWithoutRecursiveStackGrowth()
    {
        var records = new List<LegacySmartRecordSnapshot> { new(_source, "0.7z", "", "0.7z", "Full", [], [], [], []) };
        for (var index = 1; index <= 1000; index++)
            records.Add(new(_source, $"{index}.7z", $"{index - 1}.7z", "0.7z", "Smart", [], [], [], []));
        var plan = LegacySmartPlan.Build(_source, "1000.7z", records);
        Assert.HasCount(1001, plan.Chain);
        Assert.IsEmpty(plan.Owners);
    }

    [TestMethod]
    public async Task RealSmartRestoreRemovesDeletedFilesAndSupportsFileToDirectoryWithoutTouchingUnmanagedFiles()
    {
        Directory.CreateDirectory(BackupDirectory);
        await ArchiveAsync("full.7z", new() { ["a.txt"] = "old", ["gone.txt"] = "deleted", ["switch"] = "old file" });
        await ArchiveAsync("delta.7z", new() { ["a.txt"] = "new", ["switch/new.txt"] = "child" });
        var build = new LegacyHistoryMigrationBuilder().Build(Input());
        var graph = Objects<VersionRepresentation>(build);
        var targetVersion = Objects<SourceVersion>(build).Single(v => v.Provenance.Origin == HistoryOrigin.LegacyMigration);
        var environment = new RepresentationEnvironment(build.LocalReplicaCatalog.Entries, [], []);
        var backend = Backend();
        var engine = new RepresentationRuntime([new CoreArchiveRepresentationHandler(backend)]);
        var selected = graph.Single(r => r.VersionId == targetVersion.VersionId);
        var export = Path.Combine(_root, "export");
        await engine.MaterializeAsync(selected.RepresentationId, graph, environment, MaterializationFidelity.Partial, export);
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(export, "a.txt")));
        Assert.AreEqual("child", File.ReadAllText(Path.Combine(export, "switch", "new.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(export, "gone.txt")));
        Assert.HasCount(2, Directory.GetFiles(export, "*", SearchOption.AllDirectories));
        var migration = await new LegacyHistoryMigrationService().MigrateAsync(Input(), (_, _) => Task.CompletedTask);
        await using var runtime = new HistoryRuntime(migration.Repository!);
        await runtime.InitializeAsync();
        Directory.CreateDirectory(Source.OriginalPath);
        File.WriteAllText(Path.Combine(Source.OriginalPath, "unmanaged.txt"), "keep");
        var restore = new HistoryRestoreService(runtime, engine, _ => Task.FromResult<IRepresentationEnvironment>(environment), new FileSystemHistoryRestoreMutationBackend());
        var workspace = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        var binding = new HistoryRestoreSourceBinding(_source, Source.OriginalPath);
        var blocked = await restore.RestoreVersionAsync(targetVersion.VersionId, binding, workspace, HistoryRestoreApplyMode.Clean);
        StringAssert.Contains(blocked.Diagnostic, "boundary");
        await restore.RestoreVersionAsync(targetVersion.VersionId, binding, workspace, HistoryRestoreApplyMode.Overwrite);
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(Source.OriginalPath, "unmanaged.txt")));
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(Source.OriginalPath, "a.txt")));
        Assert.AreNotEqual(WorkspaceBaselineRelation.Exact, (await runtime.WorkspaceStore.LoadAsync()).Value!.SourceBaselines.Single().Relation);
        File.WriteAllText(Path.Combine(Source.OriginalPath, "switch-file"), "protected");
        Directory.CreateDirectory(Path.Combine(export, "switch-file"));
        Assert.Throws<IOException>(() => LegacyRecoveryPolicy.ValidateOverlay(targetVersion, export, Source.OriginalPath));
        File.Move(Path.Combine(BackupDirectory, "full.7z"), Path.Combine(BackupDirectory, "full.offline"));
        var unavailable = await engine.AssessVersionAsync(targetVersion.VersionId, graph, environment,
            AssessmentDepth.Deep, MaterializationFidelity.Partial);
        Assert.AreNotEqual(HistoryReadiness.Ready, unavailable.Readiness);
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(Source.OriginalPath, "a.txt")));
        var checkpoint = (await runtime.Query.GetAllCheckpointsAsync()).Single();
        Assert.IsFalse((await new HistoryExactCheckpointAdmission(runtime).EvaluateAsync(checkpoint)).IsReady);
    }

    [TestMethod]
    public async Task OfflineArchiveReattachesToOriginalVersionAndOrphanDoesNotBlockNewHistory()
    {
        Directory.CreateDirectory(BackupDirectory);
        var record = new LegacyHistoryRecord { ConfigId = _config.Value, FolderPath = Source.OriginalPath, FolderName = "Save", FileName = "full.7z", BackupType = "Full", Timestamp = new DateTime(2026, 1, 1) };
        File.WriteAllText(Path.Combine(_root, "history.json"), JsonSerializer.Serialize(new[] { record, record with { FolderPath = "C:\\removed", FileName = "removed.7z" } }));
        var takeover = new LegacyTakeoverService(_root, _config);
        var prepared = takeover.Prepare([Source], Path.Combine(_root, "backups"));
        Assert.HasCount(1, prepared.Report.Items.Where(i => i.Status == "Unassigned"));
        var migration = await new LegacyHistoryMigrationService().MigrateAsync(prepared.Input, (_, _) => Task.CompletedTask);
        await using var runtime = new HistoryRuntime(migration.Repository!);
        await runtime.InitializeAsync();
        var before = (await runtime.Query.GetAllVersionsAsync()).Single().VersionId;
        await ArchiveAsync("full.7z", new() { ["a.txt"] = "returning disk" });
        await takeover.ResumeAsync(runtime, [Source], Path.Combine(_root, "backups"));
        var count = (await runtime.Repository.ReadAllPacksAsync()).Count;
        await takeover.ResumeAsync(runtime, [Source], Path.Combine(_root, "backups"));
        Assert.HasCount(count, await runtime.Repository.ReadAllPacksAsync());
        Assert.AreEqual(before, (await runtime.Query.GetAllVersionsAsync()).Single().VersionId);
        Assert.HasCount(1, (await runtime.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries);
        var environment = new RepresentationEnvironment((await runtime.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries, [], []);
        var planner = new HistoryRetentionPlanner(runtime, new([new CoreArchiveRepresentationHandler(Backend())]),
            _ => Task.FromResult<IRepresentationEnvironment>(environment), new FileSystemHistoryLocalPayloadStore());
        var retention = await planner.PlanAsync(new(0, HistoryRetentionOperationRoots.Empty, allowPostMigrationCleanup: true));
        Assert.IsEmpty(retention.LocalPayloadDeletions, "Post-migration cleanup must never collect borrowed old archives.");
        File.WriteAllText(takeover.ReportPath, "broken");
        File.Delete(Path.Combine(_root, "history.json"));
        var recovered = takeover.Prepare([Source], Path.Combine(_root, "backups"));
        Assert.HasCount(2, recovered.Report.Items);
        Assert.AreEqual("Missing", recovered.Report.InputStatus);
    }

    [TestMethod]
    public void MetadataReaderUsesReleasedRecordPrecedenceAndPreservesAbsentVersusEmpty()
    {
        var metadata = Path.Combine(BackupDirectory, "_metadata");
        Directory.CreateDirectory(Path.Combine(metadata, "records"));
        File.WriteAllText(Path.Combine(metadata, "metadata.json"), "{\"backupRecords\":[{\"archiveFileName\":\"x.7z\",\"backupType\":\"Full\",\"fullFileList\":[\"stale\"]}]}");
        File.WriteAllText(Path.Combine(metadata, "records", "x.json"), "{\"archiveFileName\":\"x.7z\",\"backupType\":\"Full\",\"fullFileList\":[]}");
        var record = LegacySmartMetadataReader.Read([(_source, BackupDirectory)]).Single();
        Assert.IsNotNull(record.FullFileList); Assert.IsEmpty(record.FullFileList); Assert.IsNull(record.DeletedFiles);
        File.WriteAllText(Path.Combine(metadata, "records", "duplicate.json"), "{\"archiveFileName\":\"x.7z\",\"backupType\":\"Smart\"}");
        Assert.IsFalse(string.IsNullOrEmpty(LegacySmartMetadataReader.Read([(_source, BackupDirectory)]).Single().Diagnostic));
    }

    [TestMethod]
    public void HistoricalDirectoryAndAmbiguousLocationsAreNotGuessed()
    {
        var destination = Path.Combine(_root, "backups");
        var oldDirectory = Path.Combine(destination, "Old_Name");
        Directory.CreateDirectory(oldDirectory); Directory.CreateDirectory(BackupDirectory);
        var record = new LegacyHistoryRecord { FolderName = "Old:Name", FileName = "old.7z", FolderPath = Source.OriginalPath };
        File.WriteAllText(Path.Combine(oldDirectory, record.FileName), "old");
        Assert.AreEqual(Path.Combine(oldDirectory, record.FileName), LegacyArchiveLocator.Locate(destination, record, Source).Selected);
        File.WriteAllText(Path.Combine(BackupDirectory, record.FileName), "another");
        Assert.IsNull(LegacyArchiveLocator.Locate(destination, record, Source).Selected);
        Assert.IsNull(LegacyArchiveLocator.ResolveSource(record, [Source, Source with { SourceId = SourceId.New() }]));
    }

    [TestMethod]
    public async Task HistoryAppearingAfterRepositoryCreationIsAppendedWithoutChangingWorkspace()
    {
        var takeover = new LegacyTakeoverService(_root, _config);
        Assert.AreEqual("Missing", takeover.Prepare([Source], Path.Combine(_root, "backups")).Report.InputStatus);
        var paths = HistoryRepositoryPaths.ForConfigDirectory(_root, _config);
        var repository = new FileHistoryRepository(_config, paths);
        await using var runtime = new HistoryRuntime(repository);
        await runtime.InitializeAsync();
        await runtime.WorkspaceStore.SaveAsync(new(_config, 0, [new WorkspaceSourceBaseline(_source, null, WorkspaceBaselineRelation.Unknown)]), -1);
        var workspaceBefore = File.ReadAllBytes(Path.Combine(paths.LocalStateRoot, "workspace.json"));
        File.WriteAllText(Path.Combine(_root, "history.json"), "[]");
        Assert.AreEqual("Empty", takeover.Prepare([Source], Path.Combine(_root, "backups")).Report.InputStatus);
        var record = new LegacyHistoryRecord { ConfigId = _config.Value, FolderPath = Source.OriginalPath, FolderName = "Save", FileName = "full.7z", BackupType = "Full", Timestamp = new DateTime(2026, 1, 1) };
        File.WriteAllText(Path.Combine(_root, "history.json"), JsonSerializer.Serialize(new[] { record }));
        await takeover.ResumeAsync(runtime, [Source], Path.Combine(_root, "backups"));
        Assert.HasCount(1, await runtime.Query.GetAllVersionsAsync());
        CollectionAssert.AreEqual(workspaceBefore, File.ReadAllBytes(Path.Combine(paths.LocalStateRoot, "workspace.json")));
    }

    [TestMethod]
    public async Task FreshRepositoryWithNoHistoryBootstrapsOnlyUnknownLocalState()
    {
        var paths = HistoryRepositoryPaths.ForConfigDirectory(_root, _config);
        await using var runtime = new HistoryRuntime(new FileHistoryRepository(_config, paths));
        await runtime.InitializeAsync();
        var takeover = new LegacyTakeoverService(_root, _config);
        var report = await takeover.ResumeAsync(runtime, [Source], Path.Combine(_root, "backups"));
        Assert.AreEqual("Missing", report.InputStatus);
        Assert.IsEmpty(await runtime.Repository.ReadAllPacksAsync());
        Assert.IsEmpty((await runtime.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries);
        Assert.AreEqual(WorkspaceBaselineRelation.Unknown, (await runtime.WorkspaceStore.LoadAsync()).Value!.SourceBaselines.Single().Relation);
        await takeover.ResumeAsync(runtime, [Source], Path.Combine(_root, "backups"));
        Assert.IsEmpty(await runtime.Repository.ReadAllPacksAsync());
    }

    [TestMethod]
    public async Task ArchiveScanCannotBypassSmartDependencyAdmission()
    {
        var path = Path.Combine(_root, "[Smart][2026-01-01_12-00-00]Save.7z");
        File.WriteAllText(path, "not even necessary to read this payload");
        await using var runtime = new HistoryRuntime(new FileHistoryRepository(_config, new(Path.Combine(_root, "repository"))));
        await runtime.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new HistoryArchiveRecoveryService(runtime)
            .RecoverAsync(_source, new("Save", Source.OriginalPath), path, false));
        Assert.IsEmpty(await runtime.Query.GetAllVersionsAsync());
    }

    [TestMethod]
    public async Task UnreleasedLegacyRepresentationCannotUseTheOldIncompleteSmartMaterializer()
    {
        var representation = new VersionRepresentation(RepresentationId.New(), VersionId.New(), RepresentationKind.CoreSmartDelta,
            "7z", [], MaterializationFidelity.Exact, null, null, new Dictionary<string, string> { ["legacyFileName"] = "old-smart.7z" });
        var verification = await Backend().VerifyAsync(representation, Path.Combine(_root, "old-smart.7z"), default);
        Assert.IsFalse(verification.Success);
        StringAssert.Contains(verification.Diagnostic, "development-era");
    }

    [TestMethod]
    public void SameNamedMutableGenerationsRemainLogicalRecordsWithoutInventedContent()
    {
        var first = Entry("rolling.7z", "Rolling");
        var second = first with { Timestamp = first.Timestamp.AddDays(1) };
        var input = new LegacyHistoryMigrationInput(_root, _config, [Source], [first, second],
            [new(_source, "rolling.7z", "", "rolling.7z", "Rolling", [], [], [], ["a.txt"])]);
        var build = new LegacyHistoryMigrationBuilder(fileExists: _ => true).Build(input);
        Assert.HasCount(2, Objects<SourceVersion>(build));
        Assert.IsEmpty(Objects<VersionRepresentation>(build));
    }

    [TestMethod]
    [DataRow("Prepared")]
    [DataRow("PackCommitted")]
    [DataRow("CatalogApplied")]
    [DataRow("ReportSaved")]
    public async Task CompletionFailureAndTwoReopensPreserveIdentityAndRecoverCatalog(string stage)
    {
        Directory.CreateDirectory(BackupDirectory);
        var record = new LegacyHistoryRecord { ConfigId = _config.Value, FolderPath = Source.OriginalPath, FolderName = "Save", FileName = "delta.7z", BackupType = "Smart", Timestamp = new DateTime(2026, 1, 1) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { record });
        File.WriteAllBytes(Path.Combine(_root, "history.json"), bytes);
        var takeover = new LegacyTakeoverService(_root, _config);
        var prepared = takeover.Prepare([Source], Path.Combine(_root, "backups"));
        var migration = await new LegacyHistoryMigrationService().MigrateAsync(prepared.Input, (_, _) => Task.CompletedTask);
        var expectedId = LegacyHistoryMigrationIdentityV1.Version(LegacyHistoryMigrationBuilder.OriginKey(_config, prepared.Input.Entries.Single()));
        await using (var runtime = new HistoryRuntime(migration.Repository!))
        {
            await runtime.InitializeAsync();
            await ArchiveAsync("full.7z", new() { ["a.txt"] = "old", ["gone.txt"] = "old", ["switch"] = "file" });
            await ArchiveAsync("delta.7z", new() { ["a.txt"] = "new", ["switch/new.txt"] = "child" });
            var directory = Path.Combine(BackupDirectory, "_metadata", "records"); Directory.CreateDirectory(directory);
            foreach (var metadata in Records) File.WriteAllText(Path.Combine(directory, metadata.ArchiveFileName + ".json"), JsonSerializer.Serialize(metadata));
            var failing = new LegacyTakeoverService(_root, _config) { StageObserver = point => { if (point == stage) throw new IOException("Injected completion interruption"); } };
            await Assert.ThrowsAsync<IOException>(() => failing.ResumeAsync(runtime, [Source], Path.Combine(_root, "backups")));
            if (stage is "PackCommitted" or "CatalogApplied")
                await Assert.ThrowsAsync<InvalidOperationException>(async () => { await using var gate = await runtime.MutationGate.EnterAsync(); });
        }
        int? packCount = null;
        for (var i = 0; i < 2; i++)
        {
            var paths = HistoryRepositoryPaths.ForConfigDirectory(_root, _config);
            await using var runtime = new HistoryRuntime(new FileHistoryRepository(_config, paths));
            await runtime.InitializeAsync();
            await takeover.ResumeAsync(runtime, [Source], Path.Combine(_root, "backups"));
            Assert.HasCount(1, (await runtime.Query.GetAllVersionsAsync()).Where(v => v.VersionId == expectedId));
            Assert.HasCount(2, (await runtime.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries);
            var packs = await runtime.Repository.ReadAllPacksAsync();
            if (packCount is { } previousCount) Assert.HasCount(previousCount, packs);
            packCount = packs.Count;
        }
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(_root, "history.json")));
    }

    private static T[] Objects<T>(LegacyHistoryMigrationBuild build)
    {
        var codec = new HistoryPackCodec();
        return build.Packs.SelectMany(p => p.Objects).Select(codec.DeserializeKnown).OfType<T>().ToArray();
    }
    private SevenZipArchiveProcessBackend Backend() => new(() => SevenZipExecutableLocator.Resolve(Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_7Z")), () => null, false, ".marker");
    private async Task ArchiveAsync(string name, Dictionary<string, string> files)
    {
        var executable = SevenZipExecutableLocator.Resolve(Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_7Z"));
        Assert.IsNotNull(executable, "Real 7-Zip is mandatory for migration tests.");
        var source = Path.Combine(_root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(source);
        foreach (var (path, text) in files) { var full = Path.Combine(source, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, text); }
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = source };
        foreach (var argument in new[] { "a", Path.Combine(BackupDirectory, name), "*", "-y" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, await output + await error);
    }
}

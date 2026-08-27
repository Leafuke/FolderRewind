using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using System.Globalization;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryRepositoryTests
{
    private string _root = null!;
    private HistoryConfigId _configId;
    private HistoryPackCodec _codec = null!;
    private FileHistoryRepository _repository = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindHistoryRepositoryTests", Guid.NewGuid().ToString("N"));
        _configId = new HistoryConfigId(Guid.NewGuid().ToString("D"));
        _codec = new HistoryPackCodec();
        _repository = new FileHistoryRepository(_configId, new HistoryRepositoryPaths(_root), _codec);
        await _repository.InitializeAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _repository.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Commit_InstallsReadablePackAndDuplicateIsIdempotent()
    {
        var version = CreateVersion();
        var pack = CreatePack(_codec.CreateObject(version));

        var first = await _repository.CommitAsync(pack);
        var second = await _repository.ImportAsync([_codec.Encode(pack)]);
        var stored = await _repository.ReadAllPacksAsync();

        Assert.AreEqual(HistoryPackInstallDisposition.Installed, first.Disposition);
        Assert.AreEqual(HistoryPackInstallDisposition.Duplicate, second.Single().Disposition);
        Assert.HasCount(1, stored);
        Assert.AreEqual(version.VersionId.ToString(), stored[0].Pack.Objects.Single().Id);
    }

    [TestMethod]
    public async Task Import_RejectsSamePackIdWithDifferentBytesAndQuarantinesWholePack()
    {
        var packId = PackId.New();
        var first = CreatePack(_codec.CreateObject(CreateVersion()), packId);
        await _repository.CommitAsync(first);
        var conflicting = new HistoryCommitPack(
            packId,
            HistoryTransactionId.New(),
            DateTimeOffset.UtcNow,
            [_codec.CreateObject(CreateVersion())]);

        await Assert.ThrowsExactlyAsync<HistoryIntegrityConflictException>(
            () => _repository.ImportAsync([_codec.Encode(conflicting)]));

        Assert.HasCount(1, Directory.GetFiles(_repository.Paths.QuarantineRoot, "*.frpack"));
        Assert.HasCount(1, await _repository.ReadAllPacksAsync());
    }

    [TestMethod]
    public async Task Import_RejectsSameObjectIdWithDifferentFacts()
    {
        var id = VersionId.New();
        var firstVersion = CreateVersion(id, "first");
        var secondVersion = CreateVersion(id, "second");
        await _repository.CommitAsync(CreatePack(_codec.CreateObject(firstVersion)));

        await Assert.ThrowsExactlyAsync<HistoryIntegrityConflictException>(
            () => _repository.CommitAsync(CreatePack(_codec.CreateObject(secondVersion))));

        Assert.HasCount(1, Directory.GetFiles(_repository.Paths.QuarantineRoot, "*.frpack"));
    }

    [TestMethod]
    public void CanonicalPayload_IsIndependentOfCultureAndMapInsertionOrder()
    {
        var version = CreateVersion();
        var representationId = RepresentationId.New();
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            var first = new VersionRepresentation(
                representationId, version.VersionId, RepresentationKind.CoreFull, "7Z", [],
                RestoreStrategy.Exact, null, null,
                new Dictionary<string, string> { ["z"] = "last", ["i"] = "first" });
            var firstObject = _codec.CreateObject(first);

            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            var second = new VersionRepresentation(
                representationId, version.VersionId, RepresentationKind.CoreFull, "7Z", [],
                RestoreStrategy.Exact, null, null,
                new Dictionary<string, string> { ["i"] = "first", ["z"] = "last" });
            var secondObject = _codec.CreateObject(second);

            Assert.AreEqual(firstObject.PayloadHash, secondObject.PayloadHash);
            CollectionAssert.AreEqual(firstObject.CanonicalPayload, secondObject.CanonicalPayload);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [TestMethod]
    public async Task Import_PreservesUnknownObjectSchemaAndMarksCompatibilityBlocked()
    {
        using var payload = JsonDocument.Parse("{\"future\":true,\"value\":1}");
        var unknown = _codec.CreateUnknownObject(
            HistoryObjectKinds.SourceVersion,
            schemaVersion: 99,
            id: Guid.NewGuid().ToString("N"),
            payload.RootElement);
        var pack = CreatePack(unknown);
        var bytes = _codec.Encode(pack);

        var result = await _repository.ImportAsync([bytes]);
        var stored = await _repository.ReadAllPacksAsync();

        Assert.IsTrue(result.Single().CompatibilityBlocked);
        CollectionAssert.AreEqual(bytes, stored.Single().OriginalBytes);
        Assert.IsEmpty(Directory.GetFiles(_repository.Paths.QuarantineRoot));
    }

    [TestMethod]
    public async Task BatchImport_ValidatesCrossPackReferencesAgainstCandidateUnion()
    {
        var version = CreateVersion();
        var representation = new VersionRepresentation(
            RepresentationId.New(), version.VersionId, RepresentationKind.CoreFull, "7z", [],
            RestoreStrategy.Exact, null, null, null);
        var representationPack = CreatePack(_codec.CreateObject(representation));
        var versionPack = CreatePack(_codec.CreateObject(version));

        var results = await _repository.ImportAsync(
            [_codec.Encode(representationPack), _codec.Encode(versionPack)]);

        Assert.HasCount(2, results);
        Assert.HasCount(2, await _repository.ReadAllPacksAsync());
    }

    [TestMethod]
    public async Task PreparedJournal_UsesFinalPackPresenceAsCommitDecision()
    {
        var rolledBack = 0;
        var applied = 0;
        var absentPack = CreatePack(_codec.CreateObject(CreateVersion()));
        var absentJournal = HistoryTransactionJournal.Prepared(absentPack.TransactionId, absentPack.PackId);
        _repository.Journals.Save(absentJournal);

        await _repository.Journals.RecoverAsync(
            (_, _) => { applied++; return Task.CompletedTask; },
            (_, _) => { rolledBack++; return Task.CompletedTask; });

        var committedPack = CreatePack(_codec.CreateObject(CreateVersion()));
        var committedJournal = HistoryTransactionJournal.Prepared(committedPack.TransactionId, committedPack.PackId);
        await _repository.CommitAsync(committedPack, committedJournal);
        await _repository.Journals.RecoverAsync(
            (_, _) => { applied++; return Task.CompletedTask; },
            (_, _) => { rolledBack++; return Task.CompletedTask; });

        Assert.AreEqual(1, rolledBack);
        Assert.AreEqual(1, applied);
        Assert.IsTrue(_repository.Journals.EnumerateJournalPaths()
            .Select(_repository.Journals.Load)
            .All(journal => journal.Phase == HistoryTransactionPhase.Complete));
    }

    [TestMethod]
    public void PathCodec_DoesNotExposeConfigOrObjectPathInjection()
    {
        var encoded = HistoryRepositoryPaths.EncodeConfigPathSegment(new HistoryConfigId(" ..\\remote:evil/ "));

        Assert.IsTrue(encoded.StartsWith("c-", StringComparison.Ordinal));
        Assert.IsFalse(encoded.Contains("..", StringComparison.Ordinal));
        Assert.IsTrue(HistoryRepositoryPaths.IsSafeRepositoryRelativePath(
            HistoryRepositoryPaths.CreateReplicaObjectKey(ReplicaId.New())));
        Assert.IsFalse(HistoryRepositoryPaths.IsSafeRepositoryRelativePath("../escape"));
        Assert.IsFalse(HistoryRepositoryPaths.IsSafeRepositoryRelativePath("remote:path"));
    }

    [TestMethod]
    public async Task BindingCoordinator_CreateReadBackAndPersistIsIdempotent()
    {
        var configDirectory = Path.Combine(_root, "binding-config");
        var coordinator = new HistoryRepositoryBindingCoordinator(configDirectory);
        var persistedVersion = 0;

        var first = await coordinator.EnsureAsync(
            _configId,
            bindingFormatVersion: null,
            (version, _) => { persistedVersion = version; return Task.CompletedTask; });
        first.Repository?.Dispose();
        var descriptorPath = HistoryRepositoryPaths.ForConfigDirectory(configDirectory, _configId).DescriptorPath;
        var descriptorBytes = await File.ReadAllBytesAsync(descriptorPath);
        var second = await coordinator.EnsureAsync(
            _configId,
            persistedVersion,
            (_, _) => throw new AssertFailedException("Existing binding must not be persisted again."));
        second.Repository?.Dispose();

        Assert.AreEqual(HistoryRepositoryBindingStatus.CreatedAndBound, first.Status);
        Assert.AreEqual(HistoryRepositoryBindingStatus.Bound, second.Status);
        CollectionAssert.AreEqual(descriptorBytes, await File.ReadAllBytesAsync(descriptorPath));
    }

    [TestMethod]
    public async Task BindingCoordinator_MissingBoundRepositoryRequiresRecoveryWithoutCreatingEmptyHistory()
    {
        var configDirectory = Path.Combine(_root, "missing-binding-config");
        var paths = HistoryRepositoryPaths.ForConfigDirectory(configDirectory, _configId);
        var coordinator = new HistoryRepositoryBindingCoordinator(configDirectory);

        var result = await coordinator.EnsureAsync(
            _configId,
            HistoryRepositoryDescriptor.CurrentFormatVersion,
            (_, _) => Task.CompletedTask);

        Assert.AreEqual(HistoryRepositoryBindingStatus.RecoveryRequired, result.Status);
        Assert.IsFalse(File.Exists(paths.DescriptorPath));
    }

    [TestMethod]
    public async Task BindingCoordinator_SaveFailureReusesCreateOnceRepositoryOnRetry()
    {
        var configDirectory = Path.Combine(_root, "binding-retry-config");
        var coordinator = new HistoryRepositoryBindingCoordinator(configDirectory);
        var first = await coordinator.EnsureAsync(
            _configId,
            null,
            (_, _) => throw new IOException("Injected config save failure."));
        var path = HistoryRepositoryPaths.ForConfigDirectory(configDirectory, _configId).DescriptorPath;
        var bytes = await File.ReadAllBytesAsync(path);

        var retry = await coordinator.EnsureAsync(
            _configId,
            null,
            (_, _) => Task.CompletedTask);
        retry.Repository?.Dispose();

        Assert.AreEqual(HistoryRepositoryBindingStatus.BindingPersistenceFailed, first.Status);
        Assert.AreEqual(HistoryRepositoryBindingStatus.Bound, retry.Status);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
    }

    private SourceVersion CreateVersion(VersionId? id = null, string displayName = "source")
        => new(
            id ?? VersionId.New(),
            _configId,
            SourceId.New(),
            [],
            DateTimeOffset.UtcNow,
            null,
            CaptureScope.FullSource,
            CaptureOutcome.Captured,
            [],
            new SourceDescriptorSnapshot(displayName, "C:\\source"),
            null,
            HistoryProvenance.Native("test"));

    private static HistoryCommitPack CreatePack(HistoryPackObject item, PackId? packId = null)
        => new(packId ?? PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, [item]);
}

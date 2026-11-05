using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Services;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryPackReadSessionTests
{
    private readonly HistoryPackCodec _codec = new();
    private readonly HistoryConfigId _config = new(Guid.NewGuid().ToString("N"));
    private readonly SourceId _source = SourceId.New();

    [TestMethod]
    public void ReusesVerifiedTypedFactsButStillRejectsMutationOfThePublicCanonicalArray()
    {
        var pack = Pack(_codec.CreateObject(Version(VersionId.New(), [])));
        using var session = new HistoryPackReadSession();
        var decoded = _codec.DecodeOwned(_codec.Encode(pack), session);
        var item = decoded.Pack.Objects.Single();
        var first = session.DeserializeKnown(item, _codec);
        Assert.AreSame(first, session.DeserializeKnown(item, _codec));
        new HistoryRepositoryValidator(_codec).Validate(_config, [decoded], session);
        item.CanonicalPayload[0] ^= 1;
        Assert.ThrowsExactly<HistoryIntegrityConflictException>(() => session.DeserializeKnown(item, _codec));
        Assert.ThrowsExactly<HistoryIntegrityConflictException>(() => new HistoryRepositoryValidator(_codec).Validate(_config, [decoded], session));
    }

    [TestMethod]
    public void OwnedDecodeStillChecksCanonicalHashAndEnvelopeIdentityBeforeMemoizing()
    {
        var item = _codec.CreateObject(Version(VersionId.New(), []));
        using var session = new HistoryPackReadSession();
        var badHash = new HistoryPackObject(item.Kind, item.SchemaVersion, item.Id, new string('0', 64), item.CanonicalPayload);
        Assert.ThrowsExactly<HistoryRepositoryValidationException>(() => _codec.DecodeOwned(_codec.Encode(Pack(badHash)), session));
        var badIdentity = new HistoryPackObject(item.Kind, item.SchemaVersion, VersionId.New().ToString(), item.PayloadHash, item.CanonicalPayload);
        Assert.ThrowsExactly<HistoryRepositoryValidationException>(() => _codec.DecodeOwned(_codec.Encode(Pack(badIdentity)), session));
    }

    [TestMethod]
    public void ForwardReferencesValidateAgainstTheWholeUnionAndUnknownSchemaIsPreserved()
    {
        var parent = Version(VersionId.New(), []);
        var child = Version(VersionId.New(), [parent.VersionId]);
        using var document = JsonDocument.Parse("{\"future\":true}");
        var unknown = _codec.CreateUnknownObject("future-fact", 2, "future-id", document.RootElement);
        using var session = new HistoryPackReadSession();
        var first = _codec.DecodeOwned(_codec.Encode(Pack(_codec.CreateObject(child), unknown)), session);
        var second = _codec.DecodeOwned(_codec.Encode(Pack(_codec.CreateObject(parent))), session);
        new HistoryRepositoryValidator(_codec).Validate(_config, [first, second], session);
        Assert.IsTrue(first.ContainsUnsupportedObjectSchema);
        CollectionAssert.AreEqual(unknown.CanonicalPayload, first.Pack.Objects.Single(o => o.Kind == unknown.Kind).CanonicalPayload);
        Assert.ThrowsExactly<HistoryRepositoryValidationException>(() => new HistoryRepositoryValidator(_codec).Validate(_config, [first], session));
    }

    [TestMethod]
    public void ReusedFactsDoNotBypassConflictingDefinitionsOrConfigValidation()
    {
        var id = VersionId.New();
        using var session = new HistoryPackReadSession();
        var first = _codec.DecodeOwned(_codec.Encode(Pack(_codec.CreateObject(Version(id, [])))), session);
        var conflicting = Version(id, [], "conflicting descriptor");
        // Two individually valid envelopes still cannot redefine the same immutable fact.
        var second = _codec.DecodeOwned(_codec.Encode(Pack(_codec.CreateObject(conflicting))), session);
        Assert.ThrowsExactly<HistoryIntegrityConflictException>(() => new HistoryRepositoryValidator(_codec).Validate(_config, [first, second], session));
        Assert.ThrowsExactly<HistoryRepositoryValidationException>(() => new HistoryRepositoryValidator(_codec).Validate(new(Guid.NewGuid().ToString("N")), [first], session));
    }

    [TestMethod]
    public void PublicDecodeKeepsItsOwnedCopyAndDisposedSessionsCannotBeReused()
    {
        var bytes = _codec.Encode(Pack(_codec.CreateObject(Version(VersionId.New(), []))));
        var decoded = _codec.Decode(bytes);
        bytes[0] ^= 1;
        Assert.AreNotEqual(bytes[0], decoded.OriginalBytes[0]);
        var session = new HistoryPackReadSession(); session.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => _codec.DecodeOwned(decoded.OriginalBytes, session));
    }

    [TestMethod]
    public async Task EveryImportReadsDiskAgainAndDoesNotTrustAPreviousOperationsCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-fact-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var repository = new FileHistoryRepository(_config, new(root));
            await repository.InitializeAsync();
            var initial = Pack(_codec.CreateObject(Version(VersionId.New(), [])));
            var diagnostic = HistoryOperationDiagnostics.Begin("fact-session-import");
            using (diagnostic)
            {
                await repository.CommitAsync(initial);
            }
            var statistics = diagnostic.Summary!;
            Assert.AreEqual(1L, statistics.Counters["history-known-facts-reused"]);
            var path = Directory.GetFiles(repository.Paths.PacksRoot, "*.frpack", SearchOption.AllDirectories).Single();
            var before = await File.ReadAllBytesAsync(path);
            var changed = (byte[])before.Clone(); changed[0] ^= 1;
            await File.WriteAllBytesAsync(path, changed);
            await Assert.ThrowsExactlyAsync<HistoryRepositoryValidationException>(() => repository.CommitAsync(Pack(_codec.CreateObject(Version(VersionId.New(), [])))));
            Assert.HasCount(1, Directory.GetFiles(repository.Paths.PacksRoot, "*.frpack", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private SourceVersion Version(VersionId id, IEnumerable<VersionId> parents, string name = "fixture")
        => new(id, _config, _source, parents, DateTimeOffset.UtcNow, null, CaptureScope.FullSource,
            CaptureOutcome.Captured, [], new(name, "C:\\fixture"), null, HistoryProvenance.Native("test"));
    private static HistoryCommitPack Pack(params HistoryPackObject[] items)
        => new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, items);
}

using FolderRewind.History.Domain;
using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryRepositoryGraphDepthTests
{
    private static readonly HistoryConfigId Config = new("graph-depth-tests");
    private static readonly SourceId Source = SourceId.Parse("11111111-1111-1111-1111-111111111111");

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TenThousandAncestorsValidateOrRejectBackEdgeWithoutRecursiveStack(bool closeCycle)
    {
        const int count = 10000;
        var ids = Enumerable.Range(0, count).Select(_ => VersionId.New()).ToArray();
        // The first enumerated node points to the next, forcing the entire depth before
        // any ancestor can be marked visited. Parent-first fixtures would hide recursion.
        var versions = ids.Select((id, index) => Version(id,
            index + 1 < count ? [ids[index + 1]] : closeCycle ? [ids[0]] : [])).ToArray();
        if (closeCycle)
        {
            var error = Assert.ThrowsExactly<HistoryRepositoryValidationException>(() => Validate(versions));
            Assert.AreEqual($"SourceVersion graph contains a cycle at '{ids[0]}'.", error.Message);
        }
        else Validate(versions);
    }

    [TestMethod]
    public void DiamondWithSharedAncestorIsNotACycle()
    {
        var ancestor = Version(VersionId.New(), []);
        var left = Version(VersionId.New(), [ancestor.VersionId]);
        var right = Version(VersionId.New(), [ancestor.VersionId]);
        var merge = Version(VersionId.New(), [left.VersionId, right.VersionId], SourceVersionCreationKind.Merge);
        Validate([merge, left, right, ancestor]);
    }

    [TestMethod]
    public void MissingParentKeepsRepositoryReferenceDiagnostic()
    {
        var missing = VersionId.New();
        var error = Assert.ThrowsExactly<HistoryRepositoryValidationException>(() => Validate([Version(VersionId.New(), [missing])]));
        Assert.AreEqual($"SourceVersion parent '{missing}' is missing.", error.Message);
    }

    [TestMethod]
    public void SelfReferenceKeepsCycleDiagnostic()
    {
        var id = VersionId.New();
        var error = Assert.ThrowsExactly<HistoryRepositoryValidationException>(() => Validate([Version(id, [id])]));
        Assert.AreEqual($"SourceVersion graph contains a cycle at '{id}'.", error.Message);
    }

    private static SourceVersion Version(VersionId id, IEnumerable<VersionId> parents,
        SourceVersionCreationKind kind = SourceVersionCreationKind.Capture)
        => new(id, Config, Source, parents, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null,
            CaptureScope.FullSource, CaptureOutcome.Captured, [], new("source", "C:\\source"), null,
            HistoryProvenance.Native("test"), creationKind: kind);

    private static void Validate(IEnumerable<SourceVersion> versions)
    {
        var codec = new HistoryPackCodec();
        var pack = new HistoryCommitPack(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            versions.Select(version => codec.CreateObject(version)));
        new HistoryRepositoryValidator(codec).Validate(Config, [new(pack, [], false)]);
    }
}

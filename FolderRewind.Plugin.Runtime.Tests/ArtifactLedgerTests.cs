using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class ArtifactLedgerTests
{
    [TestMethod]
    public void ExplicitRootClosureKeepsSharedDependencyWithoutOwningHistoryRoots()
    {
        var folderId = Guid.NewGuid();
        var shared = CreateArtifact(folderId);
        var retained = CreateArtifact(folderId, shared.ArtifactId);
        var unrelated = CreateArtifact(folderId, shared.ArtifactId);
        var ledger = new ArtifactLedgerDocument(
            ArtifactLedgerValidator.CurrentSchemaVersion,
            new ArtifactGraphRevision("revision"),
            [shared, retained, unrelated]);

        var closure = ArtifactLedgerValidator.BuildClosure(ledger, [retained.ArtifactId]);

        CollectionAssert.AreEquivalent(
            new[] { shared.ArtifactId, retained.ArtifactId },
            closure.Reachable.ToArray());
        Assert.DoesNotContain(unrelated.ArtifactId, closure.Reachable);
        Assert.DoesNotContain(
            "HistoryRoots",
            typeof(ArtifactLedgerDocument).GetProperties().Select(property => property.Name));
    }

    private static ArtifactLedgerEntry CreateArtifact(Guid folderId, params ArtifactId[] dependencies)
        => new(
            new ArtifactId(Guid.NewGuid()),
            new ArtifactFormatRef(new OwnerId("plugin.test"), "format"),
            1,
            new RestoreStrategyId(new PluginId("plugin.test"), "restore"),
            "config",
            folderId,
            $"artifacts/{Guid.NewGuid():N}",
            new string('a', 64),
            1,
            new string('b', 64),
            1,
            ArtifactCompleteness.Complete,
            CoreCaptureMode.Full,
            dependencies,
            Guid.NewGuid().ToString("N"),
            ArtifactAvailability.Available,
            ArtifactAvailability.Pending);
}

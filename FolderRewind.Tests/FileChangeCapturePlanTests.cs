using System.Collections.Immutable;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Tests;

[TestClass]
public sealed class FileChangeCapturePlanTests
{
    [TestMethod]
    public void CompleteRootDoesNotRequestCheckpointBecauseOfCaptureCount()
    {
        var boundary = EffectiveSourceBoundarySnapshot.All;
        var states = ImmutableSortedDictionary<string, SourceCaptureFileState>.Empty.Add("world", new(10, DateTime.UnixEpoch));
        var baseline = new SourceCaptureBaseline(SourceId.New(), 0, VersionId.New(), RepresentationId.New(),
            RepresentationKind.PluginArtifact, "unused", 1000, states, boundary.Fingerprint) { ArtifactRootId = Guid.NewGuid() };
        var plan = FileChangeCapturePlan.Create(states, baseline, boundary, changesOnly: true);
        Assert.IsFalse(plan.Input.Checkpoint);
        Assert.AreEqual(ArtifactCaptureInputKind.ChangesOnly, plan.Input.Kind);
        Assert.AreEqual(baseline.BaseVersionId.ToString(), plan.Input.BaseVersionId);
        Assert.IsEmpty(plan.Input.AddedFiles); Assert.IsEmpty(plan.Input.ModifiedFiles); Assert.IsEmpty(plan.Input.DeletedFiles);
        Assert.IsTrue(FileChangeCapturePlan.Create(states, baseline, boundary, true, requestCheckpoint: true).Input.Checkpoint);
        Assert.ThrowsExactly<InvalidOperationException>(() => FileChangeCapturePlan.Create(states,
            baseline with { ArtifactRootId = null }, boundary, true));
    }
}

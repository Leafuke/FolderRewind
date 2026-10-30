using FolderRewind.History.Capture;

namespace FolderRewind.Tests;

[TestClass]
public sealed class ScopeAwareSourceCaptureDiffTests
{
    [TestMethod]
    public void DeleteIsReportedInsideCapturedScopeButParentOutsideScopeIsInherited()
    {
        var unchanged = new SourceCaptureFileState(1, DateTime.UnixEpoch);
        var parent = new Dictionary<string, SourceCaptureFileState>(StringComparer.OrdinalIgnoreCase)
        {
            ["selected/a.txt"] = unchanged,
            ["selected/deleted.txt"] = unchanged,
            ["outside/keep.txt"] = unchanged
        };
        var current = new Dictionary<string, SourceCaptureFileState>(StringComparer.OrdinalIgnoreCase)
        {
            ["selected/a.txt"] = new(2, DateTime.UnixEpoch.AddSeconds(1)),
            ["selected/new.txt"] = unchanged
        };

        var diff = ScopeAwareSourceCaptureDiff.Compute(
            parent,
            current,
            path => path.StartsWith("selected/", StringComparison.OrdinalIgnoreCase));

        CollectionAssert.AreEqual(new[] { "selected/new.txt" }, diff.AddedFiles.ToArray());
        CollectionAssert.AreEqual(new[] { "selected/a.txt" }, diff.ModifiedFiles.ToArray());
        CollectionAssert.AreEqual(new[] { "selected/deleted.txt" }, diff.DeletedFiles.ToArray());
        Assert.DoesNotContain("outside/keep.txt", diff.DeletedFiles);
    }
}

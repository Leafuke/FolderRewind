using System.Collections.Immutable;
using System.Security.Cryptography;
using FolderRewind.History.Capture;
using FolderRewind.History.Representation;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Tests;

[TestClass]
public sealed class IncrementalCaptureEvidenceTests
{
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static ArtifactMaterializedFile File(string path, byte[] bytes) => new(path, bytes.Length, Hash(bytes),
        new(new(Guid.NewGuid()), "pack.bin", 0, 10, Hash([1]), "test-recipe", 1));
    private static IncrementalCaptureEvidence Evidence(params ArtifactMaterializedFile[] files) => new(IncrementalCaptureEvidence.CurrentSchema, "config", Guid.NewGuid(),
        "version", "representation", "boundary", "size-mtime-v1", files.Select(f => new ArtifactFileEntry(f.RelativePath, f.Length, f.Sha256)).ToArray(),
        files.ToImmutableSortedDictionary(f => f.RelativePath, f => new SourceCaptureFileState(f.Length, DateTime.UnixEpoch)), 1);
    private static ArtifactIncrementalInput Input(bool checkpoint = false, params string[] changed) => new(Guid.NewGuid(),
        ArtifactCaptureInputKind.ChangesOnly, "boundary", "size-mtime-v1", "base", "base-rep", new(Guid.NewGuid()), [], changed, [], checkpoint);

    [TestMethod]
    [DataRow("space")]
    [DataRow("provider")]
    [DataRow("bytes")]
    [DataRow("cancel")]
    public async Task PreparedViewReservesBeforeProviderAndReleasesResourcesOnFailure(string failure)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NativePreparedView", Guid.NewGuid().ToString("N"));
        var evidence = Evidence(File("file", [1]));
        long available = failure == "space" ? 0 : 1;
        var budget = new ArtifactSpaceBudget(_ => new("test-volume", available));
        using var cancellation = new CancellationTokenSource();
        var view = new View([File("file", [1])], new() { ["file"] = failure == "bytes" ? [2] : [1] });
        int opens = 0;
        ValueTask<IArtifactFileView> Open()
        {
            opens++;
            Assert.ThrowsExactly<IOException>(() => budget.Reserve(new ArtifactSpaceRequest(root, 1)));
            if (failure == "provider") throw new InvalidOperationException("provider failure");
            if (failure == "cancel") cancellation.Cancel();
            return ValueTask.FromResult<IArtifactFileView>(view);
        }
        try
        {
            await Assert.ThrowsAsync<Exception>(() => evidence.MaterializeViewToAsync(Open, root, cancellation.Token, budget));
            Assert.AreEqual(failure == "space" ? 0 : 1, opens);
            Assert.AreEqual(failure is "bytes" or "cancel", view.Disposed);
            if (failure is "space" or "provider" or "cancel") Assert.IsFalse(Directory.Exists(root));
            available = 1;
            using var afterFailure = budget.Reserve(new ArtifactSpaceRequest(root, 1));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task InheritedRecipeIsNotOpenedButChangedBytesAreIndependentlyChecked()
    {
        var old = File("old", [1]); var changed = File("changed", [2]);
        await using var baseline = new View([old], []);
        await using var candidate = new View([old, changed], new() { ["changed"] = [2] });
        await Evidence(old, changed).ValidateViewAsync(candidate, baseline, Input(false, "changed"), default);
        CollectionAssert.AreEqual(new[] { "changed" }, candidate.Opened.ToArray());
        candidate.Bytes["changed"] = [3];
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Evidence(old, changed).ValidateViewAsync(candidate, baseline, Input(false, "changed"), default));
    }

    [TestMethod]
    public async Task ChangedUnannouncedRecipeIsRejectedEvenWhenClaimedHashMatches()
    {
        var old = File("old", [1]); var forged = old with { Recipe = old.Recipe with { ArtifactId = new(Guid.NewGuid()) } };
        await using var baseline = new View([old], []); await using var candidate = new View([forged], new() { ["old"] = [1] });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Evidence(old).ValidateViewAsync(candidate, baseline, Input(), default));
        await Evidence(old).ValidateViewAsync(candidate, baseline, Input(true), default);
        CollectionAssert.AreEqual(new[] { "old" }, candidate.Opened.ToArray());
    }

    [TestMethod]
    public async Task MissingExtraAndLongStreamsAreRejected()
    {
        var file = File("a", [1]);
        await using var missing = new View([], []);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Evidence(file).ValidateViewAsync(missing, null, Input(true), default));
        await using var extra = new View([file, File("b", [])], []);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Evidence(file).ValidateViewAsync(extra, null, Input(true), default));
        await using var oversized = new View([file], new() { ["a"] = [1, 2] });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Evidence(file).ValidateViewAsync(oversized, null, Input(true), default));
    }

    [TestMethod]
    public async Task DeepVerificationReadsInheritedFilesAndRejectsSameSizeCorruption()
    {
        var file = File("old", [1]);
        await using var candidate = new View([file], new() { ["old"] = [2] });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Evidence(file).VerifyViewAsync(candidate, default));
        CollectionAssert.AreEqual(new[] { "old" }, candidate.Opened.ToArray());
    }

    [TestMethod]
    public async Task DirectoryEvidenceSurvivesRestoreAndChangesFingerprint()
    {
        var root = Path.Combine(Path.GetTempPath(), "NativeEvidence", Guid.NewGuid().ToString("N"));
        var file = File("region/a", [1]);
        var evidence = Evidence(file) with { Directories = ["empty", "empty/nested", "region"] };
        try
        {
            await using var view = new View([file], new() { ["region/a"] = [1] });
            await evidence.CopyViewToAsync(view, root, default);
            Assert.IsTrue(Directory.Exists(Path.Combine(root, "empty", "nested")));
            CollectionAssert.AreEqual(new byte[] { 1 }, await System.IO.File.ReadAllBytesAsync(Path.Combine(root, "region", "a")));
            Assert.AreNotEqual(evidence.Fingerprint(), (evidence with { Directories = ["region"] }).Fingerprint());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DirectoryEvidenceRejectsAmbiguousPathsAndMissingParents()
    {
        foreach (var directories in new IReadOnlyList<string>[] { ["A", "a"], ["a/b"], ["../escape"], ["file"] })
            Assert.Throws<Exception>(() => (Evidence(File("file", [1])) with { Directories = directories }).Fingerprint());
        Assert.ThrowsExactly<InvalidDataException>(() => Evidence(File("missing/file", [1])).Fingerprint());
    }

    [TestMethod]
    public void DirectoryInventoryIncludesEmptyDirectoriesAndParentsWithinTheBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "NativeInventory", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "empty", "nested"));
            Directory.CreateDirectory(Path.Combine(root, "excluded"));
            Directory.CreateDirectory(Path.Combine(root, "data"));
            var actual = SourceDirectorySnapshot.Read(root, p => !p.StartsWith("excluded", StringComparison.Ordinal), ["data/a"], default);
            CollectionAssert.AreEqual(new[] { "data", "empty", "empty/nested" }, actual.ToArray());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class View(IReadOnlyList<ArtifactMaterializedFile> files, Dictionary<string, byte[]> bytes) : IArtifactFileView
    {
        public Dictionary<string, byte[]> Bytes => bytes;
        public List<string> Opened { get; } = [];
        public bool Disposed { get; private set; }
        public IReadOnlyList<ArtifactMaterializedFile> Files => files;
        public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Opened.Add(relativePath); return ValueTask.FromResult<Stream>(new MemoryStream(bytes[relativePath], false)); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}

using System.Collections.Immutable;
using System.Security.Cryptography;
using FolderRewind.History.Capture;
using FolderRewind.History.Representation;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Tests;

[TestClass]
public sealed class IncrementalCaptureEvidenceTests
{
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static ArtifactMaterializedFile File(string path, byte[] bytes) => new(path, bytes.Length, Hash(bytes),
        new(new(Guid.NewGuid()), "pack.bin", 0, 10, Hash([1]), "test-recipe", 1));
    private static IncrementalCaptureEvidence Evidence(params ArtifactMaterializedFile[] files) => new(1, "config", Guid.NewGuid(),
        "version", "representation", "boundary", "size-mtime-v1", files.Select(f => new ArtifactFileEntry(f.RelativePath, f.Length, f.Sha256)).ToArray(),
        files.ToImmutableSortedDictionary(f => f.RelativePath, f => new SourceCaptureFileState(f.Length, DateTime.UnixEpoch)), 1);
    private static ArtifactIncrementalInput Input(bool checkpoint = false, params string[] changed) => new(Guid.NewGuid(),
        ArtifactCaptureInputKind.ChangesOnly, "boundary", "size-mtime-v1", "base", "base-rep", new(Guid.NewGuid()), [], changed, [], checkpoint);

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

    private sealed class View(IReadOnlyList<ArtifactMaterializedFile> files, Dictionary<string, byte[]> bytes) : IArtifactFileView
    {
        public Dictionary<string, byte[]> Bytes => bytes;
        public List<string> Opened { get; } = [];
        public IReadOnlyList<ArtifactMaterializedFile> Files => files;
        public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Opened.Add(relativePath); return ValueTask.FromResult<Stream>(new MemoryStream(bytes[relativePath], false)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

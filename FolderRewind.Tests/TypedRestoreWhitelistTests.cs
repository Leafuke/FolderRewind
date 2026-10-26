using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using System.Text;

namespace FolderRewind.Tests;

[TestClass]
public sealed class TypedRestoreWhitelistTests
{
    [TestMethod]
    public async Task TypedPreservationOnlyAddsMissingMatchingFilesWithoutMaskingArchiveContent()
    {
        using var current = new View(new Dictionary<string, string>
        {
            ["session.lock"] = "keep lock", ["nested/session.lock/data"] = "ordinary nested data",
            ["cache/new.dat"] = "preserve", ["cache/existing.dat"] = "current must not overwrite archive",
            ["other/cache"] = "same named file"
        });
        using var target = new View(new Dictionary<string, string> { ["CACHE/existing.dat"] = "archive" });
        var proposals = await RestoreWhitelistPreparation.PrepareAsync(current, target,
            [new("session.lock", PathFilterTarget.File), new("cache", PathFilterTarget.Directory)],
            Path.Combine(Path.GetTempPath(), "TypedRestoreWhitelist"), default);
        CollectionAssert.AreEquivalent(new[] { "session.lock", "cache/new.dat" }, proposals.Select(p => p.RelativePath).ToArray());
        CollectionAssert.AreEquivalent(new[] { "session.lock", "cache/new.dat" }, current.Reads.ToArray());
        Assert.IsEmpty(target.Reads);
    }

    private sealed class View(Dictionary<string, string> files) : IRestoreSourceView, IDisposable
    {
        public IReadOnlyList<string> RelativePaths => files.Keys.ToArray();
        internal List<string> Reads { get; } = [];
        public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
        {
            Reads.Add(relativePath);
            return ValueTask.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(files[relativePath]), false));
        }
        public void Dispose() { }
    }
}

using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class ArchiveCompressionPolicyTests
{
    [TestMethod]
    [DataRow("LZMA2", 0, 9)]
    [DataRow("Deflate", 0, 9)]
    [DataRow("BZip2", 1, 9)]
    [DataRow("zstd", 1, 22)]
    [DataRow("bzip2", 0, 9)]
    [DataRow("unknown", 0, 9)]
    [DataRow(null, 0, 9)]
    public void LevelRangePreservesConfiguredMethodSemantics(string? method, int expectedMin, int expectedMax)
    {
        var range = ArchiveCompressionPolicy.GetLevelRange(method);

        Assert.AreEqual(expectedMin, range.Min);
        Assert.AreEqual(expectedMax, range.Max);
    }

    [TestMethod]
    [DataRow("lzma2", "LZMA2")]
    [DataRow("DEFLATE", "Deflate")]
    [DataRow("bZip2", "BZip2")]
    [DataRow("ZSTD", "zstd")]
    public void SupportedMethodsNormalizeToCanonicalNames(string input, string expected)
    {
        Assert.IsTrue(ArchiveCompressionPolicy.TryNormalizeMethod(input, out var normalized));
        Assert.AreEqual(expected, normalized);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("brotli")]
    [DataRow(null)]
    public void UnsupportedMethodsAreRejected(string? input)
    {
        Assert.IsFalse(ArchiveCompressionPolicy.TryNormalizeMethod(input, out var normalized));
        Assert.AreEqual(string.Empty, normalized);
    }
}

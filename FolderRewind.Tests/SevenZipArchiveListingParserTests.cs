using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SevenZipArchiveListingParserTests
{
    private const string Listing = """
        7-Zip listing header
        ----------
        Path = folder
        Size = 0
        Folder = +

        Path = folder/keep.txt
        Size = 4
        Folder = -

        Path = added.bin
        Size = 9
        Folder = -
        """;

    [TestMethod]
    public void ExactLogicalStateAcceptsFilesAndIgnoresDirectories()
    {
        Assert.IsTrue(SevenZipArchiveListingParser.TryParse(Listing, out var entries));

        Assert.IsTrue(ArchiveLogicalStateVerifier.Matches(
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["folder\\keep.txt"] = 4,
                ["added.bin"] = 9
            },
            entries));
    }

    [TestMethod]
    public void DeletedFileStillPresentMakesLogicalStateInexact()
    {
        Assert.IsTrue(SevenZipArchiveListingParser.TryParse(Listing, out var entries));

        Assert.IsFalse(ArchiveLogicalStateVerifier.Matches(
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["added.bin"] = 9
            },
            entries));
    }

    [TestMethod]
    public void UnsafeArchivePathIsRejected()
    {
        const string unsafeListing = """
            ----------
            Path = ../escape.txt
            Size = 1
            Folder = -
            """;

        Assert.IsFalse(SevenZipArchiveListingParser.TryParse(unsafeListing, out _));
    }

    [TestMethod]
    public void EntryWithoutFolderClassificationIsRejected()
    {
        const string malformedListing = """
            ----------
            Path = ambiguous.txt
            Size = 1
            """;

        Assert.IsFalse(SevenZipArchiveListingParser.TryParse(malformedListing, out _));
    }
}

using FolderRewind.Services;
using System.Diagnostics;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SevenZipArchiveListingParserTests
{
    private const string Listing = """
        7-Zip listing header
        ----------
        Path = folder
        Size = 0
        Attributes = D

        Path = folder/keep.txt
        Size = 4
        Attributes = A

        Path = added.bin
        Size = 9
        Attributes = A
        """;

    [TestMethod]
    public void ExactLogicalStateAcceptsFilesAndIgnoresDirectories()
    {
        Assert.IsTrue(SevenZipArchiveListingParser.TryParse(Listing, out var entries));

        Assert.IsTrue(ArchiveLogicalStateVerifier.Matches(
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["folder/keep.txt"] = 4,
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
            Attributes = A
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

    [TestMethod]
    public void ContradictoryFolderAndAttributesClassificationIsRejected()
    {
        const string contradictoryListing = """
            ----------
            Path = contradictory.txt
            Size = 1
            Folder = -
            Attributes = D
            """;

        Assert.IsFalse(SevenZipArchiveListingParser.TryParse(contradictoryListing, out _));
    }

    [TestMethod]
    public void DeletionMarkerListingWithBackslashesIsNormalizedToForwardSlashes()
    {
        const string deletionMarkerListing = """
            7-Zip listing header
            ----------
            Path = __FolderRewind_Internal
            Size = 0
            Attributes = D

            Path = __FolderRewind_Internal\__DeletedOnly.marker
            Size = 28
            Attributes = A
            """;

        Assert.IsTrue(SevenZipArchiveListingParser.TryParse(deletionMarkerListing, out var entries));
        Assert.HasCount(1, entries);
        Assert.IsTrue(entries.ContainsKey("__FolderRewind_Internal/__DeletedOnly.marker"));
        Assert.AreEqual(28, entries["__FolderRewind_Internal/__DeletedOnly.marker"].Size);
    }
}

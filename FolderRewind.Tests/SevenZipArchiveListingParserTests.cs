using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SevenZipArchiveListingParserTests
{
    [TestMethod]
    [DataRow("A", false)]
    [DataRow("R", false)]
    [DataRow("H", false)]
    [DataRow("S", false)]
    [DataRow("N", false)]
    [DataRow("TsCOIEVvXPUMB", false)]
    [DataRow("RHSANTsCOI", false)]
    [DataRow("D", true)]
    [DataRow("HD", true)]
    [DataRow("....A", false)]
    [DataRow(".....", false)]
    [DataRow(".RHS.", false)]
    [DataRow("D....", true)]
    [DataRow("DRHSA", true)]
    [DataRow("-rw-r--r--", false)]
    [DataRow("drwxr-xr-x", true)]
    [DataRow("-rwsr-Sr-T", false)]
    [DataRow("A -rw-r--r--", false)]
    [DataRow("A_ -rw-r--r--", false)]
    [DataRow("D_ drwxr-xr-x", true)]
    [DataRow("D drwxr-xr-x", true)]
    [DataRow("  N\t ", false)]
    public void KnownAttributesDescribeTheExpectedEntryType(string attributes, bool directory)
    {
        Assert.IsTrue(SevenZipArchiveListingParser.TryParse(
            $"----------\nPath = entry\nSize = 1\nAttributes = {attributes}\n", out var files, out var folders, out var failure), failure?.ToString());
        Assert.IsNull(failure);
        Assert.HasCount(directory ? 0 : 1, files);
        Assert.HasCount(directory ? 1 : 0, folders);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow(" \t ")]
    public void ExplicitFolderClassifiesEmptyOrMissingAttributesInEitherOrder(string? attributeSuffix)
    {
        foreach (bool directory in new[] { false, true })
        foreach (bool folderFirst in new[] { false, true })
        {
            string attributes = attributeSuffix is null ? "" : $"Attributes ={attributeSuffix}\n";
            string folder = $"Folder = {(directory ? "+" : "-")}\n";
            string fields = folderFirst ? folder + attributes : attributes + folder;
            Assert.IsTrue(SevenZipArchiveListingParser.TryParse($"----------\nPath = entry\nSize = 1\n{fields}",
                out var files, out var folders, out var failure), failure?.ToString());
            Assert.HasCount(directory ? 0 : 1, files);
            Assert.HasCount(directory ? 1 : 0, folders);
        }
    }

    [TestMethod]
    [DataRow("bogus", "UnsupportedAttributes")]
    [DataRow("A_", "UnsupportedAttributes")]
    [DataRow(".D...", "UnsupportedAttributes")]
    [DataRow("A 00040000", "UnsupportedAttributes")]
    [DataRow("-", "UnsupportedAttributes")]
    [DataRow("-rwxrwxrwz", "UnsupportedAttributes")]
    [DataRow("directory", "UnsupportedAttributes")]
    [DataRow("d", "UnsupportedEntryType")]
    [DataRow("L", "UnsupportedEntryType")]
    [DataRow("AL", "UnsupportedEntryType")]
    [DataRow("8", "UnsupportedEntryType")]
    [DataRow("lrwxrwxrwx", "UnsupportedEntryType")]
    [DataRow("A prw-r--r--", "UnsupportedEntryType")]
    [DataRow("A drwxr-xr-x", "ClassificationConflict")]
    [DataRow("D -rw-r--r--", "ClassificationConflict")]
    public void ExplicitFolderDoesNotOverrideUnknownOrContradictoryAttributes(string attributes, string reason)
    {
        foreach (bool folderFirst in new[] { false, true })
        {
            string folder = "Folder = -\n";
            string flags = $"Attributes = {attributes}\n";
            Assert.IsFalse(SevenZipArchiveListingParser.TryParse(
                $"----------\nPath = entry\nSize = 1\n{(folderFirst ? folder + flags : flags + folder)}",
                out var files, out var folders, out var failure));
            Assert.IsNotNull(failure);
            Assert.AreEqual(reason, failure.Reason.ToString());
            Assert.AreEqual(1, failure.EntryIndex);
            Assert.AreEqual("Attributes", failure.Field);
            Assert.HasCount(0, files);
            Assert.HasCount(0, folders);
        }
    }

    [TestMethod]
    [DataRow(null, "EmptyOutput")]
    [DataRow(" \n", "EmptyOutput")]
    [DataRow("7-Zip\nPath = private-archive.7z\nType = 7z\n", "MissingEntrySection")]
    public void InvalidOutputHasAGlobalFailure(string? output, string reason)
    {
        Assert.IsFalse(SevenZipArchiveListingParser.TryParse(output, out var files, out var folders, out var failure));
        Assert.IsNotNull(failure);
        Assert.AreEqual(reason, failure.Reason.ToString());
        Assert.AreEqual(0, failure.EntryIndex);
        Assert.HasCount(0, files);
        Assert.HasCount(0, folders);
        Assert.DoesNotContain("private-archive", failure.ToString());
    }

    [TestMethod]
    public void EntrySectionWithoutEntriesIsAValidEmptyListing()
    {
        Assert.IsTrue(SevenZipArchiveListingParser.TryParse("7-Zip\n----------\n", out var files, out var folders, out var failure));
        Assert.IsNull(failure);
        Assert.HasCount(0, files);
        Assert.HasCount(0, folders);
    }

    [TestMethod]
    [DataRow("Path = private-file\nSize = 1\n", "MissingClassification", "Folder")]
    [DataRow("Path = private-file\nSize = 1\nAttributes =\n", "MissingClassification", "Folder")]
    [DataRow("Path = private-file\nSize = 1\nFolder = ?\n", "InvalidFolderValue", "Folder")]
    [DataRow("Path = private-file\nAttributes = N\n", "InvalidSize", "Size")]
    [DataRow("Path = private-file\nSize = -1\nAttributes = N\n", "InvalidSize", "Size")]
    [DataRow("Path = private-file\nSize = 1\nSize = 2\nAttributes = N\n", "InvalidSize", "Size")]
    [DataRow("Path = private-file\nSize = 1\nFolder = +\nAttributes = N\n", "ClassificationConflict", "Attributes")]
    [DataRow("Path = private/NUL.txt\nSize = 1\nAttributes = N\n", "UnsafePath", "Path")]
    [DataRow("Path = good\nSize = 1\nAttributes = N\n", "DuplicatePath", "Path")]
    [DataRow("Path = good/private-file\nSize = 1\nAttributes = N\n", "ParentFileConflict", "Path")]
    [DataRow("Path = private-file\nSize = 1\nAttributes = N\nSymbolic Link = private-target\n", "UnsupportedEntryType", "Symbolic Link")]
    [DataRow("Path = private-file\nSize = 1\nAttributes = N\nHard Link = private-target\n", "UnsupportedEntryType", "Hard Link")]
    [DataRow("Path = private-file\nSize = 1\nAttributes = N\nAlternate Stream = +\n", "UnsupportedEntryType", "Alternate Stream")]
    public void LaterInvalidEntriesInvalidateTheWholeListingWithSafeDiagnostics(string invalidEntry, string reason, string field)
    {
        string listing = "Path = private-archive.7z\n----------\nPath = good\nSize = 1\nAttributes = A\n\n" + invalidEntry;
        Assert.IsFalse(SevenZipArchiveListingParser.TryParse(listing, out var files, out var folders, out var failure));
        Assert.IsNotNull(failure);
        Assert.AreEqual(reason, failure.Reason.ToString());
        Assert.AreEqual(2, failure.EntryIndex);
        Assert.AreEqual(field, failure.Field);
        Assert.HasCount(0, files);
        Assert.HasCount(0, folders);
        Assert.DoesNotContain("private", failure.ToString());
        if (field is "Path" or "Symbolic Link" or "Hard Link") StringAssert.Contains(failure.ValuePreview, "length=");
    }

    [TestMethod]
    public void DiagnosticPreviewsAreEscapedBoundedAndKeepTheFirstFailure()
    {
        string value = "unknown\t\u0001\"\\" + new string('Q', 150);
        Assert.IsFalse(SevenZipArchiveListingParser.TryParse(
            $"----------\nPath = private-file\nSize = 1\nAttributes = {value}\nFolder = ?\n",
            out _, out _, out var failure));
        Assert.IsNotNull(failure);
        Assert.AreEqual(ArchiveListingParseFailureReason.UnsupportedAttributes, failure.Reason);
        Assert.IsLessThanOrEqualTo(80, failure.ValuePreview.Length);
        StringAssert.Contains(failure.ValuePreview, "\\t\\u0001\\\"\\\\");
        StringAssert.EndsWith(failure.ValuePreview, "…");
        Assert.DoesNotContain("\t", failure.ToString());
        Assert.DoesNotContain("\u0001", failure.ToString());
        Assert.DoesNotContain("private", failure.ToString());
    }

    [TestMethod]
    public void LeadingSpaceIsPreservedAndWindowsAmbiguitiesAreRejected()
    {
        Assert.IsTrue(SevenZipArchiveListingParser.TryParse("----------\nPath =  leading.txt\nSize = 1\nAttributes = A\n", out var entries));
        Assert.IsTrue(entries.ContainsKey(" leading.txt"));
        foreach (var path in new[] { "trailing ", "dot.", "a//b", "NUL.txt", "COM1" })
            Assert.IsFalse(SevenZipArchiveListingParser.TryParse($"----------\nPath = {path}\nSize = 1\nAttributes = A\n", out _));
        Assert.IsFalse(SevenZipArchiveListingParser.TryParse("----------\nPath = file\nSize = 1\nAttributes = A\n\nPath = file/child\nSize = 1\nAttributes = A\n", out _));
    }
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
    [DataRow("../outside", "D", "")]
    [DataRow("C:/outside", "D", "")]
    [DataRow("valid", "A", "Symbolic Link = ../outside")]
    [DataRow("valid", "A", "Hard Link = other")]
    [DataRow("valid", "A", "Alternate Stream = +")]
    public void ExtractionSafetyRejectsUnsafeDirectoriesLinksAndStreams(string path, string attributes, string extra)
        => Assert.IsFalse(SevenZipArchiveListingParser.TryParse($"----------\nPath = {path}\nSize = 1\nAttributes = {attributes}\n{extra}\n", out _));

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

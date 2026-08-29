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
    public async Task BundledSevenZipMachineListingParsesRealOutput()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("The bundled 7za.exe integration test requires Windows.");
        var sevenZip = Path.Combine(AppContext.BaseDirectory, "7za.exe");
        Assert.IsTrue(File.Exists(sevenZip), $"Bundled 7za.exe was not copied to the test output: {sevenZip}");
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindSevenZipListingTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var archive = Path.Combine(root, "sample.7z");
        Directory.CreateDirectory(Path.Combine(source, "folder"));
        await File.WriteAllTextAsync(Path.Combine(source, "folder", "keep.txt"), "keep");
        await File.WriteAllBytesAsync(Path.Combine(source, "added.bin"), new byte[9]);

        try
        {
            var create = await RunSevenZipAsync(sevenZip, source, "a", "-t7z", archive, ".");
            Assert.AreEqual(0, create.ExitCode, create.Error);
            var listing = await RunSevenZipAsync(sevenZip, root, "l", "-slt", "-sccUTF-8", archive);
            Assert.AreEqual(0, listing.ExitCode, listing.Error);
            Assert.IsTrue(SevenZipArchiveListingParser.TryParse(listing.Output, out var entries), listing.Output);
            Assert.IsTrue(ArchiveLogicalStateVerifier.Matches(
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                {
                    ["folder\\keep.txt"] = 4,
                    ["added.bin"] = 9
                },
                entries));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunSevenZipAsync(
        string executable,
        string workingDirectory,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start bundled 7za.exe.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }
}

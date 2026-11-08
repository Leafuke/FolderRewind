using FolderRewind.Services;
using System.Security.Cryptography;

namespace FolderRewind.Tests;

internal static class MineRewindIntegrationPackage
{
    internal static string Resolve(string applicationRoot)
    {
        // Explicit candidate acceptance never changes the distributable bundle or bypasses activation validation.
        var candidate = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_MINEREWIND_PACKAGE");
        if (candidate is null) return BundledMineRewindPackage.Resolve(applicationRoot);
        Assert.IsTrue(Path.IsPathFullyQualified(candidate) && File.Exists(candidate), "Candidate package must be an existing absolute path.");
        var expected = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_MINEREWIND_SHA256");
        using var input = File.OpenRead(candidate);
        Assert.AreEqual(expected?.ToUpperInvariant(), Convert.ToHexString(SHA256.HashData(input)), "Exact candidate SHA-256 is required.");
        Console.WriteLine("Exact candidate integration: " + candidate);
        return candidate;
    }
}

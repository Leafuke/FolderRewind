using FolderRewind.Services;
using System.IO.Compression;
using System.Security.Cryptography;

namespace FolderRewind.Tests;

[TestClass]
public sealed class ToolArchiveInstallerTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize() => _root = Path.Combine(Path.GetTempPath(), "FolderRewindToolArchiveTests", Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public void ChecksumMismatchDoesNotExtractAnything()
    {
        var bytes = Archive("bin/rclone.exe");
        Assert.ThrowsExactly<InvalidDataException>(() => ToolArchiveInstaller.ExtractVerified(bytes, new string('0', 64), _root, "rclone.exe"));
        Assert.IsFalse(Directory.Exists(_root));
    }

    [TestMethod]
    [DataRow("../escape.exe", 0)]
    [DataRow("C:/escape.exe", 0)]
    [DataRow("bin/link", 0xA0000000)]
    public void UnsafeEntryIsRejectedBeforeExtraction(string name, long attributes)
    {
        var bytes = Archive(name, unchecked((int)attributes));
        Assert.ThrowsExactly<InvalidDataException>(() => ToolArchiveInstaller.ExtractVerified(bytes, Convert.ToHexString(SHA256.HashData(bytes)), _root, "rclone.exe"));
        Assert.IsFalse(Directory.Exists(_root));
    }

    [TestMethod]
    public void VerifiedArchiveResolvesOneExecutable()
    {
        var bytes = Archive("bin/rclone.exe");
        var executable = ToolArchiveInstaller.ExtractVerified(bytes, Convert.ToHexString(SHA256.HashData(bytes)), _root, "rclone.exe");
        Assert.AreEqual(Path.Combine(_root, "bin", "rclone.exe"), executable);
        Assert.AreEqual("test", File.ReadAllText(executable));
    }

    private static byte[] Archive(string name, int attributes = 0)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry(name); entry.ExternalAttributes = attributes;
            using var writer = new StreamWriter(entry.Open()); writer.Write("test");
        }
        return stream.ToArray();
    }
}

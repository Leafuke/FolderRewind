using System.Security.Cryptography;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class RcloneLegacyConfigurationDownloadTests
{
    private string _root = null!;
    private RcloneExecutionContext _context = null!;
    private string Repository => Path.Combine(_root, "repository");
    private string Output => Path.Combine(_root, "output", "config.json");
    private RcloneMigrationSnapshotTransport Transport => new(_context);

    [TestInitialize]
    public void Initialize()
    {
        var executable = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_RCLONE");
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable)) Assert.Inconclusive("Set FOLDERREWIND_TEST_RCLONE.");
        _root = Path.Combine(Path.GetTempPath(), "LegacyConfigurationDownload", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Repository); Directory.CreateDirectory(Path.GetDirectoryName(Output)!);
        var config = Path.Combine(_root, "rclone.conf"); File.WriteAllText(config, "[test]\ntype = local\n");
        _context = new(executable, config, _root, "test:repository", Path.Combine(_root, "tasks"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        _context?.Dispose();
        if (_root is not null && Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    [DataRow("config.json")]
    [DataRow("config-20261026.json")]
    public async Task DownloadsBothLegacyLocationsAndDoesNotOverwriteExistingDestination(string name)
    {
        var source = name == "config.json" ? Path.Combine(Repository, name)
            : Path.Combine(Repository, "_folderrewind", "config-exports", name);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "{\"synthetic\":true}");
        await Transport.DownloadLegacyConfigurationAsync(name, Output, default);
        CollectionAssert.AreEqual(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(Output));
        await File.WriteAllTextAsync(source, "different remote bytes");
        await Assert.ThrowsAsync<IOException>(() => Transport.DownloadLegacyConfigurationAsync(name, Output, default));
        Assert.AreEqual("{\"synthetic\":true}", await File.ReadAllTextAsync(Output));
        AssertNoTemporaryFiles();
    }

    [TestMethod]
    [DataRow(67108864, true)]
    [DataRow(67108865, false)]
    public async Task EnforcesTheExact64MiBBoundary(int length, bool accepted)
    {
        var source = Path.Combine(Repository, "config.json");
        using (var file = File.Create(source)) file.SetLength(length);
        if (accepted)
        {
            await Transport.DownloadLegacyConfigurationAsync("config.json", Output, default);
            Assert.AreEqual((long)length, new FileInfo(Output).Length);
            using var expected = File.OpenRead(source); using var actual = File.OpenRead(Output);
            CollectionAssert.AreEqual(await SHA256.HashDataAsync(expected), await SHA256.HashDataAsync(actual));
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Transport.DownloadLegacyConfigurationAsync("config.json", Output, default));
            Assert.IsFalse(File.Exists(Output));
        }
        AssertNoTemporaryFiles();
    }

    [TestMethod]
    [DataRow("grow")]
    [DataRow("shrink")]
    [DataRow("cancel")]
    public async Task StatToReadChangesFailWithoutReplacingDestinationOrLeavingTemporaryFiles(string change)
    {
        var source = Path.Combine(Repository, "config.json"); await File.WriteAllBytesAsync(source, [1, 2, 3]);
        await File.WriteAllTextAsync(Output, "keep");
        using var cancellation = new CancellationTokenSource();
        var commands = 0;
        _context.TargetGuard = () =>
        {
            if (++commands != 2) return; // Real stat completed; change the source before real cat.
            if (change == "cancel") cancellation.Cancel();
            else File.WriteAllBytes(source, change == "grow" ? [1, 2, 3, 4] : [1, 2]);
        };
        if (change == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(() => Transport.DownloadLegacyConfigurationAsync("config.json", Output, cancellation.Token));
        else await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Transport.DownloadLegacyConfigurationAsync("config.json", Output, cancellation.Token));
        Assert.AreEqual(2, commands);
        Assert.AreEqual("keep", await File.ReadAllTextAsync(Output));
        AssertNoTemporaryFiles();
    }

    [TestMethod]
    public async Task RejectsDirectoryAndUnsafeNameBeforeDownloading()
    {
        Directory.CreateDirectory(Path.Combine(Repository, "config.json"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Transport.DownloadLegacyConfigurationAsync("config.json", Output, default));
        _context.TargetGuard = () => Assert.Fail("Invalid name must not start a command.");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Transport.DownloadLegacyConfigurationAsync("../config.json", Output, default));
        Assert.IsFalse(File.Exists(Output));
        AssertNoTemporaryFiles();
    }

    private void AssertNoTemporaryFiles() => Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(Output)!, "*.download"));
}

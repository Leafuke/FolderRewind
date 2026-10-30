using FolderRewind.History.Capture;
using FolderRewind.Models;
using FolderRewind.Services;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SourceCaptureInputTests
{
    private string _root = null!;
    private string Source => Path.Combine(_root, "source");
    private string Temp => Path.Combine(_root, "temp");

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewind-input-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "changed.bin"), "new content");
        File.WriteAllText(Path.Combine(Source, "unchanged.bin"), "old content");
    }

    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private SourceCaptureInput Inventory()
        => new(Source, Directory.GetFiles(Source).ToDictionary(path => Path.GetFileName(path),
            path => new SourceCaptureFileState(new FileInfo(path).Length, File.GetLastWriteTimeUtc(path))), [], null, "boundary");

    [TestMethod]
    public void MissingAndExplicitCopySettingsRoundTrip()
    {
        Assert.IsFalse(JsonSerializer.Deserialize<ArchiveSettings>("{}")!.CopySourceBeforeArchive);
        var copy = JsonSerializer.Deserialize<ArchiveSettings>(JsonSerializer.Serialize(new ArchiveSettings { CopySourceBeforeArchive = true }))!;
        Assert.IsTrue(copy.CopySourceBeforeArchive);
    }

    [TestMethod]
    public async Task SparseCopyRetainsCompleteInventoryAndDoesNotReadUnchangedFile()
    {
        using var input = Inventory();
        using var locked = new FileStream(Path.Combine(Source, "unchanged.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var view = await input.PrepareAsync(["changed.bin"], true, true, Temp);
        Assert.HasCount(2, input.Files);
        Assert.HasCount(1, view.ReadPaths);
        Assert.AreEqual(input.Files["changed.bin"].Size, view.CopiedBytes);
        Assert.IsFalse(File.Exists(Path.Combine(view.SourcePath, "unchanged.bin")));
        Assert.AreEqual("new content", File.ReadAllText(Path.Combine(view.SourcePath, "changed.bin")));
        var copyPath = view.SourcePath;
        input.Dispose();
        Assert.IsFalse(Directory.Exists(copyPath));
        Assert.IsTrue(File.Exists(Path.Combine(Source, "changed.bin")));
    }

    [TestMethod]
    public void UnchangedCaptureDoesNotPrepareOrAllocateInput()
    {
        using var input = Inventory();
        Assert.IsNull(input.View);
        Assert.IsFalse(Directory.Exists(Temp));
    }

    [TestMethod]
    public async Task ProtectedDirectReadHoldsBytesAndParentUntilDisposed()
    {
        using var input = Inventory();
        var view = await input.PrepareAsync(["changed.bin"], false, true, Temp);
        Assert.AreEqual(Source, view.SourcePath);
        Assert.AreEqual(0L, view.CopiedBytes);
        Assert.IsFalse(Directory.Exists(Temp));
        Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(Source, "changed.bin"), "replacement"));
        Assert.Throws<IOException>(() => File.Move(Path.Combine(Source, "changed.bin"), Path.Combine(Source, "renamed.bin")));
        Assert.Throws<IOException>(() => Directory.Move(Source, Source + "-renamed"));
        await using (var stream = await view.OpenReadAsync("changed.bin", default))
        using (var reader = new StreamReader(stream)) Assert.AreEqual("new content", await reader.ReadToEndAsync());
        input.Dispose();
        File.WriteAllText(Path.Combine(Source, "changed.bin"), "released");
    }

    [TestMethod]
    public async Task WriteConflictFailsAndReleasesEarlierGuardsWithoutCopyFallback()
    {
        using var input = Inventory();
        using (var writer = new FileStream(Path.Combine(Source, "unchanged.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var error = await Assert.ThrowsAsync<CaptureInputException>(() => input.PrepareAsync(input.Files.Keys, false, true, Temp));
            StringAssert.Contains(error.SourcePath, "unchanged.bin");
            Assert.IsNull(input.View);
            Assert.IsFalse(Directory.Exists(Temp));
            File.WriteAllText(Path.Combine(Source, "changed.bin"), "guard released");
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChangedInventoryIsRejectedBeforeUse(bool copy)
    {
        using var input = Inventory();
        File.AppendAllText(Path.Combine(Source, "changed.bin"), "grew");
        await Assert.ThrowsAsync<CaptureInputException>(() => input.PrepareAsync(["changed.bin"], copy, true, Temp));
        Assert.IsNull(input.View);
        Assert.IsFalse(Directory.Exists(Temp) && Directory.EnumerateDirectories(Temp).Any());
    }

    [TestMethod]
    public async Task InputCannotExpandInventoryAndCancellationDoesNotLeaveCopy()
    {
        using var input = Inventory();
        await Assert.ThrowsAsync<InvalidDataException>(() => input.PrepareAsync(["outside.bin"], true, false, Temp));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => input.PrepareAsync(input.Files.Keys, true, false, Temp, cancel.Token));
        Assert.IsFalse(Directory.Exists(Temp) && Directory.EnumerateDirectories(Temp).Any());
    }

    [TestMethod]
    public async Task CoreDirectReadDoesNotRequireNativeWriteExclusion()
    {
        using var input = Inventory();
        using var writer = new FileStream(Path.Combine(Source, "changed.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var view = await input.PrepareAsync(["changed.bin"], false, false, Temp);
        Assert.IsFalse(view.IsStable);
        Assert.IsFalse(view.TryProtectMetadata(["changed.bin"]));
        Assert.AreEqual(0L, view.CopiedBytes);
    }

    [TestMethod]
    public void CompleteScanNeverTurnsMissingSourceIntoAnEmptyInventory()
    {
        Assert.Throws<IOException>(() => BackupSourceFileEnumerator.EnumerateUnordered(
            Path.Combine(_root, "missing"), new BackupSourceScope(), requireComplete: true).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnInternalDirectoryAliasCannotBecomeCaptureInput(bool copy)
    {
        var outside = Path.Combine(_root, "outside"); Directory.CreateDirectory(outside);
        var data = Path.Combine(outside, "data"); File.WriteAllText(data, "outside bytes");
        Directory.CreateSymbolicLink(Path.Combine(Source, "alias"), outside);
        using var input = new SourceCaptureInput(Source,
            new Dictionary<string, SourceCaptureFileState> { ["alias/data"] = new(new FileInfo(data).Length, File.GetLastWriteTimeUtc(data)) },
            ["alias"], null, "boundary");
        await Assert.ThrowsAsync<CaptureInputException>(() => input.PrepareAsync(["alias/data"], copy, true, Temp));
        Assert.AreEqual("outside bytes", File.ReadAllText(data));
    }

    [TestMethod]
    public async Task StatisticsValidationKeepsUnchangedInputsProtectedThroughSparsePreparation()
    {
        using var input = Inventory();
        var validation = await input.PrepareStatisticsValidationAsync(false, Temp, default);
        Assert.AreSame(validation, await input.PrepareAsync(["changed.bin"], false, true, Temp));
        Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(Source, "unchanged.bin"), "changed"));
        input.Dispose();
        File.WriteAllText(Path.Combine(Source, "unchanged.bin"), "released");
    }

    [TestMethod]
    public async Task CopiedValidationAndSparseCaptureUseTheSameBytesWhileOriginalIsWriteOpen()
    {
        using var input = Inventory();
        using var diagnostics = HistoryOperationDiagnostics.Begin("copy-baseline-revalidation");
        using var writer = new FileStream(Path.Combine(Source, "changed.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var validation = await input.PrepareStatisticsValidationAsync(true, Temp, default);
        var temporary = validation.SourcePath;
        await writer.WriteAsync("replacement"u8.ToArray()); await writer.FlushAsync();
        var sparse = await input.PrepareAsync(["changed.bin"], true, true, Temp);
        Assert.AreSame(validation, sparse, "Revalidation must not be followed by a second live-source copy.");
        await using (var stream = await sparse.OpenCapturedReadAsync("changed.bin", default))
        using (var reader = new StreamReader(stream)) Assert.AreEqual("new content", await reader.ReadToEndAsync());
        input.Dispose(); diagnostics.Dispose();
        Assert.IsFalse(Directory.Exists(temporary));
        Assert.AreEqual(input.Files.Values.Sum(f => f.Size), diagnostics.Summary!.Counters["source-copy-bytes"]);
        Assert.AreEqual(1L, diagnostics.Summary.Counters["source-input-lease-reuses"]);
    }

    [TestMethod]
    public async Task RevalidationWithoutCopyStillFailsOnWriteConflictAndReleasesInput()
    {
        using var input = Inventory();
        using (var writer = new FileStream(Path.Combine(Source, "unchanged.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            await Assert.ThrowsAsync<CaptureInputException>(() => input.PrepareStatisticsValidationAsync(false, Temp, default));
        Assert.IsNull(input.View);
        File.WriteAllText(Path.Combine(Source, "changed.bin"), "released");
        Assert.IsFalse(Directory.Exists(Temp));
    }
}

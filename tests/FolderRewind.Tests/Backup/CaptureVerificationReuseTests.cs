using FolderRewind.History.Application;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class CaptureVerificationReuseTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewind-reuse-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root); File.WriteAllText(Path.Combine(_root, "file.bin"), "content");
    }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public void BaselineStatisticsDoNotReadFileContent()
    {
        using var exclusive = new FileStream(Path.Combine(_root, "file.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = HistoryCaptureBaselineRepair.ReadMetadata(_root, default);
        Assert.AreEqual(7L, result["file.bin"].Size);
    }

    [TestMethod]
    public void UnusedRestoreViewDoesNotOpenLockedFiles()
    {
        using var exclusive = new FileStream(Path.Combine(_root, "file.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var diagnostics = HistoryOperationDiagnostics.Begin("lazy-unused");
        using var view = new HistoryRestoreSourceReadView(_root, new(SourceId.New(), _root));
        view.Dispose(); diagnostics.Dispose();
        Assert.AreEqual(0L, diagnostics.Summary!.Counters.GetValueOrDefault("restore-preparation-inventory-loads"));
    }

    [TestMethod]
    public async Task BorrowedRestoreViewCachesInventoryAndDoesNotEndOuterProtection()
    {
        var binding = new HistoryRestoreSourceBinding(SourceId.New(), _root);
        using var read = new HistoryWorkingTreeReadLease(binding);
        using (var view = new HistoryRestoreSourceReadView(_root, binding, read))
        {
            Assert.AreSame(view.RelativePaths, view.RelativePaths);
            await using var stream = await view.OpenReadAsync("file.bin", default);
            using var reader = new StreamReader(stream);
            Assert.AreEqual("content", await reader.ReadToEndAsync());
        }
        Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(_root, "file.bin"), "blocked"));
        read.Validate(); read.Dispose();
        File.WriteAllText(Path.Combine(_root, "file.bin"), "released");
    }

    [TestMethod]
    public async Task NativeProofReusesOnlyProtectedInputsWithinSameOperation()
    {
        var representation = new VersionRepresentation(RepresentationId.New(), VersionId.New(), RepresentationKind.PluginArtifact,
            "test", [], MaterializationFidelity.Exact, null, null, new Dictionary<string, string>());
        var path = Path.Combine(_root, "file.bin");
        object owner = new(); int verifications = 0;
        ValueTask<PayloadVerificationResult> Verify(CancellationToken token)
        { verifications++; return ValueTask.FromResult(new PayloadVerificationResult(true, "verified", "")); }
        using (var context = new ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced))
        {
            Assert.IsTrue((await context.VerifyProtectedNativeAsync(owner, representation, "policy", "closure", [path], Verify, default)).Success);
            Assert.IsTrue((await context.VerifyProtectedNativeAsync(owner, representation, "policy", "closure", [path], Verify, default)).Success);
            Assert.AreEqual(1, verifications);
            Assert.Throws<IOException>(() => File.WriteAllText(path, "changed"));
            await Assert.ThrowsAsync<IOException>(async () => await context.VerifyProtectedNativeAsync(owner, representation,
                "other-password", "closure", [path], Verify, default));
            await Assert.ThrowsAsync<IOException>(async () => await context.VerifyProtectedNativeAsync(owner, representation,
                "policy", "changed-closure", [path], Verify, default));
        }
        File.WriteAllText(path, "new bytes");
        using var next = new ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced);
        await next.VerifyProtectedNativeAsync(owner, representation, "policy", "closure", [path], Verify, default);
        Assert.AreEqual(2, verifications);
    }
}

using System.Collections.Immutable;
using System.Security.Cryptography;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class ArchiveRestoreVerificationContextTests
{
    [TestMethod]
    public async Task ChangedCredentialInvalidatesOperationAndWrongPasswordIsNotFallback()
    {
        await using var f = new HistoryChainRewriteFixture(encrypted: true); await f.InitializeAsync();
        var node = await f.AddAsync(null, dir => File.WriteAllText(Path.Combine(dir, "file"), "content"));
        using var operation = new ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced);
        Assert.IsTrue((await operation.VerifyAsync(f.Archive, node.Representation, node.Entry.Locator.AbsolutePath!, default)).Success);
        f.Password = "different-test-password";
        await Assert.ThrowsAsync<IOException>(async () => await operation.VerifyAsync(f.Archive, node.Representation, node.Entry.Locator.AbsolutePath!, default));
        using var next = new ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced);
        var failure = await next.VerifyAsync(f.Archive, node.Representation, node.Entry.Locator.AbsolutePath!, default);
        // 无加密头的归档可无需密码列清单；实际所需内容仍必须解密成功才允许提交。
        Assert.IsTrue(failure.Success);
        var graph = await f.History.Query.GetAllRepresentationsAsync();
        var env = new RepresentationEnvironment((await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries, [], []);
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () => await f.Engine.MaterializeAsync(
            node.Representation.RepresentationId, graph, env, MaterializationFidelity.Exact, Path.Combine(f.Root, "wrong-password"), default, next));
        Assert.IsFalse(error.Message.Contains(f.Password, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UnselectedCorruptEntryDoesNotClaimBalancedDeepHealthButStrictRejectsIt()
    {
        await using var f = new HistoryChainRewriteFixture(); await f.InitializeAsync();
        var data = new byte[8192]; new Random(719).NextBytes(data);
        var first = await f.AddAsync(null, dir => File.WriteAllBytes(Path.Combine(dir, "shared.bin"), data));
        // 以无固实 Copy 归档使数据块位置可确定。测试故意省略 storageSha256；有摘要时两档均拒绝损坏。
        var archive = first.Entry.Locator.AbsolutePath!; File.Delete(archive);
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_7Z")!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "a", "-t7z", "-m0=Copy", "-ms=off", archive, Path.Combine(first.StateDirectory, "*") }) start.ArgumentList.Add(arg);
        using (var process = System.Diagnostics.Process.Start(start)!)
        { var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.AreEqual(0, process.ExitCode, await error); await output; }
        var final = await f.AddAsync(first, dir => File.WriteAllText(Path.Combine(dir, "shared.bin"), "final content"));
        var bytes = File.ReadAllBytes(archive); bytes[40] ^= 1; File.WriteAllBytes(archive, bytes);
        var graph = await f.History.Query.GetAllRepresentationsAsync();
        var env = new RepresentationEnvironment((await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries, [], []);
        using (var balanced = new ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced))
        {
            var assessment = await f.Engine.AssessVersionAsync(final.Version.VersionId, graph, env, AssessmentDepth.Deep, MaterializationFidelity.Exact, default, balanced);
            Assert.AreEqual(HistoryReadiness.Ready, assessment.Readiness);
            Assert.IsFalse(assessment.Selected!.Evidence.Any(e => e.Kind == RepresentationEvidenceKind.DeepVerification));
            var staging = Path.Combine(f.Root, "balanced");
            await f.Engine.MaterializeAsync(final.Representation.RepresentationId, graph, env, MaterializationFidelity.Exact, staging, default, balanced);
            Assert.AreEqual("final content", File.ReadAllText(Path.Combine(staging, "shared.bin")));
        }
        using var strict = new ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Strict);
        Assert.IsFalse((await strict.VerifyAsync(f.Archive, first.Representation, archive, default)).Success);
    }
    [TestMethod]
    [DataRow(RestoreArchivePreflightMode.Balanced, 0L)]
    [DataRow(RestoreArchivePreflightMode.Strict, 1L)]
    public async Task OperationReusesProtectedVerificationAndExplicitDeepRemainsStrict(RestoreArchivePreflightMode mode, long tests)
    {
        await using var f = new HistoryChainRewriteFixture(); await f.InitializeAsync();
        var node = await f.AddAsync(null, dir => File.WriteAllText(Path.Combine(dir, "file.txt"), "content"));
        var graph = await f.History.Query.GetAllRepresentationsAsync();
        var env = new RepresentationEnvironment((await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries, [], []);
        var diagnostics = HistoryOperationDiagnostics.Begin("preflight-reuse");
        using (var operation = new ArchiveRestoreVerificationContext(mode))
        using (diagnostics)
        {
            var first = await f.Engine.AssessVersionAsync(node.Version.VersionId, graph, env,
                AssessmentDepth.Deep, MaterializationFidelity.Exact, default, operation);
            Assert.AreEqual(HistoryReadiness.Ready, first.Readiness);
            using var inputs = await f.Engine.LockExactVersionAsync(node.Version.VersionId, graph, env, default, operation);
            if (mode == RestoreArchivePreflightMode.Balanced)
                Assert.IsFalse(first.Selected!.Evidence.Any(e => e.Kind == RepresentationEvidenceKind.DeepVerification));
            await f.Engine.MaterializeAsync(inputs.RepresentationId, graph, env, MaterializationFidelity.Exact,
                Path.Combine(f.Root, "output"), default, operation);
            Assert.Throws<IOException>(() => File.WriteAllText(node.Entry.Locator.AbsolutePath!, "changed"));
            Assert.Throws<IOException>(() => File.Move(node.Entry.Locator.AbsolutePath!, node.Entry.Locator.AbsolutePath + ".replaced"));
            Assert.Throws<IOException>(() => Directory.Move(Path.GetDirectoryName(node.Entry.Locator.AbsolutePath!)!, Path.Combine(f.Root, "replaced-parent")));
        }
        var stats = diagnostics.Summary!;
        Assert.AreEqual(tests, stats.Counters.GetValueOrDefault("7z-t"));
        Assert.AreEqual(1L, stats.Counters["7z-l"]);
        Assert.AreEqual(1L, stats.Counters["7z-x"]);
        var deepDiagnostics = HistoryOperationDiagnostics.Begin("explicit-deep");
        using (deepDiagnostics)
            Assert.AreEqual(HistoryReadiness.Ready, (await f.Engine.AssessVersionAsync(node.Version.VersionId, graph, env,
                AssessmentDepth.Deep, MaterializationFidelity.Exact)).Readiness);
        Assert.AreEqual(1L, deepDiagnostics.Summary!.Counters["7z-t"]);
    }

    [TestMethod]
    public async Task SameSizeAndTimeChangedContentCannotReuseStorageHashAcrossOperations()
    {
        await using var f = new HistoryChainRewriteFixture(); await f.InitializeAsync();
        var node = await f.AddAsync(null, dir => File.WriteAllText(Path.Combine(dir, "file.txt"), "content"));
        var path = node.Entry.Locator.AbsolutePath!;
        var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var original = node.Representation;
        var representation = new VersionRepresentation(original.RepresentationId, original.VersionId, original.Kind,
            original.Format, original.DependencyRepresentationIds, original.Fidelity, null, null,
            original.RepresentationSpecificMetadata.SetItem("storageSha256", expected));
        using (var operation = new ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced))
            Assert.IsTrue((await operation.VerifyAsync(f.Archive, representation, path, default)).Success);
        var timestamp = File.GetLastWriteTimeUtc(path); var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1;
        File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, timestamp);
        using var next = new ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced);
        await Assert.ThrowsAsync<IOException>(async () => await next.VerifyAsync(f.Archive, representation, path, default));
    }
}

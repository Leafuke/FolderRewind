using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using FolderRewind.History.Domain;
using FolderRewind.History.Merge;
using FolderRewind.History.Representation;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SevenZipArchiveProcessBackendTests
{
    [TestMethod]
    public async Task UnsafeArchiveDiagnosticsReachVerificationAndExtractionWithoutRevealingPaths()
    {
        var executable = SevenZipExecutableLocator.Resolve(Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_7Z"));
        if (executable is null) Assert.Inconclusive("Set FOLDERREWIND_TEST_7Z for real-process tests.");
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindUnsafeListing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string payload = Path.Combine(root, "unsafe.zip");
            using (var zip = System.IO.Compression.ZipFile.Open(payload, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("../private-name.txt"); entry.ExternalAttributes = (int)FileAttributes.Archive;
                using var writer = new StreamWriter(entry.Open()); writer.Write("private contents");
            }
            var backend = new SevenZipArchiveProcessBackend(() => executable, () => null, false, ".marker");
            var representation = new VersionRepresentation(RepresentationId.New(), VersionId.New(), RepresentationKind.CoreFull,
                "zip", [], MaterializationFidelity.Exact, null, null, null);
            foreach (bool strict in new[] { false, true })
            {
                var result = await backend.PreflightAsync(representation, payload, strict, default);
                Assert.IsFalse(result.Success);
                StringAssert.Contains(result.Diagnostic, "Reason=UnsafePath; EntryIndex=1; Field=Path");
                Assert.DoesNotContain("private-name", result.Diagnostic);
            }
            string target = Path.Combine(root, "target");
            var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => backend.UnpackAsync(payload, target, false, default));
            StringAssert.Contains(failure.Message, "Reason=UnsafePath");
            Assert.DoesNotContain("private-name", failure.Message);
            Assert.IsFalse(Directory.Exists(target));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow("7z")]
    [DataRow("zip")]
    public async Task NormalAndArchiveAttributesRoundTripThroughRealListingsAndVerification(string format)
    {
        var executable = SevenZipExecutableLocator.Resolve(Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_7Z"));
        if (executable is null) Assert.Inconclusive("Set FOLDERREWIND_TEST_7Z for real-process tests.");
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindNormalAttributes", Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(Path.Combine(source, "中文目录", "empty"));
            var normal = Path.Combine(source, "normal.txt");
            File.WriteAllText(normal, "normal attributes");
            File.SetAttributes(normal, FileAttributes.Normal);
            File.WriteAllText(Path.Combine(source, "archive.txt"), "archive attributes");
            File.SetAttributes(Path.Combine(source, "archive.txt"), FileAttributes.Archive);
            File.WriteAllText(Path.Combine(source, "中文目录", "文件.txt"), "中文内容");
            Assert.AreEqual(FileAttributes.Normal, File.GetAttributes(normal));
            string archive = Path.Combine(root, "payload." + format);

            async Task<string> RunAsync(params string[] arguments)
            {
                using var process = new Process { StartInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                }};
                foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
                process.Start();
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                string stdout = await output, stderr = await error;
                Assert.AreEqual(0, process.ExitCode, stderr);
                return stdout;
            }

            await RunAsync("a", "-t" + format, archive, Path.Combine(source, "*"), "-y", "-sccUTF-8");
            string listing = await RunAsync("l", "-slt", "-sccUTF-8", archive);
            await RunAsync("t", archive, "-y");
            Assert.IsTrue(SevenZipArchiveListingParser.TryParse(listing, out var files, out var folders, out var failure), failure?.ToString());
            var expected = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(
                path => Path.GetRelativePath(source, path).Replace('\\', '/'), path => new FileInfo(path).Length, StringComparer.OrdinalIgnoreCase);
            Assert.IsTrue(ArchiveLogicalStateVerifier.Matches(expected, files));
            CollectionAssert.Contains(folders.ToArray(), "中文目录/empty");

            var backend = new SevenZipArchiveProcessBackend(() => executable, () => null, false, ".restore-marker");
            var representation = new VersionRepresentation(RepresentationId.New(), VersionId.New(), RepresentationKind.CoreFull,
                format, [], MaterializationFidelity.Exact, null, null, null);
            foreach (bool strict in new[] { false, true })
                Assert.IsTrue((await backend.PreflightAsync(representation, archive, strict, default)).Success);
            var restored = Path.Combine(root, "restored");
            await backend.UnpackAsync(archive, restored, false, default);
            Assert.AreEqual((await MergeTreeManifest.ReadAsync(source, _ => true, default)).Digest,
                (await MergeTreeManifest.ReadAsync(restored, _ => true, default)).Digest);
            Assert.IsTrue(Directory.Exists(Path.Combine(restored, "中文目录", "empty")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task LongPathsRoundTripWithoutAddingParentDirectories(bool encrypted, bool empty)
    {
        var executable = SevenZipExecutableLocator.Resolve(Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_7Z"));
        if (executable is null) Assert.Inconclusive("Set FOLDERREWIND_TEST_7Z for real-process tests.");
        var root = Path.Combine(Path.GetTempPath(), "FolderRewind7z", Guid.NewGuid().ToString("N"));
        try
        {
            var deep = Path.Combine(root, new string('a', 100), new string('b', 100));
            var source = Path.Combine(deep, "input with spaces");
            Directory.CreateDirectory(source);
            Assert.IsGreaterThan(260, source.Length);
            if (!empty)
            {
                Directory.CreateDirectory(Path.Combine(source, "nested"));
                File.WriteAllText(Path.Combine(source, "nested", "中文-file.txt"), "non-ASCII content 中文");
                File.WriteAllText(Path.Combine(source, "extensionless"), "without a dot");
                var bytes = new byte[32768]; new Random(719).NextBytes(bytes);
                File.WriteAllBytes(Path.Combine(source, "noise.bin"), bytes);
            }
            const string password = "test only p@ss word";
            var backend = new SevenZipArchiveProcessBackend(() => executable, () => password, encrypted, ".restore-marker");
            var version = new SourceVersion(VersionId.New(), new HistoryConfigId(Guid.NewGuid().ToString("N")), SourceId.New(),
                [], DateTimeOffset.UtcNow, null, CaptureScope.FullSource, CaptureOutcome.Captured, [], new("test", source), null,
                HistoryProvenance.Native("integration"));
            var id = RepresentationId.New();
            var payload = await backend.CreateFullAsync(version, source, id, Path.Combine(deep, "payload"), default);
            var representation = new VersionRepresentation(id, version.VersionId, RepresentationKind.CoreFull, "7z", [],
                MaterializationFidelity.Exact, null, null, ImmutableDictionary<string, string>.Empty);
            Assert.IsTrue((await backend.DeepVerifyAsync(representation, payload.PayloadPath, default)).Success);
            var output = Path.Combine(deep, "roundtrip");
            await backend.MaterializeAsync([new(representation, payload.PayloadPath)], output, default);
            Assert.AreEqual((await MergeTreeManifest.ReadAsync(source, _ => true, default)).Digest,
                (await MergeTreeManifest.ReadAsync(output, _ => true, default)).Digest);
            if (!empty) Assert.IsGreaterThan(5120L, new FileInfo(payload.PayloadPath).Length);
            File.WriteAllText(payload.PayloadPath, "invalid archive");
            var failed = await backend.DeepVerifyAsync(representation, payload.PayloadPath, default);
            Assert.IsFalse(failed.Success);
            StringAssert.Contains(failed.Diagnostic, "exited with code");
            Assert.IsFalse(failed.Diagnostic.Contains(password, StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task NativeExtractionChecksDeclaredBytesAndSharedEntryQuotaBeforeWriting()
    {
        var executable = SevenZipExecutableLocator.Resolve(Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_7Z"));
        if (executable is null) Assert.Inconclusive("Set FOLDERREWIND_TEST_7Z.");
        var root = Path.Combine(Path.GetTempPath(), "NativeExtractionLimits", Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            await File.WriteAllBytesAsync(Path.Combine(source, "one"), [1, 2, 3]);
            var backend = new SevenZipArchiveProcessBackend(() => executable, () => null, false, ".restore-marker");
            var payload = Path.Combine(root, "payload.7z"); await backend.PackAsync(source, payload, false, default);
            var rejected = Path.Combine(root, "too-long");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => backend.UnpackAsync(payload, rejected, false, 2, new(), default));
            Assert.IsFalse(Directory.Exists(rejected));
            var budget = new FolderRewind.Plugin.Runtime.Artifacts.ArtifactExtractionBudget(10, 1);
            await backend.UnpackAsync(payload, Path.Combine(root, "first"), false, 3, budget, default);
            var second = Path.Combine(root, "second");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => backend.UnpackAsync(payload, second, false, 3, budget, default));
            Assert.IsFalse(Directory.Exists(second));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task NativeReadUsesEachEnvelopeEncryptionFlagAndKeepsOneSharedDecodeBudget()
    {
        var executable = SevenZipExecutableLocator.Resolve(Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_7Z"));
        if (executable is null) Assert.Inconclusive("Set FOLDERREWIND_TEST_7Z.");
        var root = Path.Combine(Path.GetTempPath(), "NativeMixedEncryption", Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            await File.WriteAllBytesAsync(Path.Combine(source, "file"), [1, 2, 3]);
            var plain = new SevenZipArchiveProcessBackend(() => executable, () => "fixture-key", false, ".marker");
            var encrypted = new SevenZipArchiveProcessBackend(() => executable, () => "fixture-key", true, ".marker");
            var a = Path.Combine(root, "plain.7z"); var b = Path.Combine(root, "encrypted.7z");
            await plain.PackAsync(source, a, false, default); await encrypted.PackAsync(source, b, true, default);
            var budget = new FolderRewind.Plugin.Runtime.Artifacts.ArtifactExtractionBudget(6, 2);
            await encrypted.UnpackAsync(a, Path.Combine(root, "old-envelope"), false, 3, budget, default);
            await plain.UnpackAsync(b, Path.Combine(root, "new-envelope"), true, 3, budget, default);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => plain.UnpackAsync(b, Path.Combine(root, "over-limit"), true, 3, budget, default));
            var wrong = new SevenZipArchiveProcessBackend(() => executable, () => "wrong", false, ".marker");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => wrong.UnpackAsync(b, Path.Combine(root, "wrong"), true, 3, new(), default));
            Assert.ThrowsExactly<InvalidOperationException>(() => plain.PackAsync(source, Path.Combine(root, "mismatch.7z"), true, default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CredentialsAreRemovedFromBackendOutput()
        => Assert.AreEqual("command -p[redacted]: [redacted]", SevenZipArchiveProcessBackend.Redact("command -psecret: secret", "secret"));

    [TestMethod]
    public async Task ArtifactEnvelopeRejectsMissingWrongCredentialsAndCorruption()
    {
        var executable = SevenZipExecutableLocator.Resolve(Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_7Z"));
        if (executable is null) Assert.Inconclusive("Set FOLDERREWIND_TEST_7Z for real-process tests.");
        var root = Path.Combine(Path.GetTempPath(), "NativeEnvelopeTests", Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "input"); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "private-world-file.txt"), "unique plaintext world payload p2");
            var payload = Path.Combine(root, "payload.7z");
            var valid = new SevenZipArchiveProcessBackend(() => executable, () => "fixture-key", true, ".marker");
            await valid.PackAsync(source, payload, true, default);
            Assert.DoesNotContain("private-world-file", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(payload)));
            var representation = new VersionRepresentation(RepresentationId.New(), VersionId.New(), RepresentationKind.CoreFull,
                "7z", [], MaterializationFidelity.Exact, null, null, null);
            foreach (var password in new string?[] { null, "wrong-key" })
            {
                var invalid = new SevenZipArchiveProcessBackend(() => executable, () => password, true, ".marker");
                Assert.IsFalse((await invalid.PreflightAsync(representation, payload, false, default)).Success,
                    "Native envelope names must require the correct credential even for a safe listing.");
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => invalid.UnpackAsync(payload, Path.Combine(root, "invalid"), true, default));
            }
            await valid.UnpackAsync(payload, Path.Combine(root, "restored"), true, default);
            Assert.AreEqual("unique plaintext world payload p2", File.ReadAllText(Path.Combine(root, "restored", "private-world-file.txt")));
            using (var file = File.OpenWrite(payload)) { file.Position = file.Length / 2; file.Write(new byte[32]); }
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => valid.UnpackAsync(payload, Path.Combine(root, "corrupt"), true, default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

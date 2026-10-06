using System.Collections.Immutable;
using FolderRewind.History.Domain;
using FolderRewind.History.Merge;
using FolderRewind.History.Representation;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SevenZipArchiveProcessBackendTests
{
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
    public void CredentialsAreRemovedFromBackendOutput()
        => Assert.AreEqual("command -p[redacted]: [redacted]", SevenZipArchiveProcessBackend.Redact("command -psecret: secret", "secret"));
}

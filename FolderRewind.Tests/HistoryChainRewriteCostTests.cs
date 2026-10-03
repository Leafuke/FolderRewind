using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using FolderRewind.Services;
using System.Collections.Immutable;
using System.IO.Compression;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryChainRewriteCostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ActualArchiveCostsDriveDeltaFallbackOrSafeSkip(bool expensiveDelta)
    {
        await using var f = new HistoryChainRewriteFixture();
        await f.InitializeAsync();
        var time = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = await f.AddAsync(null, p =>
        {
            File.WriteAllBytes(Path.Combine(p, "keep.bin"), new byte[20000]);
            File.WriteAllBytes(Path.Combine(p, "change.bin"), new byte[80000]);
            foreach (var file in Directory.EnumerateFiles(p)) File.SetLastWriteTimeUtc(file, time);
        });
        var b = await f.AddAsync(a, p =>
        {
            File.WriteAllBytes(Path.Combine(p, "change.bin"), Enumerable.Repeat((byte)1, 80000).ToArray());
            File.SetLastWriteTimeUtc(Path.Combine(p, "change.bin"), time);
        });
        var c = await f.AddAsync(b, _ => { });
        var request = f.Delete(b) with { Origin = HistoryChainRewriteOrigin.Retention,
            RetainedVersionIds = [a.Version.VersionId, c.Version.VersionId], HideTargets = false, ReleaseTargets = false };
        var backend = new StoredArchiveBackend(f.Archive, expensiveDelta);
        var executor = new HistoryChainRewriteExecutor(f.History, f.Engine, backend);
        await using var prepared = await executor.PrepareAsync(await f.Planner.PlanAsync(request));
        Assert.AreEqual(1, backend.FullCount);
        Assert.AreEqual(1, backend.DeltaCount);
        var result = await executor.CommitAsync(prepared);
        Assert.AreEqual(!expensiveDelta, result.Committed, result.Diagnostic);
        if (expensiveDelta)
        {
            Assert.IsTrue(File.Exists(b.Entry.Locator.AbsolutePath));
            await f.AssertRestoresAsync(a, b, c);
        }
        else
        {
            Assert.IsGreaterThan(0L, result.NetReleasedBytes);
            Assert.AreEqual(RepresentationKind.CoreSmartDelta, prepared.Mappings.Single().Replacement.Kind);
            await f.AssertRestoresAsync(a, c);
        }
    }

    // Real, restorable stored ZIPs deliberately make the full option expensive; production writes 7z.
    private sealed class StoredArchiveBackend(SevenZipArchiveProcessBackend inner, bool expensiveDelta) : IHistoryChainRewriteArchiveBackend
    {
        public int FullCount { get; private set; }
        public int DeltaCount { get; private set; }
        public Task<HistoryCompactionPayload> CreateFullAsync(SourceVersion version, string directory, RepresentationId id, string output, CancellationToken token)
        {
            FullCount++;
            return Task.FromResult(WriteZip(directory, Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(directory, p)).ToImmutableArray(), output));
        }
        public Task<HistoryCompactionPayload> CreateDeltaAsync(SourceVersion version, string directory, ImmutableArray<string> changed, RepresentationId id, string output, CancellationToken token)
        {
            DeltaCount++;
            return expensiveDelta ? Task.FromResult(WriteZip(directory, changed, output))
                : inner.CreateDeltaAsync(version, directory, changed, id, output, token);
        }
        public ValueTask<PayloadVerificationResult> DeepVerifyAsync(VersionRepresentation representation, string path, CancellationToken token)
            => inner.DeepVerifyAsync(representation, path, token);
        private static HistoryCompactionPayload WriteZip(string directory, ImmutableArray<string> files, string output)
        {
            Directory.CreateDirectory(output);
            var path = Path.Combine(output, "stored.zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                    zip.CreateEntryFromFile(Path.Combine(directory, file), file.Replace('\\', '/'), CompressionLevel.NoCompression)
                        .ExternalAttributes = (int)FileAttributes.Archive;
                if (files.IsEmpty) zip.CreateEntry(".restore-marker/empty").ExternalAttributes = (int)FileAttributes.Archive;
            }
            return new("zip", path, new FileInfo(path).Length, null, null, ImmutableDictionary<string, string>.Empty);
        }
    }
}

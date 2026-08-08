using FolderRewind.Models;
using FolderRewind.Services.Discovery;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class LudusaviManifestCacheServiceTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FailedImportDoesNotReplaceCurrentGeneration()
    {
        var validPath = Path.Combine(_root, "manifest.yaml");
        await File.WriteAllTextAsync(validPath, "Game:\n  files:\n    '<home>/Game/save.sav': {}\n");
        var invalidPath = Path.Combine(_root, "invalid.yaml");
        await File.WriteAllTextAsync(invalidPath, "- not-a-game-map\n");
        var cacheRoot = Path.Combine(_root, "cache");
        var service = new LudusaviManifestCacheService(cacheRoot);

        var first = await service.ImportAndCompileAsync(validPath, null, null, null, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await service.ImportAndCompileAsync(invalidPath, null, null, null, CancellationToken.None));
        var current = await service.LoadCurrentAsync(CancellationToken.None);

        Assert.IsNotNull(current);
        Assert.AreEqual(first.Metadata.GenerationId, current.Value.Metadata.GenerationId);
        Assert.HasCount(1, current.Value.Index.Games);
    }

    [TestMethod]
    public async Task ImportStoresRawManifestCompressedIndexAndAtomicPointer()
    {
        var manifestPath = Path.Combine(_root, "manifest.yaml");
        await File.WriteAllTextAsync(manifestPath, "Game:\n  files:\n    '<home>/Game/save.sav': {}\n");
        var cacheRoot = Path.Combine(_root, "cache");
        var service = new LudusaviManifestCacheService(cacheRoot);

        var result = await service.ImportAndCompileAsync(manifestPath, null, null, null, CancellationToken.None);
        var generationRoot = Path.Combine(cacheRoot, "generations", result.Metadata.GenerationId);

        Assert.IsTrue(File.Exists(Path.Combine(cacheRoot, "current.json")));
        Assert.IsTrue(File.Exists(Path.Combine(generationRoot, "manifest.yaml")));
        Assert.IsTrue(File.Exists(Path.Combine(generationRoot, "index.v2.json.gz")));
        Assert.IsTrue(File.Exists(Path.Combine(generationRoot, "metadata.json")));
    }

    [TestMethod]
    public async Task ManualDownloadUsesEtagAndAcceptsNotModified()
    {
        var handler = new SequenceHandler(
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("Game:\n  files:\n    '<home>/Game/save.sav': {}\n")
                };
                response.Headers.ETag = new EntityTagHeaderValue("\"revision-1\"");
                return response;
            },
            request =>
            {
                Assert.AreEqual("\"revision-1\"", request.Headers.IfNoneMatch.Single().Tag);
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            });
        using var client = new HttpClient(handler);
        var service = new LudusaviManifestCacheService(Path.Combine(_root, "cache"));
        var uri = new Uri("https://example.test/manifest.yaml");

        var first = await service.DownloadAndCompileAsync(client, uri, null, null, null, CancellationToken.None);
        var second = await service.DownloadAndCompileAsync(client, uri, null, null, null, CancellationToken.None);

        Assert.AreEqual(LudusaviManifestUpdateStatus.Updated, first.Status);
        Assert.AreEqual(LudusaviManifestUpdateStatus.NotModified, second.Status);
        Assert.AreEqual(first.Metadata.GenerationId, second.Metadata.GenerationId);
    }

    [TestMethod]
    public async Task LegacyCompiledIndexIsRebuiltFromCachedManifestWithoutNetwork()
    {
        var cacheRoot = Path.Combine(_root, "cache");
        var legacyGenerationId = new string('a', 64);
        var legacyRoot = Path.Combine(cacheRoot, "generations", legacyGenerationId);
        Directory.CreateDirectory(legacyRoot);
        await File.WriteAllTextAsync(
            Path.Combine(legacyRoot, "manifest.yaml"),
            "Game Extended:\n  installDir:\n    GameFolder: {}\n");
        await File.WriteAllTextAsync(
            Path.Combine(legacyRoot, "metadata.json"),
            JsonSerializer.Serialize(new LudusaviManifestCacheMetadata
            {
                GenerationId = legacyGenerationId,
                SourceSha256 = new string('b', 64),
                SourceKind = "upstream",
                SourceUri = "https://example.test/manifest.yaml",
                ETag = "\"legacy\"",
                UpdatedAtUtc = DateTime.UtcNow
            }));
        await File.WriteAllTextAsync(
            Path.Combine(cacheRoot, "current.json"),
            JsonSerializer.Serialize(new LudusaviManifestPointer { GenerationId = legacyGenerationId }));
        await File.WriteAllTextAsync(Path.Combine(legacyRoot, "index.v1.json.gz"), "legacy");
        var service = new LudusaviManifestCacheService(cacheRoot);

        var current = await service.EnsureCurrentAsync(null, null, null, CancellationToken.None);

        Assert.IsNotNull(current);
        Assert.AreNotEqual(legacyGenerationId, current.Value.Metadata.GenerationId);
        Assert.AreEqual(LudusaviCompiledIndex.CurrentSchemaVersion, current.Value.Index.SchemaVersion);
        CollectionAssert.AreEqual(
            new[] { "GameFolder" },
            current.Value.Index.Games.Single().InstallDirectoryHints.ToArray());
        Assert.IsTrue(File.Exists(Path.Combine(
            cacheRoot,
            "generations",
            current.Value.Metadata.GenerationId,
            "index.v2.json.gz")));
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

        public SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        {
            _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }
}

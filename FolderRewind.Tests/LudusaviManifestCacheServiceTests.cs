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
        Assert.IsTrue(File.Exists(Path.Combine(generationRoot, "index.v3.json.gz")));
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
    public async Task NotModifiedStillRecompilesWhenManualSecondaryChanges()
    {
        var secondaryPath = Path.Combine(_root, "secondary.yaml");
        await File.WriteAllTextAsync(secondaryPath, "Extra One:\n  files:\n    '<home>/one.sav': {}\n");
        var handler = new SequenceHandler(
            _ => Response(HttpStatusCode.OK, "Primary:\n  files:\n    '<home>/primary.sav': {}\n", "\"r1\""),
            _ => new HttpResponseMessage(HttpStatusCode.NotModified));
        using var client = new HttpClient(handler);
        var service = new LudusaviManifestCacheService(Path.Combine(_root, "cache"));
        var uri = new Uri("https://example.test/manifest.yaml");

        var first = await service.DownloadAndCompileAsync(client, uri, secondaryPath, null, null, CancellationToken.None);
        await File.WriteAllTextAsync(secondaryPath, "Extra Two:\n  files:\n    '<home>/two.sav': {}\n");
        var second = await service.DownloadAndCompileAsync(client, uri, secondaryPath, null, null, CancellationToken.None);

        Assert.AreEqual(LudusaviManifestUpdateStatus.Updated, second.Status);
        Assert.AreNotEqual(first.Metadata.GenerationId, second.Metadata.GenerationId);
        Assert.IsTrue(second.Index.Games.Any(game => game.DisplayName == "Extra Two"));
        Assert.IsFalse(second.Index.Games.Any(game => game.DisplayName == "Extra One"));
    }

    [TestMethod]
    public async Task DeletedOverrideIsAnInputChangeAndIsOmitted()
    {
        var manifestPath = Path.Combine(_root, "manifest.yaml");
        var overridePath = Path.Combine(_root, "override.json");
        await File.WriteAllTextAsync(manifestPath, "Game:\n  files:\n    '<home>/old.sav': {}\n");
        await File.WriteAllTextAsync(overridePath, JsonSerializer.Serialize(new FolderRewindGameOverrideDocument
        {
            Entries = new[]
            {
                new FolderRewindGameOverrideEntry
                {
                    DefinitionId = "Game",
                    Operations = new[]
                    {
                        new FolderRewindGameOverrideOperation
                        {
                            Action = FolderRewindGameOverrideAction.Add,
                            Kind = BackupResourceKind.FileSet,
                            Expression = "<home>/added.sav"
                        }
                    }
                }
            }
        }));
        var service = new LudusaviManifestCacheService(Path.Combine(_root, "cache"));
        var first = await service.ImportAndCompileAsync(manifestPath, null, overridePath, null, CancellationToken.None);

        File.Delete(overridePath);
        var second = await service.EnsureCurrentAsync(null, overridePath, null, CancellationToken.None);

        Assert.IsNotNull(second);
        Assert.AreNotEqual(first.Metadata.GenerationId, second.Value.Metadata.GenerationId);
        Assert.HasCount(1, second.Value.Index.Games.Single().Files);
        Assert.IsTrue(second.Value.Metadata.Warnings.Any(warning => warning.Contains("override", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task LocalPrimaryIsRereadAndMissingSourceFallsBackWithWarning()
    {
        var manifestPath = Path.Combine(_root, "manifest.yaml");
        await File.WriteAllTextAsync(manifestPath, "One:\n  files:\n    '<home>/one.sav': {}\n");
        var service = new LudusaviManifestCacheService(Path.Combine(_root, "cache"));
        var first = await service.ImportAndCompileAsync(manifestPath, null, null, null, CancellationToken.None);

        await File.WriteAllTextAsync(manifestPath, "Two:\n  files:\n    '<home>/two.sav': {}\n");
        var changed = await service.EnsureCurrentAsync(null, null, null, CancellationToken.None);
        Assert.IsNotNull(changed);
        Assert.AreNotEqual(first.Metadata.GenerationId, changed.Value.Metadata.GenerationId);
        Assert.AreEqual("Two", changed.Value.Index.Games.Single().DisplayName);

        File.Delete(manifestPath);
        var missing = await service.EnsureCurrentAsync(null, null, null, CancellationToken.None);
        Assert.IsNotNull(missing);
        Assert.AreEqual(changed.Value.Metadata.GenerationId, missing.Value.Metadata.GenerationId);
        Assert.IsTrue(missing.Value.Metadata.Warnings.Any(warning => warning.Contains("cached copy", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task CorruptCurrentFallsBackToPreviousAndRepairsPointer()
    {
        var manifestPath = Path.Combine(_root, "manifest.yaml");
        var cacheRoot = Path.Combine(_root, "cache");
        var service = new LudusaviManifestCacheService(cacheRoot);
        await File.WriteAllTextAsync(manifestPath, "One:\n  files:\n    '<home>/one.sav': {}\n");
        var first = await service.ImportAndCompileAsync(manifestPath, null, null, null, CancellationToken.None);
        await File.WriteAllTextAsync(manifestPath, "Two:\n  files:\n    '<home>/two.sav': {}\n");
        var second = await service.ImportAndCompileAsync(manifestPath, null, null, null, CancellationToken.None);
        CorruptIndex(cacheRoot, second.Metadata.GenerationId);

        var loaded = await service.LoadCurrentAsync(CancellationToken.None);
        var pointer = JsonSerializer.Deserialize<LudusaviManifestPointer>(
            await File.ReadAllTextAsync(Path.Combine(cacheRoot, "current.json")));

        Assert.IsNotNull(loaded);
        Assert.AreEqual(first.Metadata.GenerationId, loaded.Value.Metadata.GenerationId);
        Assert.AreEqual(first.Metadata.GenerationId, pointer!.CurrentGenerationId);
        Assert.AreEqual(string.Empty, pointer.PreviousGenerationId);
    }

    [TestMethod]
    public async Task BothCorruptGenerationsRebuildFromCachedPrimary()
    {
        var manifestPath = Path.Combine(_root, "manifest.yaml");
        var cacheRoot = Path.Combine(_root, "cache");
        var service = new LudusaviManifestCacheService(cacheRoot);
        await File.WriteAllTextAsync(manifestPath, "One:\n  files:\n    '<home>/one.sav': {}\n");
        var first = await service.ImportAndCompileAsync(manifestPath, null, null, null, CancellationToken.None);
        await File.WriteAllTextAsync(manifestPath, "Two:\n  files:\n    '<home>/two.sav': {}\n");
        var second = await service.ImportAndCompileAsync(manifestPath, null, null, null, CancellationToken.None);
        CorruptIndex(cacheRoot, first.Metadata.GenerationId);
        CorruptIndex(cacheRoot, second.Metadata.GenerationId);

        var rebuilt = await service.EnsureCurrentAsync(null, null, null, CancellationToken.None);

        Assert.IsNotNull(rebuilt);
        Assert.AreEqual(second.Metadata.GenerationId, rebuilt.Value.Metadata.GenerationId);
        Assert.AreEqual("Two", rebuilt.Value.Index.Games.Single().DisplayName);
        Assert.IsNotNull(await service.LoadCurrentAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task CorruptDeterministicGenerationIsRewrittenInsteadOfSkipped()
    {
        var manifestPath = Path.Combine(_root, "manifest.yaml");
        var cacheRoot = Path.Combine(_root, "cache");
        var service = new LudusaviManifestCacheService(cacheRoot);
        await File.WriteAllTextAsync(manifestPath, "Game:\n  files:\n    '<home>/save.sav': {}\n");
        var imported = await service.ImportAndCompileAsync(manifestPath, null, null, null, CancellationToken.None);
        CorruptIndex(cacheRoot, imported.Metadata.GenerationId);

        var rebuilt = await service.EnsureCurrentAsync(null, null, null, CancellationToken.None);

        Assert.IsNotNull(rebuilt);
        Assert.AreEqual(imported.Metadata.GenerationId, rebuilt.Value.Metadata.GenerationId);
        Assert.IsNotNull(await service.LoadCurrentAsync(CancellationToken.None));
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
            JsonSerializer.Serialize(new LudusaviManifestPointer { CurrentGenerationId = legacyGenerationId }));
        await File.WriteAllTextAsync(Path.Combine(legacyRoot, "index.v2.json.gz"), "legacy");
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
            "index.v3.json.gz")));
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

    private static HttpResponseMessage Response(HttpStatusCode status, string content, string etag)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(content) };
        response.Headers.ETag = new EntityTagHeaderValue(etag);
        return response;
    }

    private static void CorruptIndex(string cacheRoot, string generationId) =>
        File.WriteAllText(Path.Combine(cacheRoot, "generations", generationId, "index.v3.json.gz"), "corrupt");
}

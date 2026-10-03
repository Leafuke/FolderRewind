using FolderRewind.History.Domain;
using FolderRewind.History.Migration;
using FolderRewind.History.Application;
using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass]
public sealed class LegacyHistoryMigrationTests
{
    [TestMethod]
    public void ShuffledLegacyFactsProduceIdenticalObjectsAndBootstrapVector()
    {
        var configId = new HistoryConfigId("dbcd2bc7-f203-40b7-a69f-5d372244c0d3");
        var sourceId = new SourceId(Guid.Parse("f577614c-c785-4cc9-b02a-3803db77d0ef"));
        var source = new LegacyMigrationSourceSnapshot(
            sourceId,
            "C:\\Games\\Save",
            "Save",
            "D:\\Backups\\Save");
        var timestamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
        var first = Entry(sourceId, "[Full][A].7z", timestamp, "first");
        var second = Entry(sourceId, "[Full][B].7z", timestamp, "second");
        var builder = new LegacyHistoryMigrationBuilder(fileExists: _ => true);

        var forward = builder.Build(new LegacyHistoryMigrationInput(
            "D:\\Config",
            configId,
            [source],
            [first, second]));
        var reversed = builder.Build(new LegacyHistoryMigrationInput(
            "D:\\Config",
            configId,
            [source],
            [second, first]));

        CollectionAssert.AreEqual(ObjectDefinitions(forward), ObjectDefinitions(reversed));
        Assert.AreNotEqual(forward.Packs[0].PackId, reversed.Packs[0].PackId);
        Assert.AreNotEqual(forward.Packs[0].TransactionId, reversed.Packs[0].TransactionId);
        CollectionAssert.AreEqual(
            forward.Workspace.SourceBaselines.ToArray(),
            reversed.Workspace.SourceBaselines.ToArray());
    }

    [TestMethod]
    public async Task RepeatedImportsWithRandomContainersKeepOneDefinitionPerFact()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindMigration", Guid.NewGuid().ToString("N"));
        try
        {
            var input = Input(root);
            var builder = new LegacyHistoryMigrationBuilder(fileExists: _ => true);
            var first = builder.Build(input); var second = builder.Build(input);
            CollectionAssert.AreEqual(ObjectDefinitions(first), ObjectDefinitions(second));
            using var repository = new FileHistoryRepository(input.ConfigId, new(Path.Combine(root, "import")));
            await repository.InitializeAsync();
            var codec = new HistoryPackCodec();
            var encoded = first.Packs.Select(pack => (ReadOnlyMemory<byte>)codec.Encode(pack)).ToArray();
            await repository.ImportAsync(encoded);
            await repository.ImportAsync(encoded);
            Assert.HasCount(first.Packs.Length, await repository.ReadAllPacksAsync());
            await repository.ImportAsync(second.Packs.Select(pack => (ReadOnlyMemory<byte>)codec.Encode(pack)));
            await using var runtime = new HistoryRuntime(repository);
            await runtime.InitializeAsync();
            Assert.HasCount(first.Packs.SelectMany(p => p.Objects).Count(o => o.Kind == HistoryObjectKinds.SourceVersion),
                await runtime.Query.GetAllVersionsAsync());
            var definitions = (await repository.ReadAllPacksAsync()).SelectMany(p => p.Pack.Objects)
                .GroupBy(o => (o.Kind, o.Id)).ToArray();
            Assert.HasCount(ObjectDefinitions(first).Length, definitions);
            Assert.IsTrue(definitions.All(group => group.Select(o => o.PayloadHash).Distinct().Count() == 1));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MigrationRetryReusesRepositoryAfterSuccessfulOrFailedBinding(bool failBinding)
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindMigration", Guid.NewGuid().ToString("N"));
        try
        {
            var input = Input(root);
            var service = new LegacyHistoryMigrationService(new(fileExists: _ => true));
            var first = await service.MigrateAsync(input, (_, _) => failBinding
                ? Task.FromException(new IOException("Injected binding failure")) : Task.CompletedTask);
            first.Repository?.Dispose();
            Assert.AreEqual(failBinding ? LegacyHistoryMigrationStatus.BindingPersistenceFailed
                : LegacyHistoryMigrationStatus.MigratedAndBound, first.Status, first.Diagnostic);
            var paths = HistoryRepositoryPaths.ForConfigDirectory(root, input.ConfigId);
            using var before = new FileHistoryRepository(input.ConfigId, paths);
            await before.InitializeAsync();
            var packs = await before.ReadAllPacksAsync();
            var descriptor = File.ReadAllBytes(paths.DescriptorPath);
            // Changed Legacy input must not overwrite a repository that already crossed the cutover.
            var changed = new LegacyHistoryMigrationInput(root, input.ConfigId, input.Sources,
                input.Entries.Concat([Entry(input.Sources[0].SourceId, "[Full][new].7z",
                    new DateTime(2026, 1, 3), "new")]));
            var retry = await service.MigrateAsync(changed, (_, _) => Task.CompletedTask);
            using var reopened = retry.Repository;
            Assert.AreEqual(LegacyHistoryMigrationStatus.ExistingRepositoryBound, retry.Status, retry.Diagnostic);
            CollectionAssert.AreEqual(descriptor, File.ReadAllBytes(paths.DescriptorPath));
            var after = await reopened!.ReadAllPacksAsync();
            CollectionAssert.AreEqual(packs.Select(p => p.Pack.PackId).ToArray(), after.Select(p => p.Pack.PackId).ToArray());
            foreach (var pack in packs)
                CollectionAssert.AreEqual(pack.OriginalBytes, after.Single(p => p.Pack.PackId == pack.Pack.PackId).OriginalBytes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CancellationBeforeCutoverCleansStagingAndAllowsFreshRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindMigration", Guid.NewGuid().ToString("N"));
        try
        {
            var input = Input(root);
            using var cancellation = new CancellationTokenSource();
            var service = new LegacyHistoryMigrationService(new(fileExists: _ => { cancellation.Cancel(); return true; }));
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.MigrateAsync(input,
                (_, _) => throw new AssertFailedException("Binding must follow cutover"), cancellation.Token));
            var paths = HistoryRepositoryPaths.ForConfigDirectory(root, input.ConfigId);
            Assert.IsFalse(Directory.Exists(paths.RepositoryRoot));
            var retry = await new LegacyHistoryMigrationService(new(fileExists: _ => true))
                .MigrateAsync(input, (_, _) => Task.CompletedTask);
            using var repository = retry.Repository;
            Assert.AreEqual(LegacyHistoryMigrationStatus.MigratedAndBound, retry.Status, retry.Diagnostic);
            CollectionAssert.AreEqual(ObjectDefinitions(new LegacyHistoryMigrationBuilder(fileExists: _ => true).Build(input)),
                (await repository!.ReadAllPacksAsync()).SelectMany(p => p.Pack.Objects)
                    .OrderBy(o => o.Kind, StringComparer.Ordinal).ThenBy(o => o.Id, StringComparer.Ordinal)
                    .Select(o => $"{o.Kind}|{o.Id}|{o.SchemaVersion}|{o.PayloadHash}").ToArray());
            var staging = Path.Combine(Path.GetDirectoryName(paths.RepositoryRoot)!, ".migration");
            Assert.IsFalse(Directory.Exists(staging) && Directory.EnumerateDirectories(staging).Any());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static LegacyHistoryMigrationInput Input(string root)
    {
        var source = SourceId.New();
        return new(root, new HistoryConfigId(Guid.NewGuid().ToString("N")),
            [new LegacyMigrationSourceSnapshot(source, Path.Combine(root, "source"), "Save", Path.Combine(root, "backups"))],
            [Entry(source, "[Full][A].7z", new DateTime(2026, 1, 2), "first")]);
    }

    private static LegacyHistoryEntrySnapshot Entry(
        SourceId sourceId,
        string fileName,
        DateTime timestamp,
        string comment)
        => new(
            sourceId,
            "C:\\Games\\Save",
            "Save",
            fileName,
            timestamp,
            "Full",
            comment,
            false,
            false,
            false,
            string.Empty);

    private static string[] ObjectDefinitions(LegacyHistoryMigrationBuild build)
        => build.Packs
            .SelectMany(pack => pack.Objects)
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => $"{item.Kind}|{item.Id}|{item.SchemaVersion}|{item.PayloadHash}")
            .ToArray();
}

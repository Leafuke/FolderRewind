using FolderRewind.History.Domain;
using FolderRewind.History.Migration;

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
        Assert.AreEqual(forward.BootstrapCheckpointId, reversed.BootstrapCheckpointId);
        CollectionAssert.AreEqual(
            forward.Workspace.SourceBaselines.ToArray(),
            reversed.Workspace.SourceBaselines.ToArray());
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

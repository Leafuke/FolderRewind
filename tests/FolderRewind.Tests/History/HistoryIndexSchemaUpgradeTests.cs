using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Index;
using FolderRewind.History.Storage;
using Microsoft.Data.Sqlite;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryIndexSchemaUpgradeTests
{
    private string _root = null!;
    private HistoryConfigId _config;
    private readonly HistoryPackCodec _codec = new();
    private string IndexPath => Path.Combine(_root, "index", "history-index.db");

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindIndexUpgradeTests", Guid.NewGuid().ToString("N"));
        _config = new(Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task RebuildAtLongDirectoryDoesNotExhaustSqliteJournalPathBudget()
    {
        var directory = Path.Combine(_root, new string('x', Math.Max(1, 201 - _root.Length - 1)));
        using var index = new HistoryIndex(Path.Combine(directory, "history-index.db"));
        await index.RebuildAsync([]);
        Assert.AreEqual(5, await index.GetSchemaVersionAsync());
        Assert.AreEqual(0, await index.GetIndexedPackCountAsync());
        CollectionAssert.AreEqual(new[] { "history-index.db" }, Directory.GetFiles(directory).Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    [DataRow("initialize", 3)]
    [DataRow("commit", 3)]
    [DataRow("query", 3)]
    [DataRow("initialize", 4)]
    [DataRow("commit", 4)]
    [DataRow("query", 4)]
    public async Task SchemaUpgradePreservesObservationsAndFactsAtEveryRuntimeEntry(string entry, int version)
    {
        await SeedAsync();
        await using var runtime = Runtime();
        if (entry != "initialize") await runtime.InitializeReadableAsync();
        DowngradeTo(version);

        if (entry == "initialize") await runtime.InitializeReadableAsync();
        else if (entry == "commit") await runtime.Repository.CommitAsync(Pack());
        else await runtime.EnsureIndexCurrentAsync();

        Assert.AreEqual(5, await runtime.Index.GetSchemaVersionAsync());
        Assert.AreEqual(entry == "commit" ? 2 : 1, await runtime.Index.GetIndexedPackCountAsync());
        Assert.AreEqual(1L, Scalar("SELECT COUNT(*) FROM ReplicaObservations WHERE ReplicaKey='upgrade-witness'"),
            "An in-place upgrade must retain observations that a rebuild would discard.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA index_info(IX_BranchUpdateParents_Parent)";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(2));
        CollectionAssert.AreEqual(new[] { "ParentUpdateId", "UpdateId" }, columns);
    }

    [TestMethod]
    [DataRow(2, false)]
    [DataRow(3, true)]
    public async Task UnsupportedOrIncompleteSchemaIsRebuiltFromPacks(int version, bool incomplete)
    {
        await SeedAsync();
        DowngradeTo3();
        Execute($"PRAGMA user_version={version}");
        if (incomplete) Execute("DROP TABLE CheckpointParents");
        await using var runtime = Runtime();
        await runtime.InitializeReadableAsync();
        Assert.AreEqual(5, await runtime.Index.GetSchemaVersionAsync());
        Assert.AreEqual(1, await runtime.Index.GetObjectCountAsync());
        Assert.AreEqual(0L, Scalar("SELECT COUNT(*) FROM ReplicaObservations"));
    }

    [TestMethod]
    public async Task CorruptDatabaseIsRebuiltFromPacks()
    {
        await SeedAsync();
        File.WriteAllText(IndexPath, "not a SQLite database");
        await using var runtime = Runtime();
        await runtime.InitializeReadableAsync();
        Assert.AreEqual(5, await runtime.Index.GetSchemaVersionAsync());
        Assert.AreEqual(1, await runtime.Index.GetObjectCountAsync());
    }

    [TestMethod]
    public async Task CancelledUpgradeDoesNotPublishNewSchema()
    {
        await SeedAsync();
        DowngradeTo3();
        using var index = new HistoryIndex(IndexPath);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => index.EnsureCurrentSchemaAsync(cancelled.Token));
        Assert.AreEqual(3, await index.GetSchemaVersionAsync());
        Assert.AreEqual(0L, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name='IX_BranchUpdateParents_Parent'"));
        Assert.AreEqual(1L, Scalar("SELECT COUNT(*) FROM ReplicaObservations"));
        Assert.IsTrue(await index.EnsureCurrentSchemaAsync(default));
    }

    [TestMethod]
    [DataRow("initialize")]
    [DataRow("commit")]
    public async Task FailedUpgradeKeepsSchema3AndRuntimeRebuildsInvalidCache(string entry)
    {
        await SeedAsync();
        await using var runtime = Runtime();
        if (entry == "commit") await runtime.InitializeReadableAsync();
        DowngradeTo3();
        Execute("CREATE INDEX IX_BranchUpdateParents_Parent ON BranchUpdates(Name)");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => runtime.Index.EnsureCurrentSchemaAsync(default));
        Assert.AreEqual(3, await runtime.Index.GetSchemaVersionAsync());
        Assert.AreEqual(1L, Scalar("SELECT COUNT(*) FROM ReplicaObservations"));
        if (entry == "commit") await runtime.Repository.CommitAsync(Pack());
        else await runtime.InitializeReadableAsync();
        Assert.AreEqual(5, await runtime.Index.GetSchemaVersionAsync());
        Assert.AreEqual(entry == "commit" ? 2 : 1, await runtime.Index.GetObjectCountAsync());
        Assert.AreEqual(0L, Scalar("SELECT COUNT(*) FROM ReplicaObservations"));
    }

    [TestMethod]
    public async Task UpgradedIndexPreservesDivergentAndCrossBranchTipsDuringUnorderedImport()
    {
        await SeedAsync();
        await using var runtime = Runtime();
        await runtime.InitializeReadableAsync();
        var version = (await runtime.Query.GetAllVersionsAsync()).Single();
        var checkpoint = new SourceCheckpoint(CheckpointId.New(), _config, DateTimeOffset.UtcNow, null,
            HistoryProvenance.Native("test"), [new(version.SourceId, version.SourceDescriptorSnapshot,
                version.VersionId, CheckpointSourceDisposition.Captured)]);
        var main = BranchId.New();
        var fork = BranchId.New();
        var root = new BranchUpdate(BranchUpdateId.New(), main, [], "main", checkpoint.CheckpointId, false,
            DateTimeOffset.UtcNow, BranchUpdateReason.Created, version.SourceId);
        var left = new BranchUpdate(BranchUpdateId.New(), main, [root.UpdateId], "main", checkpoint.CheckpointId, false,
            DateTimeOffset.UtcNow, BranchUpdateReason.Backup, version.SourceId);
        var right = new BranchUpdate(BranchUpdateId.New(), main, [root.UpdateId], "main", checkpoint.CheckpointId, false,
            DateTimeOffset.UtcNow, BranchUpdateReason.Backup, version.SourceId);
        var otherBranch = new BranchUpdate(BranchUpdateId.New(), fork, [right.UpdateId], "fork", checkpoint.CheckpointId, false,
            DateTimeOffset.UtcNow, BranchUpdateReason.Created, version.SourceId);
        DowngradeTo3();
        var bytes = new object[] { otherBranch, left, right, root, checkpoint }.Select(value =>
            (ReadOnlyMemory<byte>)_codec.Encode(new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, [_codec.CreateObject(value)]))).ToArray();
        await runtime.Repository.ImportAsync(bytes);
        CollectionAssert.AreEquivalent(new[] { left.UpdateId, right.UpdateId }, (await runtime.Index.GetBranchTipsAsync(main)).ToArray());
        CollectionAssert.AreEqual(new[] { otherBranch.UpdateId }, (await runtime.Index.GetBranchTipsAsync(fork)).ToArray());
        using var rebuilt = new HistoryIndex(Path.Combine(_root, "rebuilt.db"));
        await rebuilt.RebuildAsync(await runtime.Repository.ReadAllPacksAsync());
        CollectionAssert.AreEquivalent((await rebuilt.GetBranchTipsAsync(main)).ToArray(), (await runtime.Index.GetBranchTipsAsync(main)).ToArray());
        CollectionAssert.AreEquivalent((await rebuilt.GetBranchTipsAsync(fork)).ToArray(), (await runtime.Index.GetBranchTipsAsync(fork)).ToArray());
    }

    private async Task SeedAsync()
    {
        await using var runtime = Runtime();
        await runtime.InitializeReadableAsync();
        await runtime.Repository.CommitAsync(Pack());
        await runtime.Index.UpsertObservationAsync(new("upgrade-witness", ReplicaAvailabilityObservation.Available,
            ReplicaIntegrityObservation.Unknown, DateTimeOffset.UtcNow, "preserved by additive upgrade"));
    }

    private HistoryRuntime Runtime() => new(new FileHistoryRepository(_config, new(_root)));
    private HistoryCommitPack Pack()
    {
        var version = new SourceVersion(VersionId.New(), _config, SourceId.New(), Array.Empty<VersionId>(),
            DateTimeOffset.UtcNow, null, CaptureScope.FullSource, CaptureOutcome.Captured, Array.Empty<HistoryDiagnostic>(),
            new("source", _root), null, HistoryProvenance.Native("test"));
        return new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, [_codec.CreateObject(version)]);
    }

    private void DowngradeTo3() => DowngradeTo(3);
    private void DowngradeTo(int version)
    {
        Execute("""
            ALTER TABLE Objects RENAME TO Objects5;
            CREATE TABLE Objects(Kind TEXT NOT NULL, ObjectId TEXT NOT NULL, SchemaVersion INTEGER NOT NULL, PayloadHash TEXT NOT NULL, PackId TEXT NOT NULL, IsSupported INTEGER NOT NULL, PayloadJson TEXT NOT NULL, PRIMARY KEY(Kind, ObjectId));
            INSERT INTO Objects SELECT Kind,ObjectId,SchemaVersion,PayloadHash,'legacy-fixture',1,'{}' FROM Objects5;
            DROP TABLE Objects5;
            CREATE TABLE VersionParents(VersionId TEXT NOT NULL, ParentVersionId TEXT NOT NULL, Ordinal INTEGER NOT NULL, PRIMARY KEY(VersionId, Ordinal));
            CREATE TABLE CheckpointParents(CheckpointId TEXT NOT NULL, ParentCheckpointId TEXT NOT NULL, Ordinal INTEGER NOT NULL, PRIMARY KEY(CheckpointId, Ordinal));
            CREATE TABLE CheckpointSources(CheckpointId TEXT NOT NULL, SourceId TEXT NOT NULL, VersionId TEXT NULL, Disposition INTEGER NOT NULL, Ordinal INTEGER NOT NULL, PRIMARY KEY(CheckpointId, SourceId));
            CREATE TABLE AnnotationParents(UpdateId TEXT NOT NULL, ParentUpdateId TEXT NOT NULL, Ordinal INTEGER NOT NULL, PRIMARY KEY(UpdateId, Ordinal));
            CREATE TABLE ReplicaLifecycleParents(UpdateId TEXT NOT NULL, ParentUpdateId TEXT NOT NULL, Ordinal INTEGER NOT NULL, PRIMARY KEY(UpdateId, Ordinal));
            CREATE TABLE MaterializationPolicyParents(UpdateId TEXT NOT NULL, ParentUpdateId TEXT NOT NULL, Ordinal INTEGER NOT NULL, PRIMARY KEY(UpdateId, Ordinal));
            """);
        if (version == 3) Execute("DROP INDEX IX_BranchUpdateParents_Parent");
        Execute($"PRAGMA user_version={version}");
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = IndexPath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
    private void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private long Scalar(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}

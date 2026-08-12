using FolderRewind.Plugin.Runtime.Configuration;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class ConfigFileMigrationServiceTests
{
    private readonly List<string> _temporaryDirectories = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var directory in _temporaryDirectories)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void MissingConfigurationDoesNotCreateFiles()
    {
        var directory = CreateDirectory();
        var path = Path.Combine(directory, "config.json");

        var result = new ConfigFileMigrationService().Prepare(path);

        Assert.AreEqual(ConfigFilePreparationStatus.Missing, result.Status);
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(directory));
    }

    [TestMethod]
    [DataRow("malformed.json", "config_json_malformed")]
    [DataRow("newer-schema.json", "config_schema_newer_than_host")]
    public void UnreadableConfigurationEntersRecoveryWithoutWriting(string fixture, string expectedCode)
    {
        var (path, original) = CreateConfig(fixture);

        var result = new ConfigFileMigrationService().Prepare(path);

        Assert.AreEqual(ConfigFilePreparationStatus.RecoveryRequired, result.Status);
        Assert.AreEqual(expectedCode, result.Diagnostic!.Code);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        Assert.AreEqual(1, Directory.EnumerateFiles(Path.GetDirectoryName(path)!).Count());
    }

    [TestMethod]
    public void SuccessfulMigrationCreatesFsyncedRecoveryCopyAndValidatesFormalFile()
    {
        var (path, original) = CreateConfig("legacy-representative.json");
        var service = new ConfigFileMigrationService(
            utcNow: () => new DateTimeOffset(2026, 8, 12, 1, 2, 3, TimeSpan.Zero));

        var result = service.Prepare(path);

        Assert.AreEqual(ConfigFilePreparationStatus.Migrated, result.Status);
        Assert.IsNotNull(result.RecoveryCopyPath);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(result.RecoveryCopyPath));
        Assert.AreEqual(ConfigDocumentKind.Current, ConfigDocumentParser.Parse(File.ReadAllBytes(path)).Kind);
        Assert.AreEqual(2, Directory.EnumerateFiles(Path.GetDirectoryName(path)!).Count());
    }

    [TestMethod]
    public void CurrentConfigurationIsReadOnlyAtTheSchemaGate()
    {
        var (path, _) = CreateConfig("legacy-representative.json");
        var first = new ConfigFileMigrationService().Prepare(path);
        var migratedBytes = File.ReadAllBytes(path);
        var filesBefore = Directory.EnumerateFiles(Path.GetDirectoryName(path)!).Order().ToArray();

        var second = new ConfigFileMigrationService().Prepare(path);

        Assert.AreEqual(ConfigFilePreparationStatus.Current, second.Status);
        CollectionAssert.AreEqual(migratedBytes, File.ReadAllBytes(path));
        CollectionAssert.AreEqual(filesBefore, Directory.EnumerateFiles(Path.GetDirectoryName(path)!).Order().ToArray());
        Assert.IsNotNull(first.RecoveryCopyPath);
    }

    [TestMethod]
    [DataRow(ConfigMigrationStage.RecoveryCopyFlushed)]
    [DataRow(ConfigMigrationStage.TemporaryFileFlushed)]
    [DataRow(ConfigMigrationStage.TemporaryFileValidated)]
    [DataRow(ConfigMigrationStage.AtomicReplaceCompleted)]
    [DataRow(ConfigMigrationStage.FormalFileValidated)]
    public void FaultAtEveryCommitStageLeavesOriginalFormalFile(ConfigMigrationStage stage)
    {
        var (path, original) = CreateConfig("legacy-representative.json");
        var observer = new ThrowingObserver(stage);

        var result = new ConfigFileMigrationService(observer: observer).Prepare(path);

        Assert.AreEqual(ConfigFilePreparationStatus.RecoveryRequired, result.Status, stage.ToString());
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path), stage.ToString());
        Assert.IsFalse(
            Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(),
            stage.ToString());
    }

    [TestMethod]
    public void RecoveryCopiesAreDiscoverableWithoutMutatingConfiguration()
    {
        var (path, original) = CreateConfig("legacy-representative.json");
        var migrated = new ConfigFileMigrationService().Prepare(path);
        var bytesBefore = File.ReadAllBytes(path);

        var copies = ConfigFileMigrationService.ListRecoveryCopies(path);

        Assert.HasCount(1, copies);
        Assert.AreEqual(migrated.RecoveryCopyPath, copies[0]);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(copies[0]));
        CollectionAssert.AreEqual(bytesBefore, File.ReadAllBytes(path));
    }

    private (string Path, byte[] Original) CreateConfig(string fixture)
    {
        var directory = CreateDirectory();
        var path = Path.Combine(directory, "config.json");
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        File.WriteAllBytes(path, bytes);
        return (path, bytes);
    }

    private string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "FolderRewind.Plugin.Runtime.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _temporaryDirectories.Add(path);
        return path;
    }

    private sealed class ThrowingObserver(ConfigMigrationStage target) : IConfigMigrationObserver
    {
        public void OnStage(ConfigMigrationStage stage)
        {
            if (stage == target)
            {
                throw new IOException($"Injected failure at {stage}.");
            }
        }
    }
}

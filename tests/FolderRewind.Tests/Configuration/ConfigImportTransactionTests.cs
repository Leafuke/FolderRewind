using FolderRewind.Services;
using System.Text;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class ConfigImportTransactionTests
{
    private string _root = null!;
    private string _path = null!;
    [TestInitialize] public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindConfigImport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "config.json");
        File.WriteAllText(_path, "{\"name\":\"old\"}");
    }
    [TestCleanup] public void Cleanup() => Directory.Delete(_root, true);
    private static string? Validate(byte[] bytes)
    {
        try { using var json = JsonDocument.Parse(bytes); return json.RootElement.TryGetProperty("name", out _) ? null : "Missing name"; }
        catch (JsonException ex) { return ex.Message; }
    }
    private static byte[] Candidate => Encoding.UTF8.GetBytes("{\"name\":\"new\"}");

    [TestMethod] public async Task OldQueuedSaveCannotOverwritePreparedOrActivatedImport()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var writer = new ConfigWriteCoordinator(async (bytes, _) =>
        {
            started.SetResult(); await release.Task;
            AtomicFileService.Write(_path, stream => stream.Write(bytes.Span));
        });
        var save = writer.EnqueueAsync(Encoding.UTF8.GetBytes("{\"name\":\"latest-old\"}"), false);
        await started.Task;
        var store = new ConfigImportTransactionStore(_path);
        Assert.AreEqual(ConfigImportStatus.StagedRequiresRestart, store.Stage(Candidate, Validate).Status);
        Assert.AreEqual("{\"name\":\"old\"}", File.ReadAllText(_path));
        release.SetResult(); await save; await writer.FlushAsync();
        Assert.IsTrue(store.BeginActivation(Validate)); store.CommitActivation();
        CollectionAssert.AreEqual(Candidate, File.ReadAllBytes(_path));
        Assert.AreEqual(ConfigImportStatus.Applied, store.Result.Status);
    }

    [TestMethod] public void InterruptedStartupRollsBackToLatestConfiguration()
    {
        var store = new ConfigImportTransactionStore(_path);
        store.Stage(Candidate, Validate);
        File.WriteAllText(_path, "{\"name\":\"latest-old\"}");
        Assert.IsTrue(store.BeginActivation(Validate));
        // No commit: emulate losing the process after the replacement.
        store = new ConfigImportTransactionStore(_path);
        Assert.IsFalse(store.BeginActivation(Validate));
        Assert.AreEqual(ConfigImportStatus.RolledBack, store.Result.Status);
        Assert.AreEqual("{\"name\":\"latest-old\"}", File.ReadAllText(_path));
    }

    [TestMethod] public void ActivationFailureAndCanceledPreparationLeaveOldConfiguration()
    {
        var store = new ConfigImportTransactionStore(_path);
        store.Stage(Candidate, Validate);
        store.FaultInjector = phase => { if (phase == "Replaced") throw new IOException("Injected readback failure"); };
        Assert.IsFalse(store.BeginActivation(Validate));
        Assert.AreEqual(ConfigImportStatus.RolledBack, store.Result.Status);
        Assert.AreEqual("{\"name\":\"old\"}", File.ReadAllText(_path));
        store.Stage(Candidate, Validate); store.Cancel();
        Assert.IsFalse(store.BeginActivation(Validate));
        Assert.AreEqual(ConfigImportStatus.Canceled, store.Result.Status);
    }

    [TestMethod] public void DamagedCandidateDoesNotReplaceActiveConfiguration()
    {
        var store = new ConfigImportTransactionStore(_path);
        store.Stage(Candidate, Validate);
        File.WriteAllText(Directory.GetFiles(Path.Combine(_root, "migration", "config-import"), "candidate-*.json").Single(), "corrupt");
        Assert.IsFalse(store.BeginActivation(Validate));
        Assert.AreEqual(ConfigImportStatus.Failed, store.Result.Status);
        Assert.AreEqual("{\"name\":\"old\"}", File.ReadAllText(_path));
    }

    [TestMethod] public void SetupConfirmationBelongsToOneImportEvenWhenBytesAreIdentical()
    {
        var id = Guid.NewGuid().ToString("N");
        var store = new ConfigImportTransactionStore(_path);
        store.Stage(Candidate, Validate, [id]);
        Assert.IsTrue(store.BeginActivation(Validate)); store.CommitActivation();
        Assert.IsTrue(MigrationSetupStateStore.IsPending(_root, id));
        MigrationSetupStateStore.Confirm(_root, id);
        Assert.IsFalse(MigrationSetupStateStore.IsPending(_root, id));
        store.Stage(Candidate, Validate, [id]);
        Assert.IsTrue(store.BeginActivation(Validate)); store.CommitActivation();
        Assert.IsTrue(MigrationSetupStateStore.IsPending(_root, id));
    }

    [TestMethod] public void ASecondCandidateCannotRemoveActiveSetupGatesAndRollbackRestoresThem()
    {
        var first = Guid.NewGuid().ToString("N");
        var second = Guid.NewGuid().ToString("N");
        var store = new ConfigImportTransactionStore(_path);
        store.Stage(Candidate, Validate, [first]);
        Assert.IsTrue(store.BeginActivation(Validate)); store.CommitActivation();
        store.Stage(Encoding.UTF8.GetBytes("{\"name\":\"second\"}"), Validate, [second]);
        Assert.IsTrue(MigrationSetupStateStore.IsPending(_root, first));
        Assert.IsFalse(MigrationSetupStateStore.IsPending(_root, second));
        Assert.IsTrue(store.BeginActivation(Validate));
        Assert.IsTrue(MigrationSetupStateStore.IsPending(_root, second));
        Assert.IsFalse(new ConfigImportTransactionStore(_path).BeginActivation(Validate));
        Assert.IsTrue(MigrationSetupStateStore.IsPending(_root, first));
        Assert.IsFalse(MigrationSetupStateStore.IsPending(_root, second));
        CollectionAssert.AreEqual(Candidate, File.ReadAllBytes(_path));
        Assert.HasCount(2, store.SafetyCopies);
    }
}

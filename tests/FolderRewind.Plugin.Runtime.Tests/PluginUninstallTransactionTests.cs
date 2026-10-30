using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Packaging;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class PluginUninstallTransactionTests
{
    private static readonly PluginId PluginId = new("com.example.uninstall");

    [TestMethod]
    public async Task SuccessfulUninstallCommitsConfigThenDeletesQuarantine()
    {
        using var scope = new TransactionScope();
        var state = "original";
        var coordinator = scope.CreateCoordinator(
            commit: () => state = "removed",
            restore: () => state = "original");

        var result = await coordinator.ExecuteAsync(scope.Request());

        Assert.AreEqual(OperationOutcome.Success, result.Outcome);
        Assert.AreEqual(PluginUninstallTransactionPhase.Completed, result.Phase);
        Assert.AreEqual("removed", state);
        Assert.IsFalse(Directory.Exists(scope.CodePath));
        Assert.IsFalse(Directory.Exists(scope.DataPath));
        Assert.IsEmpty(await coordinator.RecoverAsync());
    }

    [TestMethod]
    [DataRow(PluginUninstallFaultPoint.AfterPrepared)]
    [DataRow(PluginUninstallFaultPoint.AfterCodeQuarantined)]
    [DataRow(PluginUninstallFaultPoint.AfterDataQuarantined)]
    [DataRow(PluginUninstallFaultPoint.AfterConfigurationCommit)]
    public async Task PrecommitFaultRollsBackCodeDataAndConfiguration(PluginUninstallFaultPoint faultPoint)
    {
        using var scope = new TransactionScope();
        var state = "original";
        var coordinator = scope.CreateCoordinator(
            commit: () => state = "removed",
            restore: () => state = "original",
            faultInjector: point =>
            {
                if (point == faultPoint) throw new IOException("injected");
            });

        var error = await Assert.ThrowsExactlyAsync<PluginUninstallTransactionException>(
            () => coordinator.ExecuteAsync(scope.Request()).AsTask());

        Assert.IsFalse(error.RecoveryRequired);
        Assert.AreEqual("original", state);
        Assert.IsTrue(Directory.Exists(scope.CodePath));
        Assert.IsTrue(Directory.Exists(scope.DataPath));
    }

    [TestMethod]
    public async Task ConfigSaveFailureRollsBackQuarantineAndInMemoryState()
    {
        using var scope = new TransactionScope();
        var state = "original";
        var coordinator = scope.CreateCoordinator(
            commit: () =>
            {
                state = "removed";
                throw new IOException("save failed");
            },
            restore: () => state = "original");

        var error = await Assert.ThrowsExactlyAsync<PluginUninstallTransactionException>(
            () => coordinator.ExecuteAsync(scope.Request()).AsTask());

        Assert.IsFalse(error.RecoveryRequired);
        Assert.AreEqual("original", state);
        Assert.IsTrue(Directory.Exists(scope.CodePath));
        Assert.IsTrue(Directory.Exists(scope.DataPath));
    }

    [TestMethod]
    [DataRow(PluginUninstallFaultPoint.AfterPrepared)]
    [DataRow(PluginUninstallFaultPoint.AfterCodeQuarantined)]
    [DataRow(PluginUninstallFaultPoint.AfterDataQuarantined)]
    [DataRow(PluginUninstallFaultPoint.AfterConfigurationCommit)]
    public async Task RestartRecoveryRollsBackAnyUncommittedCrash(PluginUninstallFaultPoint faultPoint)
    {
        using var scope = new TransactionScope();
        var state = "original";
        var crashing = scope.CreateCoordinator(
            commit: () => state = "removed",
            restore: () => state = "original",
            faultInjector: point =>
            {
                if (point == faultPoint) throw new PluginUninstallSimulatedCrashException("crash");
            });
        await Assert.ThrowsExactlyAsync<PluginUninstallSimulatedCrashException>(
            () => crashing.ExecuteAsync(scope.Request()).AsTask());

        var recovery = scope.CreateCoordinator(
            commit: () => state = "removed",
            restore: () => state = "original");
        var recovered = await recovery.RecoverAsync();

        Assert.HasCount(1, recovered);
        Assert.AreEqual(OperationOutcome.SuccessWithWarnings, recovered[0].Outcome);
        Assert.AreEqual("original", state);
        Assert.IsTrue(Directory.Exists(scope.CodePath));
        Assert.IsTrue(Directory.Exists(scope.DataPath));
        Assert.IsEmpty(await recovery.RecoverAsync());
    }

    [TestMethod]
    public async Task RestartRecoveryCompletesCleanupAfterCommittedCrash()
    {
        using var scope = new TransactionScope();
        var state = "original";
        var crashing = scope.CreateCoordinator(
            commit: () => state = "removed",
            restore: () => state = "original",
            faultInjector: point =>
            {
                if (point == PluginUninstallFaultPoint.AfterConfigCommitted)
                    throw new PluginUninstallSimulatedCrashException("crash");
            });
        await Assert.ThrowsExactlyAsync<PluginUninstallSimulatedCrashException>(
            () => crashing.ExecuteAsync(scope.Request()).AsTask());

        var recovery = scope.CreateCoordinator(
            commit: () => state = "removed",
            restore: () => state = "original");
        var recovered = await recovery.RecoverAsync();

        Assert.HasCount(1, recovered);
        Assert.AreEqual(OperationOutcome.Success, recovered[0].Outcome);
        Assert.AreEqual("removed", state);
        Assert.IsFalse(Directory.Exists(scope.CodePath));
        Assert.IsFalse(Directory.Exists(scope.DataPath));
        Assert.IsEmpty(await recovery.RecoverAsync());
    }

    [TestMethod]
    public async Task FinalCleanupFailureReturnsWarningAndIsRetriedIdempotently()
    {
        using var scope = new TransactionScope();
        var state = "original";
        var failing = scope.CreateCoordinator(
            commit: () => state = "removed",
            restore: () => state = "original",
            faultInjector: point =>
            {
                if (point == PluginUninstallFaultPoint.BeforeFinalCleanup)
                    throw new IOException("directory occupied");
            });

        var result = await failing.ExecuteAsync(scope.Request());

        Assert.AreEqual(OperationOutcome.SuccessWithWarnings, result.Outcome);
        Assert.AreEqual(PluginUninstallTransactionPhase.RecoveryRequired, result.Phase);
        Assert.IsTrue(Directory.Exists(result.RecoveryPath));
        Assert.AreEqual("removed", state);

        var recovery = scope.CreateCoordinator(
            commit: () => state = "removed",
            restore: () => state = "original");
        var recovered = await recovery.RecoverAsync();
        Assert.HasCount(1, recovered);
        Assert.AreEqual(OperationOutcome.Success, recovered[0].Outcome);
        Assert.IsEmpty(await recovery.RecoverAsync());
    }

    [TestMethod]
    public async Task RollbackFailureIsPersistedAsRecoveryRequired()
    {
        using var scope = new TransactionScope();
        var coordinator = scope.CreateCoordinator(
            commit: () => { },
            restore: () => throw new IOException("restore failed"),
            faultInjector: point =>
            {
                if (point == PluginUninstallFaultPoint.AfterDataQuarantined)
                    throw new IOException("uninstall failed");
            });

        var error = await Assert.ThrowsExactlyAsync<PluginUninstallTransactionException>(
            () => coordinator.ExecuteAsync(scope.Request()).AsTask());

        Assert.IsTrue(error.RecoveryRequired);
        Assert.IsTrue(Directory.Exists(error.RecoveryPath));
        var recovery = await coordinator.RecoverAsync();
        Assert.HasCount(1, recovery);
        Assert.AreEqual(OperationOutcome.Failed, recovery[0].Outcome);
    }

    [TestMethod]
    public async Task OccupiedRestoreTargetLeavesRecoverableJournal()
    {
        using var scope = new TransactionScope();
        var crashing = scope.CreateCoordinator(
            commit: () => { },
            restore: () => { },
            faultInjector: point =>
            {
                if (point == PluginUninstallFaultPoint.AfterCodeQuarantined)
                    throw new PluginUninstallSimulatedCrashException("crash");
            });
        await Assert.ThrowsExactlyAsync<PluginUninstallSimulatedCrashException>(
            () => crashing.ExecuteAsync(scope.Request()).AsTask());
        Directory.CreateDirectory(scope.CodePath);

        var recovery = scope.CreateCoordinator(commit: () => { }, restore: () => { });
        var result = await recovery.RecoverAsync();

        Assert.HasCount(1, result);
        Assert.AreEqual(OperationOutcome.Failed, result[0].Outcome);
        Assert.AreEqual(PluginUninstallTransactionPhase.RecoveryRequired, result[0].Phase);
    }

    [TestMethod]
    public async Task NestedDestructiveTargetsAreRejectedBeforeAnythingMoves()
    {
        using var scope = new TransactionScope();
        var nestedData = Path.Combine(scope.CodePath, PluginId.Value);
        Directory.CreateDirectory(nestedData);
        var coordinator = scope.CreateCoordinator(commit: () => { }, restore: () => { });
        var request = scope.Request() with { DataPath = nestedData };

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => coordinator.ExecuteAsync(request).AsTask());

        Assert.IsTrue(Directory.Exists(scope.CodePath));
        Assert.IsTrue(Directory.Exists(nestedData));
    }

    private sealed class TransactionScope : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "FolderRewind-UninstallTests",
            Guid.NewGuid().ToString("N"));

        public TransactionScope()
        {
            CodePath = Path.Combine(_root, "plugins", PluginId.Value);
            DataPath = Path.Combine(_root, "plugin-data", PluginId.Value);
            TransactionsPath = Path.Combine(_root, "transactions");
            Directory.CreateDirectory(CodePath);
            Directory.CreateDirectory(DataPath);
            File.WriteAllText(Path.Combine(CodePath, "plugin.dll"), "code");
            File.WriteAllText(Path.Combine(DataPath, "state.json"), "data");
        }

        public string CodePath { get; }
        public string DataPath { get; }
        public string TransactionsPath { get; }

        public PluginUninstallTransactionRequest Request()
            => new(PluginId, CodePath, DataPath, JsonDocument.Parse("{\"revision\":1}").RootElement.Clone());

        public PluginUninstallTransactionCoordinator CreateCoordinator(
            Action commit,
            Action restore,
            Action<PluginUninstallFaultPoint>? faultInjector = null)
            => new(
                TransactionsPath,
                new PluginUninstallTransactionCallbacks(
                    (_, _) =>
                    {
                        commit();
                        return ValueTask.CompletedTask;
                    },
                    (_, _) =>
                    {
                        restore();
                        return ValueTask.CompletedTask;
                    }),
                faultInjector is null
                    ? null
                    : (point, _) =>
                    {
                        faultInjector(point);
                        return ValueTask.CompletedTask;
                    });

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}

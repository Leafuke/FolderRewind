using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class MiniBackupControllerTests
{
    [TestMethod]
    [DataRow(OperationOutcome.Success, MiniWindowVisualState.BackupDone)]
    [DataRow(OperationOutcome.SuccessWithWarnings, MiniWindowVisualState.BackupDone)]
    [DataRow(OperationOutcome.Failed, MiniWindowVisualState.BackupFailed)]
    [DataRow(OperationOutcome.Blocked, MiniWindowVisualState.BackupFailed)]
    [DataRow(OperationOutcome.NoChanges, MiniWindowVisualState.Normal)]
    [DataRow(OperationOutcome.Canceled, MiniWindowVisualState.Normal)]
    public async Task OutcomeControlsFeedbackWithoutFalseSuccess(OperationOutcome outcome, MiniWindowVisualState state)
    {
        var controller = new MiniBackupController((_, _) => Task.FromResult(outcome), () => false, _ => Assert.Fail());
        var operation = controller.BackupAsync("");
        Assert.AreEqual(state, controller.State);
        controller.Dispose();
        await operation;
    }

    [TestMethod]
    public async Task CloseDuringBackupPreventsLateFeedback()
    {
        var completion = new TaskCompletionSource<OperationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new MiniBackupController((_, _) => completion.Task, () => false, _ => Assert.Fail());
        var events = 0;
        controller.Changed += () => events++;
        var operation = controller.BackupAsync("comment");
        controller.Dispose();
        var countAtClose = events;
        completion.SetResult(OperationOutcome.Success);
        await operation;
        Assert.AreEqual(countAtClose, events);
        Assert.IsFalse(controller.CanExecute);
    }

    [TestMethod]
    public async Task SlowBackupDoesNotOverlapAndCancellationRestoresWatchState()
    {
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        using var controller = new MiniBackupController(async (_, token) =>
        {
            calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return OperationOutcome.Success;
        }, () => true, _ => Assert.Fail());
        var operation = controller.BackupAsync("", cancellation.Token);
        await controller.BackupAsync("second");
        Assert.AreEqual(1, calls);
        cancellation.Cancel();
        await operation;
        Assert.AreEqual(MiniWindowVisualState.Changed, controller.State);
    }

    [TestMethod]
    public async Task ExceptionIsLoggedAndShownAsFailure()
    {
        var errors = new List<Exception>();
        var controller = new MiniBackupController((_, _) => throw new IOException("failed"), () => false, errors.Add);
        var operation = controller.BackupAsync("");
        Assert.AreEqual(MiniWindowVisualState.BackupFailed, controller.State);
        Assert.HasCount(1, errors);
        controller.Dispose();
        await operation;
    }

    [TestMethod]
    public void ChangesDuringBackupSurviveAcknowledgementOfEarlierRevision()
    {
        var changes = new ChangeTrackingState(true);
        var captured = changes.Revision;
        changes.MarkChanged();
        changes.Acknowledge(captured);
        Assert.IsTrue(changes.HasChanges);
        changes.Acknowledge(changes.Revision);
        Assert.IsFalse(changes.HasChanges);
    }

    [TestMethod]
    public void StaleAcknowledgementNeverReintroducesOldChanges()
    {
        var changes = new ChangeTrackingState();
        changes.MarkChanged(); changes.MarkChanged();
        changes.Acknowledge(2); changes.Acknowledge(1);
        Assert.IsFalse(changes.HasChanges);
        changes.Acknowledge(999);
        changes.MarkChanged();
        Assert.IsTrue(changes.HasChanges);
    }

    [TestMethod]
    public void WindowCodeBehindDoesNotOwnBackupOrProcessBusiness()
    {
        var source = File.ReadAllText(Path.Combine(FolderManagerMvvmArchitectureTests.FindRoot(), "FolderRewind/Views/MiniWindow.xaml.cs"));
        foreach (var dependency in new[] { "BackupService", "FolderWatcherService", "Process.Start", "ConfigService", "Task.Run(", "TryEnqueue(async" })
            Assert.IsFalse(source.Contains(dependency, StringComparison.Ordinal), dependency);
        StringAssert.Contains(source, "ViewModel.Dispose()");
        StringAssert.Contains(source, "Tick -= WatchTimer_Tick");
    }
}

using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryInteractionControllerTests
{
    [TestMethod]
    public async Task ConfirmAndExecuteAsync_Cancelled_DoesNotRunOperation()
    {
        var interaction = new FakeHistoryInteractionService { ConfirmResult = false };
        var controller = new HistoryInteractionController(interaction);
        var executed = false;

        var result = await controller.ConfirmAndExecuteAsync(
            "title",
            "message",
            "continue",
            _ =>
            {
                executed = true;
                return Task.FromResult(true);
            },
            "failed");

        Assert.IsFalse(result);
        Assert.IsFalse(executed);
        Assert.IsEmpty(interaction.Notifications);
    }

    [TestMethod]
    public async Task ConfirmAndExecuteAsync_Success_DoesNotReportFailure()
    {
        var interaction = new FakeHistoryInteractionService { ConfirmResult = true };
        var controller = new HistoryInteractionController(interaction);

        var result = await controller.ConfirmAndExecuteAsync(
            "title",
            "message",
            "continue",
            _ => Task.FromResult(true),
            "failed");

        Assert.IsTrue(result);
        Assert.IsEmpty(interaction.Notifications);
    }

    [TestMethod]
    public async Task ConfirmAndExecuteAsync_FalseResult_ReportsFailure()
    {
        var interaction = new FakeHistoryInteractionService { ConfirmResult = true };
        var controller = new HistoryInteractionController(interaction);

        var result = await controller.ConfirmAndExecuteAsync(
            "title",
            "message",
            "continue",
            _ => Task.FromResult(false),
            "operation failed");

        Assert.IsFalse(result);
        CollectionAssert.AreEqual(
            new[] { (HistoryNotificationKind.Error, "operation failed") },
            interaction.Notifications);
    }

    [TestMethod]
    public async Task RequestTextAndExecuteAsync_Exception_ReportsObservedError()
    {
        var interaction = new FakeHistoryInteractionService { TextResult = "updated" };
        var controller = new HistoryInteractionController(interaction);

        var result = await controller.RequestTextAndExecuteAsync(
            "title",
            "message",
            "initial",
            (_, _) => throw new InvalidOperationException("write failed"),
            "fallback");

        Assert.IsFalse(result);
        CollectionAssert.AreEqual(
            new[] { (HistoryNotificationKind.Error, "write failed") },
            interaction.Notifications);
    }

    [TestMethod]
    public async Task RequestTextAndExecuteAsync_CancelledInput_DoesNotRunOperation()
    {
        var interaction = new FakeHistoryInteractionService { TextResult = null };
        var controller = new HistoryInteractionController(interaction);
        var executed = false;

        var result = await controller.RequestTextAndExecuteAsync(
            "title",
            "message",
            "initial",
            (_, _) =>
            {
                executed = true;
                return Task.FromResult(true);
            },
            "fallback");

        Assert.IsFalse(result);
        Assert.IsFalse(executed);
        Assert.IsEmpty(interaction.Notifications);
    }

    [TestMethod]
    public async Task RequestTextAndExecuteAsync_RequiredValueRejectsWhitespaceWithoutFailure()
    {
        var interaction = new FakeHistoryInteractionService { TextResult = "   " };
        var controller = new HistoryInteractionController(interaction);
        var executed = false;

        var result = await controller.RequestTextAndExecuteAsync(
            "title",
            "message",
            "initial",
            (_, _) =>
            {
                executed = true;
                return Task.FromResult(true);
            },
            "fallback",
            allowEmpty: false);

        Assert.IsFalse(result);
        Assert.IsFalse(executed);
        Assert.IsEmpty(interaction.Notifications);
    }

    [TestMethod]
    public async Task ConfirmAndExecuteAsync_Cancellation_IsNotReportedAsFailure()
    {
        var interaction = new FakeHistoryInteractionService { ConfirmResult = true };
        var controller = new HistoryInteractionController(interaction);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            controller.ConfirmAndExecuteAsync(
                "title",
                "message",
                "continue",
                token => Task.FromCanceled<bool>(token),
                "fallback",
                cancellationToken: source.Token));

        Assert.IsEmpty(interaction.Notifications);
    }

    private sealed class FakeHistoryInteractionService : IHistoryInteractionService
    {
        public bool ConfirmResult { get; init; }
        public string? TextResult { get; init; }
        public List<(HistoryNotificationKind Kind, string Message)> Notifications { get; } = [];

        public Task<bool> ConfirmAsync(
            string title,
            string message,
            string primaryButtonText,
            bool isDestructive = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ConfirmResult);

        public Task<string?> RequestTextAsync(
            string title,
            string message,
            string initialValue = "",
            bool isPassword = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult(TextResult);

        public Task<HistoryInteractionResult> ChooseAsync(
            HistoryChoiceRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new HistoryInteractionResult(HistoryInteractionOutcome.Cancelled));

        public Task<string?> PickFolderAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task OpenCloudSyncAsync(string configId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Notify(HistoryNotificationKind kind, string message)
            => Notifications.Add((kind, message));

        public void NotifyRestoreCompleted(string targetName, bool success, string? detail = null)
        {
        }
    }
}

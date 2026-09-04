using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SettingsOrchestrationTests
{
    [TestMethod]
    public async Task ValidationFailureDoesNotWriteAndAllowsRetry()
    {
        var controller = new SettingsSaveController();
        var errors = new List<string>(); var writes = 0;
        Task<ConfigSaveResult> Save() { writes++; return Task.FromResult(new ConfigSaveResult { Success = true }); }
        Assert.IsFalse(await controller.SaveAsync(() => "invalid", Save, errors.Add));
        Assert.AreEqual(0, writes);
        Assert.IsFalse(controller.IsSaving);
        Assert.IsTrue(await controller.SaveAsync(() => null, Save, errors.Add));
        Assert.AreEqual(1, writes);
        CollectionAssert.AreEqual(new[] { "invalid" }, errors);
    }

    [TestMethod]
    public async Task FailedWriteKeepsEditorOpenAndReportsFailure()
    {
        var errors = new List<string>();
        var controller = new SettingsSaveController();
        Assert.IsFalse(await controller.SaveAsync(() => null,
            () => Task.FromResult(new ConfigSaveResult { ErrorMessage = "disk full" }), errors.Add));
        Assert.IsFalse(controller.IsSaving);
        CollectionAssert.AreEqual(new[] { "disk full" }, errors);
    }

    [TestMethod]
    public async Task ThrownWriteFailureIsObserved()
    {
        var errors = new List<string>();
        var controller = new SettingsSaveController();
        Assert.IsFalse(await controller.SaveAsync(() => null, () => throw new IOException("denied"), errors.Add));
        CollectionAssert.AreEqual(new[] { "denied" }, errors);
        Assert.IsFalse(controller.IsSaving);
    }

    [TestMethod]
    public async Task SaveWaitsForDurabilityRejectsDuplicatesAndDoesNotAbandonAdmittedWrite()
    {
        var source = new TaskCompletionSource<ConfigSaveResult>();
        var controller = new SettingsSaveController();
        using var cancellation = new CancellationTokenSource();
        var pending = controller.SaveAsync(() => null, () => source.Task, Assert.Fail, cancellation.Token);
        Assert.IsTrue(controller.IsSaving);
        Assert.IsFalse(pending.IsCompleted);
        Assert.IsFalse(await controller.SaveAsync(() => throw new Exception("duplicate"), () => source.Task, Assert.Fail));
        cancellation.Cancel();
        Assert.IsFalse(pending.IsCompleted);
        source.SetResult(new ConfigSaveResult { Success = true });
        Assert.IsTrue(await pending);
    }

    [TestMethod]
    public async Task CanceledSaveDoesNotValidateOrWrite()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.IsFalse(await new SettingsSaveController().SaveAsync(
            () => throw new Exception("must not validate"), () => throw new Exception("must not write"),
            Assert.Fail, cancellation.Token));
    }

    [TestMethod]
    [DataRow("", true)]
    [DataRow("localhost", true)]
    [DataRow("127.0.0.1", true)]
    [DataRow("::1", true)]
    [DataRow("example.org", true)]
    [DataRow("https://example.org", false)]
    [DataRow("example.org:6376", false)]
    [DataRow("bad host", false)]
    public void KnotLinkHostValidationKeepsDefaultsAndRejectsUrlOrPort(string host, bool valid)
        => Assert.AreEqual(valid, KnotLinkSettingsPolicy.Validate(host, "", "id") is null);

    [TestMethod]
    public void IdentifiersRetainProtocolFreedomButRejectControlCharacters()
    {
        Assert.IsNull(KnotLinkSettingsPolicy.Validate(null, "test-id", "plugin:custom", ""));
        Assert.IsNotNull(KnotLinkSettingsPolicy.Validate(null, "id\r\ninjected"));
    }

    [TestMethod]
    [DataRow(false, false, false, false, KnotLinkConnectionStatus.Disabled)]
    [DataRow(true, false, false, false, KnotLinkConnectionStatus.NotInitialized)]
    [DataRow(true, true, true, true, KnotLinkConnectionStatus.Connected)]
    [DataRow(true, true, true, false, KnotLinkConnectionStatus.Partial)]
    [DataRow(true, true, false, true, KnotLinkConnectionStatus.Partial)]
    [DataRow(true, true, false, false, KnotLinkConnectionStatus.Failed)]
    public void ConnectionPresentationUsesSemanticStatus(bool enabled, bool initialized, bool receiver, bool sender, object expected)
        => Assert.AreEqual((KnotLinkConnectionStatus)expected, KnotLinkSettingsPolicy.GetStatus(enabled, initialized, receiver, sender));

    [TestMethod]
    [DataRow(OperationOutcome.Success, SemanticStatus.Success)]
    [DataRow(OperationOutcome.SuccessWithWarnings, SemanticStatus.Warning)]
    [DataRow(OperationOutcome.Failed, SemanticStatus.Error)]
    [DataRow(OperationOutcome.Blocked, SemanticStatus.Error)]
    [DataRow(OperationOutcome.Canceled, SemanticStatus.Error)]
    [DataRow(OperationOutcome.NoChanges, SemanticStatus.Error)]
    public void FailedDeletionNeverReportsSuccess(OperationOutcome outcome, SemanticStatus expected)
        => Assert.AreEqual(expected, PluginSettingsOutcomePolicy.GetStatus(outcome));

    [TestMethod]
    public async Task PluginCommandWaitsForCompletionAndPreventsOverlappingWork()
    {
        var release = new TaskCompletionSource();
        var actions = new FakeActions(async (_, _) => await release.Task);
        using var controller = new PluginSettingsCommandController(actions, _ => Assert.Fail());
        var request = new PluginSettingsRequest(PluginSettingsAction.KnotLinkRestart);
        var pending = controller.ExecuteAsync(request);
        Assert.IsTrue(controller.IsBusy);
        Assert.IsFalse(controller.CanExecute);
        await controller.ExecuteAsync(request);
        Assert.AreEqual(1, actions.Calls);
        release.SetResult(); await pending;
        Assert.IsTrue(controller.CanExecute);
    }

    [TestMethod]
    public async Task PluginCommandExceptionsAreObservedAndNextRequestStillRuns()
    {
        var errors = new List<Exception>();
        var actions = new FakeActions((_, _) => throw new IOException("offline"));
        using var controller = new PluginSettingsCommandController(actions, errors.Add);
        var request = new PluginSettingsRequest(PluginSettingsAction.KnotLinkTest);
        await controller.ExecuteAsync(request); await controller.ExecuteAsync(request);
        Assert.HasCount(2, errors); Assert.AreEqual(2, actions.Calls);
        Assert.IsTrue(controller.CanExecute);
    }

    [TestMethod]
    public async Task NavigationCancelsPluginCommandAndPreventsLaterRequestsUntilActivated()
    {
        var entered = new TaskCompletionSource();
        var actions = new FakeActions(async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); });
        using var controller = new PluginSettingsCommandController(actions, _ => Assert.Fail());
        var request = new PluginSettingsRequest(PluginSettingsAction.KnotLinkSendCustom);
        var pending = controller.ExecuteAsync(request); await entered.Task;
        controller.Deactivate(); await pending;
        await controller.ExecuteAsync(request);
        Assert.AreEqual(1, actions.Calls); Assert.IsFalse(controller.CanExecute);
        controller.Activate(); Assert.IsTrue(controller.CanExecute);
    }

    [TestMethod]
    public void SettingsCodeBehindDoesNotOwnPersistenceProcessOrPluginBusiness()
    {
        var root = FolderManagerMvvmArchitectureTests.FindRoot();
        var files = new[] { "Views/ConfigSettingsDialog.xaml.cs", "Views/ConfigSettingsDialog.Actions.cs",
            "Views/ConfigSettingsDialog.Filters.cs", "Views/Settings/PluginsKnotLinkControl.xaml.cs" };
        foreach (var file in files)
        {
            var source = File.ReadAllText(Path.Combine(root, "FolderRewind", file));
            foreach (var forbidden in new[] { "ConfigService.", "PluginService.", "PluginV3PackageService.", "KnotLinkService.",
                "KnotLinkServerManagerService.", "BackupPresetService.", "Directory.", "Process.Start", "Task.Run(" })
                Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal), file + ": " + forbidden);
        }
    }

    private sealed class FakeActions(Func<PluginSettingsRequest, CancellationToken, Task> execute) : IPluginSettingsActions
    {
        public int Calls { get; private set; }
        public Task ExecuteAsync(PluginSettingsRequest request, CancellationToken token) { Calls++; return execute(request, token); }
    }
}

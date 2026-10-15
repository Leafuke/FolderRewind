using FolderRewind.History.Storage;
using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class UiFailurePresentationTests
{
    [TestMethod]
    public void MissingOrUnreadableBoundHistoryOffersImportWithoutParsingDiagnosticText()
    {
        foreach (var diagnostic in new[] { "opaque diagnostic", "仓库不可读取", "repository.json is missing" })
        {
            var error = new HistoryRepositoryBindingException(HistoryRepositoryBindingStatus.RecoveryRequired, diagnostic);
            var presentation = HistoryLoadFailurePolicy.Present(error, false);
            Assert.AreEqual("HistoryLoad_RecoveryRequired", presentation.SummaryKey);
            Assert.AreEqual(HistoryLoadAction.ImportHistory, presentation.Action);
            Assert.AreEqual(diagnostic, error.Message);
            Assert.IsInstanceOfType<InvalidOperationException>(error);
        }
    }

    [TestMethod]
    public void PendingMigrationProvidesContinuationInsteadOfStartingAnotherImport()
    {
        var presentation = HistoryLoadFailurePolicy.Present(
            new HistoryRepositoryBindingException(HistoryRepositoryBindingStatus.RecoveryRequired, "detail"), true);
        Assert.AreEqual(HistoryLoadAction.ContinueMigration, presentation.Action);
    }

    [TestMethod]
    public void UnsupportedFormatDoesNotOfferRetryOrImportEvenWithPendingMigration()
    {
        var presentation = HistoryLoadFailurePolicy.Present(
            new HistoryRepositoryBindingException(HistoryRepositoryBindingStatus.CompatibilityBlocked, "future format"), true);
        Assert.AreEqual("HistoryLoad_Incompatible", presentation.SummaryKey);
        Assert.AreEqual(HistoryLoadAction.None, presentation.Action);
    }

    [TestMethod]
    public void PersistenceAndAccessFailuresOfferRetryAndKeepDiagnosticOutOfSummary()
    {
        var save = HistoryLoadFailurePolicy.Present(
            new HistoryRepositoryBindingException(HistoryRepositoryBindingStatus.BindingPersistenceFailed, "private path"), false);
        Assert.AreEqual("HistoryLoad_BindingSaveFailed", save.SummaryKey);
        Assert.AreEqual(HistoryLoadAction.Retry, save.Action);
        var access = HistoryLoadFailurePolicy.Present(new UnauthorizedAccessException("secret credential"), false);
        Assert.AreEqual("HistoryLoad_AccessDenied", access.SummaryKey);
        Assert.AreEqual(HistoryLoadAction.Retry, access.Action);
    }

    [TestMethod]
    public void PartialMigrationAndPermissionChecksAreNotPresentedAsSuccess()
    {
        Assert.AreEqual(SemanticStatus.Warning, MigrationPresentationPolicy.StatusFor(MigrationWorkflowStage.PartiallyCompleted));
        Assert.AreEqual(SemanticStatus.Warning, MigrationPresentationPolicy.StatusFor(MigrationWorkflowStage.AwaitingPermissions));
        Assert.AreEqual(SemanticStatus.Warning, MigrationPresentationPolicy.StatusFor(MigrationWorkflowStage.NeedsAttention));
        Assert.AreEqual(SemanticStatus.Info, MigrationPresentationPolicy.StatusFor(MigrationWorkflowStage.Paused));
        Assert.AreEqual(SemanticStatus.Success, MigrationPresentationPolicy.StatusFor(MigrationWorkflowStage.Completed));
    }

    [TestMethod]
    public void UnrelatedEditorSurvivesRefreshButChangedConfigurationOrRecoveryPhaseInvalidatesItsEvidence()
    {
        var current = new MigrationWorkflowProject("project-a", "A")
        { Phase = MigrationProjectPhase.AwaitingPaths, ConfigurationSignature = "signature-a" };
        Assert.IsTrue(MigrationPresentationPolicy.CanReuseEditor(MigrationProjectPhase.AwaitingPaths, "signature-a", current));
        Assert.IsFalse(MigrationPresentationPolicy.CanReuseEditor(MigrationProjectPhase.AwaitingPaths, "signature-a",
            current with { ConfigurationSignature = "signature-b" }));
        Assert.IsFalse(MigrationPresentationPolicy.CanReuseEditor(MigrationProjectPhase.AwaitingPaths, "signature-a",
            current with { Phase = MigrationProjectPhase.Completed }));
    }
}

using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class TaskWarningPresentationTests
{
    private static PluginDiagnostic Warning(string path = "world") => new("minerewind.world_lock_probe_failed",
        DiagnosticSeverity.Warning, "BackupConsistency", "com.folderrewind.minerewind",
        new Dictionary<string, string> { ["path"] = path, ["hresult"] = "0x80070005" });

    [TestMethod]
    public void WarningSnapshotsAreDetachedAndOnlyIdenticalDiagnosticsAreDeduplicated()
    {
        var source = Warning();
        var snapshot = OperationDiagnosticFormatter.Snapshot([source, source, Warning("other world")]);
        ((Dictionary<string, string>)source.Arguments)["path"] = "changed after capture";
        Assert.HasCount(2, snapshot);
        Assert.AreEqual("world", snapshot[0].Arguments["path"]);
    }

    [TestMethod]
    public void SuccessWithWarningsHasDetailsAndWarningSemanticsWithoutBecomingAFailure()
    {
        var task = new BackupTask { IsCompleted = true, IsSuccess = true };
        Assert.AreEqual(SemanticStatus.Success, task.StatusSemantic);
        Assert.IsFalse(task.HasDetails);
        var changed = new List<string>();
        task.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);
        task.WarningDiagnostics = [Warning()];
        Assert.IsTrue(task.IsSuccess);
        Assert.IsTrue(task.HasWarnings);
        Assert.IsTrue(task.HasDetails);
        Assert.AreEqual("", task.ErrorMessage);
        Assert.AreEqual(SemanticStatus.Warning, task.StatusSemantic);
        CollectionAssert.Contains(changed, nameof(BackupTask.StatusSemantic));
        CollectionAssert.Contains(changed, nameof(BackupTask.HasDetails));
        task.IsSuccess = false;
        task.ErrorMessage = "backup failed";
        Assert.AreEqual(SemanticStatus.Error, task.StatusSemantic);
    }

    [TestMethod]
    public void CopyableDetailsIncludeWarningIdentityAndAllAvailableSections()
    {
        var task = new BackupTask { ErrorMessage = "error evidence", Log = "7z evidence", WarningDiagnostics = [Warning()] };
        var details = OperationDiagnosticFormatter.TaskDetails(task);
        StringAssert.Contains(details, "error evidence");
        StringAssert.Contains(details, "7z evidence");
        StringAssert.Contains(details, "minerewind.world_lock_probe_failed");
        StringAssert.Contains(details, "0x80070005");
    }
}

using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryWorkspaceRepairTests
{
    private string _root = null!;
    private HistoryRuntime _history = null!;
    private string WorkspacePath => Path.Combine(_history.Repository.Paths.LocalStateRoot, "workspace.json");
    [TestInitialize]
    public async Task Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "fr-workspace-repair-" + Guid.NewGuid().ToString("N"));
        _history = new(new(new("workspace-repair"), new(_root)));
        await _history.InitializeReadableAsync();
    }
    [TestCleanup]
    public async Task Cleanup()
    {
        _history.WorkspaceStore.RepairFault = null;
        await _history.DisposeAsync();
        Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task RepairPreservesBytesAndDoesNotRepairIndependentCatalogDamage()
    {
        File.WriteAllText(WorkspacePath, "original damaged state");
        File.WriteAllText(Path.Combine(_history.Repository.Paths.LocalStateRoot, "replicas.json"), "separate damage");
        var source = SourceId.New();
        var service = new HistoryWorkspaceRepairService(_history);
        var preview = await service.PreviewAsync([source], "config");
        var repaired = await service.RepairAsync(preview, [source], "config");
        Assert.IsTrue(repaired.Rebuilt);
        Assert.AreEqual("original damaged state", File.ReadAllText(repaired.PreservedPath!));
        var baseline = repaired.Workspace.GetSourceState(source);
        Assert.AreEqual(WorkspaceBaselineRelation.Unknown, baseline.Relation);
        Assert.IsTrue(baseline.RequiresFullCapture);
        Assert.IsNull(baseline.BaseVersionId); Assert.IsNull(baseline.ActiveBranchId);
        Assert.AreNotEqual(Guid.Empty, baseline.ContentRealizationId);
        Assert.IsTrue(_history.Health.HasFlag(HistoryRuntimeHealth.LocalReplicaCatalogRecoveryRequired));
        Assert.IsFalse(_history.Health.HasFlag(HistoryRuntimeHealth.WorkspaceRecoveryRequired));
        Assert.IsFalse((await service.RepairAsync(preview, [source], "config")).Rebuilt);
    }

    [TestMethod]
    public async Task DeniedReadIsInaccessibleAndNeverAuthorizesRepair()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows ACL semantics are required."); return; }
        File.WriteAllText(WorkspacePath, "preserve inaccessible state");
        var info = new FileInfo(WorkspacePath);
        var original = System.IO.FileSystemAclExtensions.GetAccessControl(info);
        try
        {
            var security = System.IO.FileSystemAclExtensions.GetAccessControl(info);
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var user = identity.User!;
            security.AddAccessRule(new(user, System.Security.AccessControl.FileSystemRights.ReadData, System.Security.AccessControl.AccessControlType.Deny));
            System.IO.FileSystemAclExtensions.SetAccessControl(info, security);
            var state = await _history.WorkspaceStore.LoadAsync();
            Assert.AreEqual(DeviceLocalStateStatus.Inaccessible, state.Status);
            Assert.IsNotNull(state.ErrorCode);
            var preview = await new HistoryWorkspaceRepairService(_history).PreviewAsync([], "config");
            Assert.IsFalse(preview.CanRepair);
        }
        finally
        {
            var restored = new System.Security.AccessControl.FileSecurity();
            restored.SetSecurityDescriptorSddlForm(original.GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access),
                System.Security.AccessControl.AccessControlSections.Access);
            System.IO.FileSystemAclExtensions.SetAccessControl(info, restored);
        }
        Assert.AreEqual("preserve inaccessible state", File.ReadAllText(WorkspacePath));
    }

    [TestMethod]
    public async Task OccupiedAndUnsupportedFilesCannotBeResetAndChangedPreviewCannotOverwrite()
    {
        File.WriteAllText(WorkspacePath, "broken");
        using (var locked = new FileStream(WorkspacePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.AreEqual(DeviceLocalStateStatus.Inaccessible, (await _history.WorkspaceStore.LoadAsync()).Status);
        File.WriteAllText(WorkspacePath, "{\"formatVersion\":999}");
        var service = new HistoryWorkspaceRepairService(_history);
        var unsupported = await service.PreviewAsync([], "config");
        Assert.IsTrue(unsupported.UnsupportedFormat); Assert.IsFalse(unsupported.CanRepair);
        await Assert.ThrowsExactlyAsync<DeviceLocalStateConflictException>(() => service.RepairAsync(unsupported, [], "config"));
        await Assert.ThrowsExactlyAsync<DeviceLocalStateConflictException>(() => _history.WorkspaceStore.SaveAsync(new(_history.ConfigId, 0, []), -1));
        File.WriteAllText(WorkspacePath, "broken");
        var preview = await service.PreviewAsync([], "config");
        File.WriteAllText(WorkspacePath, "changed damage");
        await Assert.ThrowsExactlyAsync<DeviceLocalStateConflictException>(() => service.RepairAsync(preview, [], "config"));
        Assert.AreEqual("changed damage", File.ReadAllText(WorkspacePath));
        await Assert.ThrowsExactlyAsync<DeviceLocalStateConflictException>(() => service.RepairAsync(preview, [], "different config"));
    }

    [TestMethod]
    public async Task MissingRepairIsCreateOnlyAndPendingTransactionsCannotBeBypassed()
    {
        var service = new HistoryWorkspaceRepairService(_history);
        var preview = await service.PreviewAsync([], "config");
        _history.WorkspaceStore.RepairFault = point => { if (point == "before-publication") File.WriteAllText(WorkspacePath, "foreign"); };
        await Assert.ThrowsExactlyAsync<IOException>(() => service.RepairAsync(preview, [], "config"));
        Assert.AreEqual("foreign", File.ReadAllText(WorkspacePath));
        _history.WorkspaceStore.RepairFault = null;
        File.Delete(WorkspacePath);
        var journal = Path.Combine(_history.Repository.Paths.TransactionsRoot, "restore-journal.json");
        File.WriteAllText(journal, "null");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.RepairAsync(preview, [], "config"));
        Assert.IsFalse(File.Exists(WorkspacePath));
        Assert.AreEqual("null", File.ReadAllText(journal));
        File.Delete(journal);
        Assert.IsTrue((await service.RepairAsync(preview, [], "config")).Rebuilt);
    }

    [TestMethod]
    [DataRow("after-preservation")]
    [DataRow("before-publication")]
    [DataRow("after-publication")]
    public async Task InterruptedRepairPreservesOriginalAndRetriesWithoutResettingValidState(string point)
    {
        File.WriteAllText(WorkspacePath, "raw bytes to preserve");
        var service = new HistoryWorkspaceRepairService(_history);
        var preview = await service.PreviewAsync([SourceId.New()], "config");
        _history.WorkspaceStore.RepairFault = actual => { if (actual == point) throw new IOException("interrupted"); };
        await Assert.ThrowsExactlyAsync<IOException>(() => service.RepairAsync(preview, preview.Sources, "config"));
        _history.WorkspaceStore.RepairFault = null;
        var copies = Directory.GetFiles(_history.Repository.Paths.LocalStateRoot, "*.original");
        Assert.IsNotEmpty(copies);
        Assert.IsTrue(copies.All(p => File.ReadAllText(p) == "raw bytes to preserve"));
        var next = await service.PreviewAsync(preview.Sources, "config");
        await service.RepairAsync(next, next.Sources, "config");
        Assert.AreEqual(DeviceLocalStateStatus.Valid, (await _history.WorkspaceStore.LoadAsync()).Status);
    }
}

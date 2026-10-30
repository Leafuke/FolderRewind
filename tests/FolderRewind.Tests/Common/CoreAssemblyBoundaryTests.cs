using System.Reflection;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;

namespace FolderRewind.Tests;

[TestClass]
public sealed class CoreAssemblyBoundaryTests
{
    [TestMethod]
    public void ProductionHistoryHasOneHeadlessAssemblyOwner()
    {
        var core = typeof(HistoryRuntime).Assembly;
        Assert.AreEqual("FolderRewind.Core", core.GetName().Name);
        Assert.AreSame(core, typeof(VersionId).Assembly);
        Assert.IsFalse(core.GetReferencedAssemblies().Any(reference => reference.Name is "FolderRewind"
            || reference.Name!.StartsWith("Microsoft.UI", StringComparison.Ordinal)
            || reference.Name.StartsWith("Microsoft.WindowsAppSDK", StringComparison.Ordinal)));
        var duplicated = core.GetTypes().Where(type => type.Namespace?.StartsWith("FolderRewind.", StringComparison.Ordinal) == true
            && GetType().Assembly.GetType(type.FullName!) is not null).Select(type => type.FullName).ToArray();
        Assert.IsEmpty(duplicated, "Tests must reference Core rather than compile copies of its production types.");
    }

    [TestMethod]
    public void ProductionRecoveryDoesNotExposeFaultInjectionFields()
    {
        var core = typeof(HistoryRuntime).Assembly;
        var flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Assert.IsNull(core.GetType("FolderRewind.History.Application.HistoryRestoreFileSystemPlanStore")!.GetField("TestFault", flags));
        Assert.IsNull(core.GetType("FolderRewind.History.LocalState.HistoryWorkspaceStore")!.GetProperty("RepairFault", flags));
    }

    [TestMethod]
    public void RecoveryDiagnosticIsDeliveredToTheSuppliedHostSink()
    {
        var messages = new List<(string Message, string Source)>();
        HistoryRestoreRecoveryDiagnostics.Report(new HistoryConfigId("diagnostic-test"), null, "Preparing", false,
            null, new IOException("missing ownership evidence"), "RecoveryBlocked", (message, source) => messages.Add((message, source)));
        Assert.HasCount(1, messages);
        Assert.AreEqual("RestoreRecovery", messages[0].Source);
        StringAssert.Contains(messages[0].Message, "missing ownership evidence");
        StringAssert.Contains(messages[0].Message, "RecoveryBlocked");
    }
}

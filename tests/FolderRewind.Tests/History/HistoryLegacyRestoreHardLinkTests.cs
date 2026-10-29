using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using System.Runtime.InteropServices;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryLegacyRestoreHardLinkTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LegacyCompletionAndRollbackNeverClearAnOutsideHardLinksReadOnlyAttribute(bool rollback)
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-legacy-hardlink-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var outside = Path.Combine(root, "outside");
        try
        {
            var target = Path.Combine(root, "world"); Directory.CreateDirectory(target);
            var staging = Path.Combine(root, "staging"); Directory.CreateDirectory(staging);
            File.WriteAllText(outside, "original object");
            Assert.IsTrue(CreateHardLinkW(Path.Combine(target, "shared"), outside, IntPtr.Zero));
            File.SetAttributes(outside, FileAttributes.ReadOnly);
            var identity = WindowsRestoreFileSystem.Identity(outside);
            File.WriteAllText(Path.Combine(staging, "shared"), "restored contents");
            var binding = new HistoryRestoreSourceBinding(SourceId.New(), target);
            var backend = new FileSystemHistoryRestoreMutationBackend();
            var snapshot = backend.PlanRollback(binding, HistoryTransactionId.New());
            await backend.PrepareRollbackAsync(snapshot, default);
            await backend.ApplyAsync(binding, staging, HistoryRestoreApplyMode.Clean, snapshot, default);
            if (rollback) await backend.RollbackAsync(snapshot, default);
            else await backend.CommitAsync(snapshot, default);
            Assert.AreEqual("original object", File.ReadAllText(outside));
            Assert.AreEqual(identity, WindowsRestoreFileSystem.Identity(outside));
            Assert.IsTrue(File.GetAttributes(outside).HasFlag(FileAttributes.ReadOnly));
            Assert.AreEqual(rollback ? "original object" : "restored contents", File.ReadAllText(Path.Combine(target, "shared")));
        }
        finally
        {
            if (File.Exists(outside)) File.SetAttributes(outside, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string name, string existing, IntPtr attributes);
}

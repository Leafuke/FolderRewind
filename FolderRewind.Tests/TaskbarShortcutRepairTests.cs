using FolderRewind.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Runtime.InteropServices;

namespace FolderRewind.Tests;

[TestClass]
public sealed class TaskbarShortcutRepairTests
{
    [TestMethod]
    public void RepairsOnlyExactTargetAndPreservesArgumentsAndForeignPins()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows Shell integration test."); return; }
        var root = Path.Combine(Path.GetTempPath(), "FolderRewind-pins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "FolderRewind.exe");
        File.WriteAllText(executable, "inert shortcut target");
        var owned = Path.Combine(root, "Owned.lnk");
        var foreign = Path.Combine(root, "Foreign.lnk");
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        try
        {
            object link = ((dynamic)shell).CreateShortcut(owned);
            try { ((dynamic)link).TargetPath = executable; ((dynamic)link).Arguments = "--safe-mode"; ((dynamic)link).IconLocation = Path.Combine(root,"removed-version.ico") + ",0"; ((dynamic)link).Save(); }
            finally { Marshal.FinalReleaseComObject(link); }
            object other = ((dynamic)shell).CreateShortcut(foreign);
            try { ((dynamic)other).TargetPath = Path.Combine(root,"Other","FolderRewind.exe"); ((dynamic)other).Save(); }
            finally { Marshal.FinalReleaseComObject(other); }
            var untouched = File.ReadAllBytes(foreign);
            Assert.AreEqual(1, TaskbarShortcutRepair.RepairOwnedPins(root, executable, error => Assert.Fail(error.ToString())));
            Assert.AreEqual(ShellShortcutIdentity.MsiAppId, ShellShortcutIdentity.GetShortcutProperty(owned,5));
            Assert.AreEqual($"\"{executable}\"", ShellShortcutIdentity.GetShortcutProperty(owned,2));
            Assert.AreEqual(Path.Combine(root,"Assets","MsiApp.ico")+",0", ShellShortcutIdentity.GetShortcutProperty(owned,3));
            Assert.AreEqual("FolderRewind", ShellShortcutIdentity.GetShortcutProperty(owned,4));
            object updated = ((dynamic)shell).CreateShortcut(owned);
            try { Assert.AreEqual("--safe-mode", (string)((dynamic)updated).Arguments); Assert.AreEqual(executable+",0", (string)((dynamic)updated).IconLocation); }
            finally { Marshal.FinalReleaseComObject(updated); }
            CollectionAssert.AreEqual(untouched,File.ReadAllBytes(foreign));
            Assert.AreEqual(0,TaskbarShortcutRepair.RepairOwnedPins(root,executable));
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
            // Fixed files in the fresh test directory only; no real taskbar pin.
            foreach (var path in new[] {owned,foreign,executable}) if (File.Exists(path)) File.Delete(path);
            Directory.Delete(root);
        }
    }

    [TestMethod]
    public void RepairsLegacyRelaunchResourceEvenWhenShortcutIconAndAppIdAreCurrent()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows Shell integration test."); return; }
        var root = Path.Combine(Path.GetTempPath(), "FolderRewind-pins with spaces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "FolderRewind.exe");
        File.WriteAllText(executable, "inert shortcut target");
        var owned = Path.Combine(root, "Owned.lnk");
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        try
        {
            object link = ((dynamic)shell).CreateShortcut(owned);
            try
            {
                ((dynamic)link).TargetPath = executable;
                ((dynamic)link).IconLocation = executable + ",0";
                ((dynamic)link).Save();
            }
            finally { Marshal.FinalReleaseComObject(link); }
            ShellShortcutIdentity.SetShortcutIdentity(owned, ShellShortcutIdentity.MsiAppId, executable);
            // Recreate the malformed EXE resource left by 1.9.3.0.
            ShellShortcutIdentity.SetShortcutProperty(owned, 3, executable + ",0");
            Assert.AreEqual(1, TaskbarShortcutRepair.RepairOwnedPins(root, executable, error => Assert.Fail(error.ToString())));
            Assert.AreEqual(Path.Combine(root, "Assets", "MsiApp.ico") + ",0", ShellShortcutIdentity.GetShortcutProperty(owned, 3));
            Assert.AreEqual(0, TaskbarShortcutRepair.RepairOwnedPins(root, executable));
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
            foreach (var path in new[] {owned, executable}) if (File.Exists(path)) File.Delete(path);
            Directory.Delete(root);
        }
    }
}

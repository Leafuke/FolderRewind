using FolderRewind.Services;
using System.Diagnostics;

namespace FolderRewind.Tests;

[TestClass]
public sealed class OwnedCloudDependencyProcessTests
{
    [TestMethod]
    public async Task ObservedExternalEndpointNeverGrantsStopOwnership()
    {
        var controller = new OwnedCloudDependencyProcess(); var starts = 0;
        using var lease = await controller.AcquireAsync("external", false, () => { starts++; return new(); },
            _ => Task.FromResult(true), TimeSpan.FromSeconds(1), default);
        Assert.AreEqual(0, starts);
        Assert.IsFalse(controller.Owns("external"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => controller.StopAsync("external", default));
    }
    [TestMethod]
    public async Task UnavailableDependencyWithoutAuthorizationDoesNotStartAProcess()
    {
        var controller = new OwnedCloudDependencyProcess(); var starts = 0;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => controller.AcquireAsync("not-authorized", false,
            () => { starts++; return new(); }, _ => Task.FromResult(false), TimeSpan.FromSeconds(1), default));
        Assert.AreEqual(0, starts);
    }
    [TestMethod]
    public async Task ConcurrentUsersStartOnceAndReleaseDoesNotStopTheOwnedInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindDependency", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "ready"); var script = Path.Combine(root, "dependency.js");
        var path = System.Text.Json.JsonSerializer.Serialize(marker);
        File.WriteAllText(script, "var fs = new ActiveXObject('Scripting.FileSystemObject'); fs.CreateTextFile(" + path + ", true).Close(); WScript.Sleep(60000);");
        var controller = new OwnedCloudDependencyProcess(); var starts = 0;
        ProcessStartInfo Start()
        {
            starts++;
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cscript.exe")) { WorkingDirectory = root };
            start.ArgumentList.Add("//nologo"); start.ArgumentList.Add(script); return start;
        }
        IDisposable[] leases = [];
        try
        {
            leases = await Task.WhenAll(controller.AcquireAsync("authorized-instance", true, Start, _ => Task.FromResult(File.Exists(marker)), TimeSpan.FromSeconds(5), default),
                controller.AcquireAsync("authorized-instance", true, Start, _ => Task.FromResult(File.Exists(marker)), TimeSpan.FromSeconds(5), default));
            Assert.AreEqual(1, starts); Assert.IsTrue(controller.Owns("authorized-instance"));
            Assert.IsFalse(controller.CanStop("authorized-instance"));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => controller.StopAsync("authorized-instance", default));
            foreach (var lease in leases) lease.Dispose();
            Assert.IsTrue(controller.Owns("authorized-instance"));
            Assert.IsFalse(controller.CanStop("different-instance"));
            await controller.StopAsync("authorized-instance", default);
            Assert.IsFalse(controller.Owns("authorized-instance"));
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
            if (controller.CanStop("authorized-instance")) await controller.StopAsync("authorized-instance", default);
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try { Directory.Delete(root, true); break; }
                catch (IOException) when (attempt < 19) { await Task.Delay(50); }
            }
        }
    }
}

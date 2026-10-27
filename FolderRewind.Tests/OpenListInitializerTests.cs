using FolderRewind.Services;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class OpenListInitializerTests
{
    private string _root = null!, _exe = null!;
    [TestInitialize] public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewind-init-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root); _exe = Path.Combine(_root, "fake.exe"); File.WriteAllText(_exe, "fixture");
    }
    [TestCleanup] public void Cleanup() => Directory.Delete(_root, true);
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    [TestMethod]
    public async Task FreshInitializationPassesPasswordOnlyToChildAndKeepsOtherData()
    {
        var root = Path.Combine(_root, "instances"); Directory.CreateDirectory(root);
        var original = Path.Combine(root, "existing-user-data"); File.WriteAllText(original, "keep");
        ProcessStartInfo? captured = null;
        var result = await OpenListInitializer.InitializeAsync(_exe, root, "test-only-password", FreePort(), [], default, async (start, token) =>
        {
            if (start.ArgumentList[0] == "version") return "OpenList v4.2.6";
            captured = start;
            Assert.AreEqual("admin", start.ArgumentList[0]);
            Assert.AreEqual("test-only-password", start.Environment["OPENLIST_ADMIN_PASSWORD"]);
            Assert.IsFalse(start.ArgumentList.Any(a => a.Contains("test-only-password")));
            var path = start.ArgumentList[start.ArgumentList.IndexOf("--config") + 1];
            var json = await File.ReadAllTextAsync(path, token);
            Assert.DoesNotContain("test-only-password", json);
            using var doc = JsonDocument.Parse(json);
            Assert.AreEqual("127.0.0.1", doc.RootElement.GetProperty("scheme").GetProperty("address").GetString());
            Assert.IsFalse(doc.RootElement.GetProperty("log").GetProperty("enable").GetBoolean());
            var db = new byte[128]; Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(db, 0);
            await File.WriteAllBytesAsync(doc.RootElement.GetProperty("database").GetProperty("db_file").GetString()!, db, token);
            return "sensitive child output: test-only-password";
        });
        Assert.AreEqual("keep", File.ReadAllText(original));
        Assert.IsFalse(captured!.Environment.ContainsKey("OPENLIST_ADMIN_PASSWORD"));
        Assert.IsFalse(result.AllowStartOnDemand);
        Assert.IsTrue(File.Exists(result.ConfigFilePath));
        Assert.DoesNotContain("test-only-password", File.ReadAllText(Path.Combine(result.WorkingDirectory, ".folderrewind-initialization.json")));
    }
    [TestMethod]
    public async Task FailureRetainsOwnedDirectoryWithoutLeakingSecretOrStartingServer()
    {
        var root = Path.Combine(_root, "instances");
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => OpenListInitializer.InitializeAsync(
            _exe, root, "sensitive-fixture", FreePort(), [], default, (start, token) =>
                start.ArgumentList[0] == "version" ? Task.FromResult("OpenList v4.2.6") : throw new IOException("sensitive-fixture")));
        Assert.DoesNotContain("sensitive-fixture", error.ToString());
        Assert.HasCount(1, Directory.GetDirectories(root));
    }
    [TestMethod]
    public async Task OccupiedPortAndUnsupportedVersionDoNotInitialize()
    {
        var root = Path.Combine(_root, "instances");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => OpenListInitializer.InitializeAsync(_exe, root, "password", ((IPEndPoint)listener.LocalEndpoint).Port, [], default,
                (_, _) => Task.FromResult("OpenList v4.2.6")));
        }
        finally { listener.Stop(); }
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => OpenListInitializer.InitializeAsync(_exe, root, "password", FreePort(), [], default,
            (_, _) => Task.FromResult("OpenList v4.2.60")));
        Assert.IsFalse(Directory.Exists(root));
    }
    [TestMethod]
    public async Task ProtectedDirectoryAndCancellationAreRejectedBeforeCreation()
    {
        var root = Path.Combine(_root, "protected", "instances");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => OpenListInitializer.InitializeAsync(_exe, root, "password", FreePort(), [Path.Combine(_root, "protected")], default));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => OpenListInitializer.InitializeAsync(_exe, root, "password", FreePort(), [], canceled.Token,
            (_, _) => Task.FromResult("OpenList v4.2.6")));
        Assert.IsFalse(Directory.Exists(root));
    }
    [TestMethod]
    public async Task PinnedRealBinaryInitializesIsolatedDatabaseWithoutStartingServer()
    {
        var executable = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_OPENLIST");
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable)) Assert.Inconclusive("Set FOLDERREWIND_TEST_OPENLIST for isolated local initialization.");
        var port = FreePort();
        var settings = await OpenListInitializer.InitializeAsync(executable!, Path.Combine(_root, "instances"), "isolated-test-" + Guid.NewGuid().ToString("N"), port, [], default);
        Assert.IsTrue(File.Exists(Path.Combine(settings.DataDirectory, "data.db")));
        var listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Start(); } finally { listener.Stop(); }
        var owned = new OwnedCloudDependencyProcess();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        async Task<bool> Probe(CancellationToken token)
        {
            try { using var response = await client.GetAsync(settings.ServiceBaseUri, token); return response.IsSuccessStatusCode; }
            catch (HttpRequestException) { return false; }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { return false; }
        }
        ProcessStartInfo Start()
        {
            var start = new ProcessStartInfo(executable!) { WorkingDirectory = settings.WorkingDirectory };
            foreach (var argument in new[] { "server", "--data", settings.DataDirectory, "--config", settings.ConfigFilePath }) start.ArgumentList.Add(argument);
            return start;
        }
        try
        {
            using (await owned.AcquireAsync("isolated-instance", true, Start, Probe, TimeSpan.FromSeconds(15), default))
            { Assert.IsTrue(owned.Owns("isolated-instance")); Assert.IsFalse(owned.CanStop("isolated-instance")); }
            Assert.IsTrue(owned.CanStop("isolated-instance"));
        }
        finally { if (owned.CanStop("isolated-instance")) await owned.StopAsync("isolated-instance", default); }
    }
}

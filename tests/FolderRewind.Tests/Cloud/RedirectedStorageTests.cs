using System.Diagnostics;
using FolderRewind.History.Application;
using FolderRewind.History.Retention;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class RedirectedStorageTests
{
    private string _root = null!;
    private readonly List<string> _links = [];
    [TestInitialize] public void Initialize()
    {
        // The synthetic WSH child uses legacy path limits; preserve the storage layout under a short fixture root.
        _root = Path.Combine(Path.GetTempPath(), "fr68", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    [TestCleanup] public void Cleanup()
    {
        foreach (var link in _links.AsEnumerable().Reverse())
            if (Directory.Exists(link)) Directory.Delete(link);
        Directory.Delete(_root, true);
    }

    private void Junction(string path, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", path, target }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.WaitForExit(); Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd());
        _links.Add(path);
    }

    [TestMethod]
    [DataRow("LocalState")]
    [DataRow("LocalCache")]
    public async Task RedirectedStorageSupportsCredentialsChildProcessWorkspacesExportAndRewrite(string name)
    {
        var physical = Path.Combine(_root, "physical"); Directory.CreateDirectory(physical);
        var link = Path.Combine(_root, name); Junction(link, physical);
        var credentials = Path.Combine(link, "FolderRewind", "credentials");
        var directory = Path.Combine(credentials, "rclone", "connection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "rclone.conf");
        File.WriteAllText(source, "[test]\ntype = webdav\nurl = https://example.invalid/\n");
        RcloneCredentialStore.RegisterFresh(source, File.ReadAllBytes(source));
        var tasks = Path.Combine(credentials, "rclone-tasks");
        await RcloneCredentialRefresh.PrepareStoreAsync(source, tasks, true, default);
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cscript.exe");
        using (var context = new RcloneExecutionContext(executable, source, _root, "test:backup", tasks, true))
        {
            Assert.AreEqual(source, context.ConfigPath); // Persisted path/StorageKey must not be rewritten.
            Assert.IsTrue(RcloneCredentialStore.HasFreshOrigin(source, true));
            Assert.ThrowsExactly<IOException>(() => Directory.Delete(link));
            var script = Path.Combine(_root, "read-config.js");
            File.WriteAllText(script, "var fs = new ActiveXObject('Scripting.FileSystemObject'); var f = fs.OpenTextFile(WScript.Arguments.Item(1), 1); var text = f.ReadAll(); f.Close(); f = fs.CreateTextFile(WScript.ScriptFullName + '.read', true); f.Write(text); f.Close(); WScript.Echo('snapshot-readable');");
            var start = context.CreateStartInfo(["lsf", "test:backup"]);
            start.ArgumentList.Insert(0, script); start.ArgumentList.Insert(0, "//nologo");
            await RcloneConnectionService.RunAsync(start, TimeSpan.FromSeconds(10), default);
            Assert.AreEqual(File.ReadAllText(source), File.ReadAllText(script + ".read"));
        }
        var workspace = Path.Combine(link, "workspace");
        HostArtifactWorkspace.CreatePrivateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "payload"), "synthetic");
        HostArtifactWorkspace.ValidateOwnedDirectory(workspace);
        HostArtifactWorkspace.DeleteOwnedDirectory(workspace);
        Assert.IsFalse(Directory.Exists(workspace));
        HistoryVersionExportService.RequireDestination(Path.Combine(link, "export"), []);
        using (HistoryRewriteStoragePaths.BindStorage(Path.Combine(link, ".folderrewind", "rewrites"), link))
            Assert.ThrowsExactly<IOException>(() => Directory.Delete(link));
        Directory.Delete(link); // All successful operations released the redirection binding.
        Assert.IsTrue(File.Exists(Path.Combine(physical, "FolderRewind", "credentials", "rclone", Path.GetFileName(directory), "rclone.conf")));
    }

    [TestMethod]
    public async Task InternalTaskAndRewriteLinksStillFailWithoutTouchingTheirTargets()
    {
        var outside = Path.Combine(_root, "outside"); Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel"); File.WriteAllText(sentinel, "keep");
        var tasks = Path.Combine(_root, "tasks"); Junction(tasks, outside);
        var config = Path.Combine(_root, "rclone.conf"); File.WriteAllText(config, "[test]\ntype = webdav\n");
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => RcloneCredentialRefresh.PrepareStoreAsync(config, tasks, true, default));
        Assert.AreEqual("connection.path-unsafe", RcloneConnectionDiagnostics.Code(error));
        Assert.ThrowsExactly<IOException>(() => HostArtifactWorkspace.CreatePrivateDirectory(tasks));
        var backup = Path.Combine(_root, "backup"); Directory.CreateDirectory(backup);
        Junction(Path.Combine(backup, ".folderrewind"), outside);
        Assert.ThrowsExactly<IOException>(() => HistoryRewriteStoragePaths.BindStorage(Path.Combine(backup, ".folderrewind", "rewrites"), backup));
        Assert.AreEqual("keep", File.ReadAllText(sentinel));
        Assert.HasCount(1, Directory.GetFileSystemEntries(outside));
    }

    [TestMethod]
    public void ExportThroughAliasStillRejectsPhysicalSourceOverlap()
    {
        var source = Path.Combine(_root, "source"); Directory.CreateDirectory(source);
        var link = Path.Combine(_root, "alias"); Junction(link, source);
        Assert.ThrowsExactly<IOException>(() => HistoryVersionExportService.RequireDestination(Path.Combine(link, "export"), [source]));
        Assert.IsFalse(Directory.Exists(Path.Combine(source, "export")));
    }

    [TestMethod]
    public async Task CandidateCleanupNeverFollowsAReplacedConnectionDirectory()
    {
        var outside = Path.Combine(_root, "outside"); Directory.CreateDirectory(outside);
        var externalCredential = Path.Combine(outside, "rclone.conf"); File.WriteAllText(externalCredential, "keep");
        var alias = Path.Combine(_root, "connection-test"); Junction(alias, outside);
        var candidate = new OwnedRcloneConnection(Path.Combine(alias, "rclone.conf"), "test:");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => candidate.DisposeAsync().AsTask());
        Assert.AreEqual("keep", File.ReadAllText(externalCredential));
    }

    [TestMethod]
    public async Task RewriteCleanupRejectsInternalFileLinksBeforeChangingOutsideAttributes()
    {
        var paths = new HistoryRepositoryPaths(Path.Combine(_root, "repository"));
        await using var history = new HistoryRuntime(new FileHistoryRepository(new HistoryConfigId("link-cleanup"), paths));
        await history.InitializeAsync();
        var owned = Path.Combine(paths.RepositoryRoot, "owned"); Directory.CreateDirectory(owned);
        var outside = Path.Combine(_root, "outside.txt"); File.WriteAllText(outside, "keep");
        File.SetAttributes(outside, File.GetAttributes(outside) | FileAttributes.ReadOnly);
        var link = Path.Combine(owned, "linked.txt"); File.CreateSymbolicLink(link, outside);
        try
        {
            Assert.ThrowsExactly<IOException>(() => new HistoryChainRewriteJournalStore(history).CleanupOwnedTree(owned));
            Assert.IsTrue(File.Exists(link));
            Assert.AreEqual("keep", File.ReadAllText(outside));
            Assert.AreNotEqual((FileAttributes)0, File.GetAttributes(outside) & FileAttributes.ReadOnly);
        }
        finally { File.Delete(link); File.SetAttributes(outside, FileAttributes.Normal); }
    }
}

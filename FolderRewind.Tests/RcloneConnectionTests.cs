using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class RcloneConnectionTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize() { _root = Path.Combine(Path.GetTempPath(), "FolderRewindConnectionTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { Directory.Delete(_root, true); }

    [TestMethod]
    public void SnapshotKeepsQueuedTargetAndRejectsChangedSourceFile()
    {
        var exe = Path.Combine(_root, "fake.exe"); File.WriteAllText(exe, "test");
        var config = Path.Combine(_root, "rclone.conf"); File.WriteAllText(config, "[same]\ntype = webdav\nurl = https://first.test/\n", new System.Text.UTF8Encoding(true));
        using var context = new RcloneExecutionContext(exe, config, _root, "same:backup", _root);
        var snapshot = context.SnapshotPath;
        var start = context.CreateStartInfo(["lsf", "same:backup"]);
        Assert.AreEqual(snapshot, start.ArgumentList[1]);
        Assert.IsFalse(start.Environment.Keys.Any(k => k.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)));
        File.WriteAllText(config, "[same]\ntype = webdav\nurl = https://second.test/\n");
        Assert.ThrowsExactly<InvalidOperationException>(() => context.CreateStartInfo(["lsf", "same:backup"]));
        StringAssert.Contains(File.ReadAllText(snapshot), "https://first.test/");
        context.Dispose();
        Assert.IsFalse(File.Exists(snapshot));
        StringAssert.Contains(File.ReadAllText(config), "https://second.test/");
    }

    [TestMethod]
    public void ExpiredSnapshotCleanupRespectsActiveOwnershipAndUnknownFiles()
    {
        var exe = Path.Combine(_root, "fake.exe"); File.WriteAllText(exe, "test");
        var config = Path.Combine(_root, "rclone.conf"); File.WriteAllText(config, "[remote]\ntype = local\n");
        using var active = new RcloneExecutionContext(exe, config, _root, "remote:backup", _root);
        var directory = Path.GetDirectoryName(active.SnapshotPath)!;
        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(-3));
        Assert.AreEqual(0, RcloneExecutionContext.CleanupExpired(_root, DateTimeOffset.UtcNow));
        Assert.IsTrue(File.Exists(active.SnapshotPath));
        var stale = Path.Combine(_root, "task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "ownership.lock"), "");
        File.WriteAllText(Path.Combine(stale, "rclone.conf"), "sensitive");
        File.WriteAllText(Path.Combine(stale, "user-file"), "keep");
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-3));
        Assert.AreEqual(0, RcloneExecutionContext.CleanupExpired(_root, DateTimeOffset.UtcNow));
        File.Delete(Path.Combine(stale, "user-file"));
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-3));
        Assert.AreEqual(1, RcloneExecutionContext.CleanupExpired(_root, DateTimeOffset.UtcNow));
        Assert.IsFalse(Directory.Exists(stale));
        Assert.IsTrue(File.Exists(config));
    }

    [TestMethod]
    public void SameRemoteNameRemainsBoundToItsOwnConfigAndEnvironmentIsStripped()
    {
        var exe = Path.Combine(_root, "fake.exe"); File.WriteAllText(exe, "test");
        var firstConfig = Path.Combine(_root, "first.conf"); var secondConfig = Path.Combine(_root, "second.conf");
        File.WriteAllText(firstConfig, "[same]\ntype = webdav\nurl = https://first.test/\n");
        File.WriteAllText(secondConfig, "[same]\ntype = webdav\nurl = https://second.test/\n");
        const string key = "RCLONE_ONBOARDING_TEST_VALUE";
        var previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, "must-not-inherit");
            using var first = new RcloneExecutionContext(exe, firstConfig, _root, "same:backup", _root);
            using var second = new RcloneExecutionContext(exe, secondConfig, _root, "same:backup", _root);
            var start = first.CreateStartInfo(["lsf", "same:backup"]);
            Assert.IsFalse(start.Environment.ContainsKey(key));
            Assert.AreNotEqual(first.SnapshotPath, second.SnapshotPath);
            StringAssert.Contains(File.ReadAllText(first.SnapshotPath), "https://first.test/");
            StringAssert.Contains(File.ReadAllText(second.SnapshotPath), "https://second.test/");
            if (OperatingSystem.IsWindows())
            {
                var security = new DirectoryInfo(Path.GetDirectoryName(first.SnapshotPath)!).GetAccessControl();
                Assert.IsTrue(security.AreAccessRulesProtected);
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
                Assert.HasCount(1, rules);
                Assert.IsNotNull(rules[0]);
                Assert.AreEqual(identity.User, rules[0]!.IdentityReference);
            }
        }
        finally { Environment.SetEnvironmentVariable(key, previous); }
    }

    [TestMethod]
    public void BoundConnectionRejectsConflictingArgumentsAndEncryptedConfig()
    {
        var exe = Path.Combine(_root, "fake.exe"); File.WriteAllText(exe, "test");
        var config = Path.Combine(_root, "rclone.conf"); File.WriteAllText(config, "[remote]\ntype = alias\nremote = other:dir\n[other]\ntype = webdav\nurl = https://test/\n");
        using (var context = new RcloneExecutionContext(exe, config, _root, "remote:dir", _root))
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => context.CreateStartInfo(["lsf", "remote:dir", "--config=other.conf"]));
            Assert.ThrowsExactly<InvalidOperationException>(() => context.CreateStartInfo(["copyto", "file", "remote:dir", "--alias-remote=other:changed"]));
            Assert.ThrowsExactly<InvalidOperationException>(() => context.CreateStartInfo(["lsf", "remote:dir", "--header=Authorization: other"]));
            Assert.ThrowsExactly<InvalidOperationException>(() => context.CreateStartInfo(["config", "update", "remote", "url", "https://changed.test/"]));
            File.AppendAllText(config, "user = changed\n");
            Assert.ThrowsExactly<InvalidOperationException>(context.RequireUnchanged);
        }
        File.WriteAllText(config, "RCLONE_ENCRYPT_V0:\nencrypted-test\n");
        Assert.ThrowsExactly<InvalidOperationException>(() => new RcloneExecutionContext(exe, config, _root, "remote:dir", _root));
    }

    [TestMethod]
    public void RemoteInspectionEvidenceMatchesTheExactBytesUsedByExecution()
    {
        var exe = Path.Combine(_root, "fake.exe"); File.WriteAllText(exe, "test");
        var config = Path.Combine(_root, "rclone.conf"); File.WriteAllText(config, "[same]\ntype = webdav\nurl = https://first.test/\n");
        var inspected = RcloneConnectionService.InspectRemotes(config);
        using var context = new RcloneExecutionContext(exe, config, _root, "same:backup", _root);
        Assert.AreEqual(inspected.RevisionEvidence, context.RevisionEvidence);
        Assert.AreEqual("same", inspected.Remotes.Single().Name);
        File.AppendAllText(config, "user = replaced\n");
        Assert.AreNotEqual(inspected.RevisionEvidence, RcloneConnectionService.InspectRemotes(config).RevisionEvidence);
        Assert.ThrowsExactly<InvalidOperationException>(context.RequireUnchanged);
    }

    [TestMethod]
    public void WebDavUrlRejectsImplicitRemoteHttpAndEmbeddedCredentials()
    {
        Assert.ThrowsExactly<ArgumentException>(() => RcloneConnectionService.ValidateWebDavUrl("http://remote.test/dav/"));
        Assert.ThrowsExactly<ArgumentException>(() => RcloneConnectionService.ValidateWebDavUrl("https://user:pass@remote.test/dav/"));
        Assert.AreEqual("http", RcloneConnectionService.ValidateWebDavUrl("http://127.0.0.1:5244/dav/").Scheme);
        Assert.AreEqual("http", RcloneConnectionService.ValidateWebDavUrl("http://remote.test/dav/", true).Scheme);
    }

    [TestMethod]
    public void DirectoryPagingContinuesInBoundedBatchesAndStopsAtTheInteractionBudget()
    {
        var names = Enumerable.Range(0, 1020).Select(i => "directory-" + i).ToArray();
        var visited = new List<string>(); var offset = 0;
        RemoteDirectoryResult page;
        do
        {
            page = RcloneConnectionService.PageDirectories(names, offset);
            Assert.IsLessThanOrEqualTo(200, page.Names.Count);
            visited.AddRange(page.Names); offset = page.NextOffset;
        } while (offset >= 0);
        Assert.HasCount(1000, visited);
        Assert.HasCount(1000, visited.Distinct().ToArray());
        Assert.IsTrue(page.Truncated);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RcloneConnectionService.PageDirectories(names, 1000));
    }

    [TestMethod]
    public void StandardArgumentParserPreservesSpacesUnicodeAndEmptyArguments()
    {
        CollectionAssert.AreEqual(new[] { "copyto", "C:\\with space\\保存.7z", "same:backup/file", "" },
            ProcessArgumentTokenizer.Parse("copyto \"C:\\with space\\保存.7z\" \"same:backup/file\" \"\"").ToArray());
        Assert.ThrowsExactly<ArgumentException>(() => ProcessArgumentTokenizer.Parse("copyto \"unfinished"));
    }

}

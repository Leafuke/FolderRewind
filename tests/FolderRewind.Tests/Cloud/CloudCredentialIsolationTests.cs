using FolderRewind.Models;
using FolderRewind.Services;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class CloudCredentialIsolationTests
{
    private string _root = null!;
    private string Tasks => Path.Combine(_root, "tasks");
    private string Executable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cscript.exe");
    [TestInitialize]
    public void Initialize() { _root = Path.Combine(Path.GetTempPath(), "FolderRewindIssue66Unit", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup]
    public void Cleanup()
    {
        var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FolderRewindIssue66Unit")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        Directory.Delete(_root, true);
    }
    private string Config(string name, string? content = null)
    {
        var path = Path.Combine(_root, name + ".conf");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.Write(Encoding.UTF8.GetBytes(content ?? "[test]\ntype = local\n"));
        return path;
    }
    private RcloneExecutionContext Context(string source) => new(Executable, source, _root, "test:backup", Tasks, true);
    private string LegacyOrphan()
    {
        var directory = Path.Combine(Tasks, "task-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "rclone.conf"), "unique old credential evidence");
        return directory;
    }
    private string Registered(string source, bool scoped)
    {
        var directory = Path.Combine(scoped ? RcloneCredentialStore.Scope(Tasks, source) : Tasks, "task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        RcloneCredentialRefresh.RegisterSnapshot(directory, source, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))));
        File.Copy(source, Path.Combine(directory, "rclone.conf")); File.WriteAllText(Path.Combine(directory, "ownership.lock"), "");
        return directory;
    }

    [TestMethod]
    [DataRow(false, 0, 5, "connection.metadata-unavailable")]
    [DataRow(true, 0, 0, "connection.metadata-unavailable")]
    [DataRow(true, 2, 0, "connection.hardlink-unsafe")]
    public void MetadataQueryFailureAndHardlinksHaveDistinctEvidence(bool succeeded, int links, int error, string code)
    {
        var source = Config("metadata");
        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => RcloneCredentialRefresh.RequireSingleLink(source, new(succeeded, (uint)links, error)));
        Assert.AreEqual(code, RcloneConnectionDiagnostics.Code(failure));
        var details = RcloneConnectionDiagnostics.Details(failure)!;
        Assert.AreEqual(succeeded ? (uint)links : null, details.LinkCount);
        Assert.AreEqual(error, details.Win32Error ?? 0);
        Assert.AreEqual(RcloneConnectionDiagnostics.StorageKey(source), details.CredentialKey);
    }

    [TestMethod]
    public void DuplicateKeysReportLineWithoutExposingValuesOrExceptionData()
    {
        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => RcloneCredentialRefresh.TargetRevision(Encoding.UTF8.GetBytes(
            "[test]\ntype = webdav\npass = first-secret\npass = second-secret\n")));
        failure.Data["secret"] = "private-data";
        Assert.AreEqual("connection.config-invalid", RcloneConnectionDiagnostics.Code(failure));
        var details = RcloneConnectionDiagnostics.Details(failure)!;
        Assert.AreEqual(4, details.LineNumber);
        var text = details.ToLogString();
        foreach (var secret in new[] { "first-secret", "second-secret", "private-data", _root }) Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        var unrelated = RcloneConnectionDiagnostics.Capture(new IOException("pass = unrelated-secret"));
        Assert.DoesNotContain("unrelated-secret", unrelated.ToLogString(), StringComparison.Ordinal);
    }

    [TestMethod]
    public void ContextsPartitionByStorageAndShareOnePartitionForTheSameFile()
    {
        var source = Config("first"); var other = Config("second");
        using var first = Context(source); using var same = Context(source); using var second = Context(other);
        Assert.AreEqual(first.TaskRoot, same.TaskRoot); Assert.AreNotEqual(first.TaskRoot, second.TaskRoot);
        Assert.AreEqual(RcloneCredentialStore.Scope(Tasks, source), first.TaskRoot);
        Assert.AreEqual(Tasks, first.SharedTaskRoot);
        Assert.IsTrue(File.Exists(first.SnapshotPath)); Assert.AreNotEqual(first.SnapshotPath, same.SnapshotPath);
    }

    [TestMethod]
    public async Task DamagedOtherPartitionDoesNotBlockOrBecomePendingForThisStorage()
    {
        var first = Config("first"); var second = Config("second");
        var directory = Registered(first, true);
        File.WriteAllText(Path.Combine(directory, "attempt.json"), "damaged-private-evidence");
        var before = File.ReadAllBytes(Path.Combine(directory, "rclone.conf"));
        await RcloneCredentialRefresh.PrepareStoreAsync(second, Tasks, true, default);
        Assert.IsFalse(RcloneCredentialRefresh.HasPendingEvidence(Tasks, second));
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => RcloneCredentialRefresh.PrepareStoreAsync(first, Tasks, true, default));
        Assert.AreEqual("connection.evidence-invalid", RcloneConnectionDiagnostics.Code(failure));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(directory, "rclone.conf")));
        Assert.IsTrue(RcloneCredentialRefresh.HasPendingEvidence(Tasks, first));
    }

    [TestMethod]
    public async Task AttributedForeignLegacyPayloadIsNotParsedOrRecovered()
    {
        var first = Config("first"); var second = Config("second");
        var directory = Registered(first, false);
        var attempt = Path.Combine(directory, "attempt.json");
        File.WriteAllText(attempt, JsonSerializer.Serialize(new { Version = 1, SourcePath = first, TokenSections = 42 }));
        await RcloneCredentialRefresh.PrepareStoreAsync(second, Tasks, false, default);
        Assert.IsTrue(File.Exists(attempt)); Assert.IsTrue(File.Exists(Path.Combine(directory, "rclone.conf")));
        Assert.IsFalse(RcloneCredentialRefresh.HasPendingEvidence(Tasks, second));
    }

    [TestMethod]
    public async Task MatchingSnapshotWithoutOwnershipCannotBeSilentlySkipped()
    {
        var source = Config("missing-lock");
        RcloneCredentialStore.RegisterFresh(source, File.ReadAllBytes(source));
        var directory = Registered(source, true);
        File.Delete(Path.Combine(directory, "ownership.lock"));
        var snapshot = Path.Combine(directory, "rclone.conf");
        File.WriteAllText(snapshot, "unique snapshot bytes");
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => RcloneCredentialRefresh.PrepareStoreAsync(source, Tasks, true, default));
        Assert.AreEqual("connection.evidence-invalid", RcloneConnectionDiagnostics.Code(failure));
        Assert.AreEqual("unique snapshot bytes", File.ReadAllText(snapshot));
    }

    [TestMethod]
    public async Task UnknownLegacyEvidenceBlocksOldStorageAndPersistsAcrossFreshCredentialReopen()
    {
        var old = Config("old"); var fresh = Config("fresh"); var directory = LegacyOrphan();
        var evidence = File.ReadAllBytes(Path.Combine(directory, "rclone.conf"));
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => RcloneCredentialRefresh.PrepareStoreAsync(old, Tasks, true, default));
        Assert.AreEqual("connection.snapshot-unattributed", RcloneConnectionDiagnostics.Code(failure));
        RcloneCredentialStore.RegisterFresh(fresh, File.ReadAllBytes(fresh));
        await RcloneCredentialRefresh.PrepareStoreAsync(fresh, Tasks, true, default);
        using (var context = Context(fresh)) Assert.IsTrue(File.Exists(context.SnapshotPath));
        await RcloneCredentialRefresh.PrepareStoreAsync(fresh, Tasks, true, default);
        Assert.IsFalse(RcloneCredentialRefresh.HasPendingEvidence(Tasks, fresh, true));
        Assert.IsTrue(RcloneCredentialRefresh.HasPendingEvidence(Tasks, old, true));
        CollectionAssert.AreEqual(evidence, File.ReadAllBytes(Path.Combine(directory, "rclone.conf")));
    }

    [TestMethod]
    [DataRow("damaged")]
    [DataRow("copied")]
    [DataRow("migration")]
    public async Task InvalidOrMigratedOriginCannotExemptUnknownLegacyEvidence(string mode)
    {
        var source = Config("source"); var target = Config("target"); LegacyOrphan();
        RcloneCredentialStore.RegisterFresh(source, File.ReadAllBytes(source));
        if (mode == "damaged") File.WriteAllText(target + RcloneCredentialStore.OriginSuffix, "invalid-origin");
        if (mode == "copied") File.Copy(source + RcloneCredentialStore.OriginSuffix, target + RcloneCredentialStore.OriginSuffix);
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => RcloneCredentialRefresh.PrepareStoreAsync(target, Tasks, true, default));
        Assert.AreEqual(mode == "migration" ? "connection.snapshot-unattributed" : "connection.evidence-invalid", RcloneConnectionDiagnostics.Code(failure));
    }

    [TestMethod]
    public async Task FreshOriginStillBlocksItsOwnUnrecordedTokenChangeAndPreservesCancellationEvidence()
    {
        var source = Config("oauth", "[test]\ntype = box\ntoken = {\"access_token\":\"old\"}\n");
        RcloneCredentialStore.RegisterFresh(source, File.ReadAllBytes(source));
        var directory = Registered(source, false);
        var snapshot = Path.Combine(directory, "rclone.conf"); File.WriteAllText(snapshot, File.ReadAllText(snapshot).Replace("old", "unique-new"));
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => RcloneCredentialRefresh.PrepareStoreAsync(source, Tasks, true, default));
        Assert.AreEqual("connection.refresh-conflict", RcloneConnectionDiagnostics.Code(failure));
        await new OwnedRcloneConnection(source, "test:").DisposeAsync();
        Assert.IsTrue(File.Exists(source)); StringAssert.Contains(File.ReadAllText(snapshot), "unique-new");
    }

    [TestMethod]
    public async Task ProbePreservesGuardDiagnosticInsteadOfReturningGenericFailure()
    {
        var source = Config("linked");
        Assert.IsTrue(CreateHardLink(Path.Combine(_root, "second-link.conf"), source, IntPtr.Zero));
        using var context = Context(source);
        var result = await RcloneConnectionService.VerifyWriteAsync(context);
        Assert.IsFalse(result.ReadWriteVerified);
        Assert.AreEqual("connection.hardlink-unsafe", result.Failure?.Code);
        Assert.AreEqual((uint)2, result.Failure?.LinkCount);
        StringAssert.Contains(result.Diagnostic, "[connection.hardlink-unsafe]");
    }

    [TestMethod]
    public void AncestorReparsePointReportsDepthWithoutPublishingPath()
    {
        var real = Path.Combine(_root, "real"); Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "test.conf"), "[test]\ntype = local\n");
        var link = Path.Combine(_root, "link"); Directory.CreateSymbolicLink(link, real);
        var source = Path.Combine(link, "test.conf");
        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => RcloneCredentialRefresh.RejectReparsePoints(source, _root));
        var details = RcloneConnectionDiagnostics.Details(failure)!;
        Assert.AreEqual("connection.path-unsafe", details.Code); Assert.AreEqual(1, details.AncestorDepth);
        Assert.AreNotEqual(0, details.Attributes & (int)FileAttributes.ReparsePoint);
        Assert.DoesNotContain(_root, details.ToLogString(), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ExplicitWebDavCreationWritesOriginAndCancelDoesNotDeleteLegacyEvidence()
    {
        var executable = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_RCLONE");
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable)) Assert.Inconclusive("Set FOLDERREWIND_TEST_RCLONE.");
        var directory = LegacyOrphan();
        await using var connection = await RcloneConnectionService.CreateWebDavAsync(executable, Path.Combine(_root, "credentials"),
            "http://127.0.0.1:5244/dav/", "synthetic", "synthetic");
        var source = connection.ConfigPath;
        Assert.IsTrue(RcloneCredentialStore.HasFreshOrigin(source, true));
        await RcloneCredentialRefresh.PrepareStoreAsync(source, Tasks, true, default);
        await connection.DisposeAsync();
        Assert.IsFalse(File.Exists(source)); Assert.IsFalse(File.Exists(source + RcloneCredentialStore.OriginSuffix));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "rclone.conf")));
    }

    [TestMethod]
    public async Task ScopedContextStillUsesTheLegacyExternalCredentialLock()
    {
        var source = Config("external"); Directory.CreateDirectory(Tasks);
        var path = Path.Combine(Tasks, "credential-" + RcloneConnectionDiagnostics.StorageKey(source) + ".lock");
        using var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var context = new RcloneExecutionContext(Executable, source, _root, "test:backup", Tasks);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
        {
            await using var lease = await RcloneCredentialRefresh.BeginAsync(context.CreateStartInfo(["version"]), cancel.Token);
        });
    }

    [TestMethod]
    public async Task ScopedExpiredCleanupRetainsPayloadUntilRecoveryProvesItSettled()
    {
        var source = Config("cleanup"); string directory;
        using (var context = Context(source)) { directory = context.TaskDirectory; context.PreserveSnapshot = true; }
        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(-3));
        Assert.AreEqual(0, RcloneExecutionContext.CleanupExpired(Tasks, DateTimeOffset.UtcNow));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "rclone.conf")));
        await RcloneCredentialRefresh.PrepareStoreAsync(source, Tasks, true, default);
        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(-3));
        Assert.AreEqual(1, RcloneExecutionContext.CleanupExpired(Tasks, DateTimeOffset.UtcNow));
        Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod]
    public void MissingEmptyAndAmbiguousProfilesNeverProduceANavigationTarget()
    {
        var profile = new CloudConnectionProfile { Id = "existing" };
        Assert.IsNull(CloudConnectionNavigationPolicy.FindProfile([profile], null));
        Assert.IsNull(CloudConnectionNavigationPolicy.FindProfile([profile], ""));
        Assert.IsNull(CloudConnectionNavigationPolicy.FindProfile([profile], "missing"));
        Assert.IsNull(CloudConnectionNavigationPolicy.FindProfile([profile, profile], "existing"));
        Assert.AreSame(profile, CloudConnectionNavigationPolicy.FindProfile([profile], "existing"));
    }

    [TestMethod]
    [DataRow("webdav", true)]
    [DataRow("box", false)]
    public async Task UncertainStaticWebDavTaskDoesNotDisableCredentialsOrReplayTheOldTask(string backend, bool reusable)
    {
        var source = Config("starting", "[test]\ntype = " + backend + "\nurl = https://example.invalid/\n"
            + (backend == "box" ? "token = {\"access_token\":\"old\"}\n" : "vendor = other\nuser = synthetic\npass = synthetic\n"));
        string directory;
        using (var context = Context(source))
        {
            directory = context.TaskDirectory;
            await using var lease = await RcloneCredentialRefresh.BeginAsync(context.CreateStartInfo(["version"]), default);
            await lease!.StartingAsync(default);
        }
        var attempt = File.ReadAllBytes(Path.Combine(directory, "attempt.json"));
        if (reusable) await RcloneCredentialRefresh.PrepareStoreAsync(source, Tasks, true, default);
        else
        {
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => RcloneCredentialRefresh.PrepareStoreAsync(source, Tasks, true, default));
            Assert.AreEqual("connection.refresh-conflict", RcloneConnectionDiagnostics.Code(error));
        }
        CollectionAssert.AreEqual(attempt, File.ReadAllBytes(Path.Combine(directory, "attempt.json")));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "rclone.conf"))); // Remote outcome is still unknown.
    }

    [TestMethod]
    public async Task ChangedStaticSnapshotCannotBeTreatedAsUnchangedWebDav()
    {
        var source = Config("changed-static", "[test]\ntype = webdav\nuser = old\n");
        string snapshot;
        using (var context = Context(source))
        {
            snapshot = context.SnapshotPath;
            await using var lease = await RcloneCredentialRefresh.BeginAsync(context.CreateStartInfo(["version"]), default);
            await lease!.StartingAsync(default);
            File.WriteAllText(snapshot, "[test]\ntype = webdav\nuser = new\n");
        }
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => RcloneCredentialRefresh.PrepareStoreAsync(source, Tasks, true, default));
        StringAssert.Contains(File.ReadAllText(snapshot), "user = new");
        StringAssert.Contains(File.ReadAllText(source), "user = old");
    }

    [TestMethod]
    public async Task FreshStorageIsIndependentOfMoreThanAThousandEmptyLegacyDirectories()
    {
        var source = Config("many-legacy"); RcloneCredentialStore.RegisterFresh(source, File.ReadAllBytes(source));
        for (var i = 0; i < 1001; i++) Directory.CreateDirectory(Path.Combine(Tasks, "task-" + Guid.NewGuid().ToString("N")));
        var unknown = LegacyOrphan(); var evidence = File.ReadAllBytes(Path.Combine(unknown, "rclone.conf"));
        await RcloneCredentialRefresh.PrepareStoreAsync(source, Tasks, true, default);
        using var context = Context(source);
        Assert.IsTrue(File.Exists(context.SnapshotPath));
        CollectionAssert.AreEqual(evidence, File.ReadAllBytes(Path.Combine(unknown, "rclone.conf")));
        using var cancel = new CancellationTokenSource();
        using var iterator = RcloneCredentialStore.Enumerate(Tasks, source, cancel.Token).GetEnumerator();
        Assert.IsTrue(iterator.MoveNext()); cancel.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => iterator.MoveNext());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string path, string existing, IntPtr reserved);
}

using FolderRewind.Services;
using FolderRewind.History.Application;
using FolderRewind.History.Cloud;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.History.Representation;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

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
                Assert.AreEqual(1, rules.Count);
                Assert.AreEqual(identity.User, rules[0].IdentityReference);
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
            Assert.IsTrue(page.Names.Count <= 200);
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

    [TestMethod]
    [DataRow("\"quoted\" 用户", "Unicode-密码#'\"\\")]
    [DataRow("#name", "password with spaces")]
    public async Task WebDavCredentialsRoundTripThroughActualRclone(string user, string password)
    {
        var exe = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_RCLONE_PATH");
        if (string.IsNullOrEmpty(exe)) { Assert.Inconclusive("Set FOLDERREWIND_TEST_RCLONE_PATH for rclone config verification."); return; }
        using var connection = await RcloneConnectionService.CreateWebDavAsync(exe, "http://127.0.0.1:5244/dav/", user, password);
        using var context = new RcloneExecutionContext(exe, connection.ConfigPath, _root, connection.RemoteRoot, _root);
        // The test explicitly inspects its isolated configuration. Config commands are not admitted by the production transfer context.
        var inspect = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "--config", context.SnapshotPath, "config", "dump" }) inspect.ArgumentList.Add(argument);
        var json = await RcloneConnectionService.RunAsync(inspect, TimeSpan.FromSeconds(10), CancellationToken.None);
        using var document = JsonDocument.Parse(json);
        var remote = document.RootElement.GetProperty(connection.RemoteRoot.TrimEnd(':'));
        Assert.AreEqual(user, remote.GetProperty("user").GetString());
        Assert.AreNotEqual(password, remote.GetProperty("pass").GetString());
    }

    [TestMethod]
    [DataRow("test-user", "test-password", false)]
    [DataRow("测试用户", "测试-密码#'\"\\", false)]
    [DataRow("partial-user", "partial-password", true)]
    public async Task ControlledWebDavBrowsesWritesReadsAndPreciselyCleansProbe(string user, string password, bool partial)
    {
        var exe = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_RCLONE_PATH");
        if (string.IsNullOrEmpty(exe)) { Assert.Inconclusive("Set FOLDERREWIND_TEST_RCLONE_PATH for isolated rclone/WebDAV integration validation."); return; }
        var storage = Path.Combine(_root, "remote"); Directory.CreateDirectory(Path.Combine(storage, "nested"));
        File.WriteAllText(Path.Combine(storage, "keep.txt"), "keep");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var serverStart = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "serve", "webdav", storage, "--addr", "127.0.0.1:" + port, "--user", user, "--pass", password }) serverStart.ArgumentList.Add(argument);
        using var server = Process.Start(serverStart)!;
        var drainOut = server.StandardOutput.ReadToEndAsync(); var drainError = server.StandardError.ReadToEndAsync();
        try
        {
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                try { using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port, ready.Token); break; }
                catch (SocketException) { await Task.Delay(100, ready.Token); }
            }
            using var connection = await RcloneConnectionService.CreateWebDavAsync(exe, "http://127.0.0.1:" + port, user, password);
            using var context = new RcloneExecutionContext(exe, connection.ConfigPath, _root, connection.RemoteRoot, _root);
            var directories = await RcloneConnectionService.BrowseAsync(context);
            CollectionAssert.Contains(directories.Names.ToArray(), "nested");
            var probe = await RcloneConnectionService.VerifyWriteAsync(context);
            Assert.IsTrue(probe.ReadWriteVerified, probe.Diagnostic);
            Assert.IsNull(probe.RetainedObject);
            Assert.AreEqual("keep", File.ReadAllText(Path.Combine(storage, "keep.txt")));
            Assert.IsEmpty(Directory.GetFiles(storage, ".folderrewind-probe-*"));
            if (Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_SEVENZIP_PATH") is { } sevenZip)
                await VerifyNativeCloudRoundTripAsync(context, storage, sevenZip, partial);
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
            await Task.WhenAll(drainOut, drainError);
        }
    }

    private async Task VerifyNativeCloudRoundTripAsync(RcloneExecutionContext context, string remoteDirectory, string sevenZip, bool partial)
    {
        var source = Path.Combine(_root, "source"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "保存.txt"), "first backup content");
        var configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        var versionId = VersionId.New(); var sourceId = SourceId.New(); var representationId = RepresentationId.New();
        var scope = partial ? CaptureScope.PartialSource : CaptureScope.FullSource;
        var fidelity = HistoryRecoveryPreview.FidelityFor(scope);
        var version = new SourceVersion(versionId, configId, sourceId, [], DateTimeOffset.UtcNow, null, scope,
            CaptureOutcome.Recovered, [], new("isolated source", ""), "state", HistoryProvenance.Native("controlled-test"),
            creationKind: SourceVersionCreationKind.Recovery);
        var backend = new SevenZipArchiveProcessBackend(() => sevenZip, () => null, false, "__FolderRewind_Internal");
        var archive = await backend.CreateFullAsync(version, source, representationId, Path.Combine(_root, "archive"), CancellationToken.None);
        if (partial) File.WriteAllText(Path.Combine(source, "outside-captured-scope.txt"), "must remain outside the partial export");
        var representation = new VersionRepresentation(representationId, versionId, RepresentationKind.CoreFull, "7z", [], fidelity, null, "state", null);
        await using var first = new HistoryRuntime(new FileHistoryRepository(configId, new(Path.Combine(_root, "first-history"))));
        await first.InitializeAsync();
        var codec = new HistoryPackCodec();
        await first.Repository.CommitAsync(new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, [codec.CreateObject(version), codec.CreateObject(representation)]));
        await first.EnsureIndexCurrentAsync();
        var transport = new RcloneNativeHistoryTransport(context);
        var sync = new HistoryMetadataSyncService(first, transport);
        var uploaded = await new HistoryCloudBackupService(first, transport, token => sync.SyncAsync(token))
            .UploadClosureAsync([representationId], (id, token) => Task.FromResult<string?>(archive.PayloadPath));
        Assert.IsTrue(uploaded.Complete, uploaded.Items.FirstOrDefault()?.Diagnostic);
        File.Delete(archive.PayloadPath); // 恢复不能借用原本的本地归档掩盖远端缺失。
        await using var fresh = new HistoryRuntime(new FileHistoryRepository(configId, new(Path.Combine(_root, "fresh-history"))));
        await fresh.InitializeAsync();
        var readOnly = new ReadOnlyTransport(transport);
        Assert.IsTrue((await new HistoryMetadataSyncService(fresh, readOnly).PullAsync()).Succeeded);
        var replica = (await fresh.Query.GetStorageReplicasAsync(representationId)).Single();
        var manifest = new HistoryReplicaManifest(representationId, replica.ReplicaId, null, replica.ObjectKey, replica.ExpectedSize!.Value, replica.ExpectedStorageSha256!);
        var payload = Path.Combine(_root, "downloaded.payload");
        var downloaded = await new HistoryReplicaSyncService(fresh, readOnly, _ => throw new InvalidOperationException("Readonly metadata callback must not run."), new NoRetirement()).DownloadAsync(manifest, payload);
        Assert.AreEqual(HistoryReplicaOperationStatus.Succeeded, downloaded.Status, downloaded.Diagnostic);
        var local = (await fresh.LocalReplicaCatalogStore.LoadAsync()).Value!;
        var environment = new RepresentationEnvironment(local.Entries, [replica], [replica.ReplicaId]);
        var materializer = new RepresentationRuntime([new CoreArchiveRepresentationHandler(backend), new SmartDeltaRepresentationHandler(backend)]);
        var target = Path.Combine(_root, "restored");
        var assessment = await materializer.AssessVersionAsync(versionId, [representation], environment, AssessmentDepth.Fast, fidelity);
        var preview = HistoryRecoveryPreview.Create(version, new Dictionary<RepresentationId, VersionRepresentation> { [representationId] = representation }, assessment, _ => true, _ => null);
        Assert.IsTrue(preview.CanPrepare);
        Assert.AreEqual(fidelity, preview.RequiredFidelity);
        await new HistoryVersionExportService(materializer).ExportAsync(versionId, [representation], environment, target, [source, fresh.Repository.Paths.RepositoryRoot], requiredFidelity: fidelity);
        Assert.AreEqual("first backup content", File.ReadAllText(Path.Combine(target, "保存.txt")));
        Assert.AreEqual("first backup content", File.ReadAllText(Path.Combine(source, "保存.txt")));
        if (partial)
        {
            Assert.IsFalse(File.Exists(Path.Combine(target, "outside-captured-scope.txt")));
            Assert.IsTrue(File.Exists(Path.Combine(source, "outside-captured-scope.txt")));
        }
        Assert.AreEqual(0, readOnly.WriteRequests);
        Assert.IsEmpty(Directory.GetFiles(remoteDirectory, ".folderrewind-probe-*"));
    }

    private sealed class ReadOnlyTransport(RcloneNativeHistoryTransport inner) : IHistoryMetadataTransport, IHistoryReplicaTransport
    {
        public int WriteRequests { get; private set; }
        private Task Write() { WriteRequests++; throw new UnauthorizedAccessException("Read-only test transport forbids mutation."); }
        public Task<byte[]?> ReadDescriptorAsync(HistoryConfigId id, CancellationToken token) => inner.ReadDescriptorAsync(id, token);
        public Task CreateDescriptorOnceAsync(HistoryConfigId id, byte[] bytes, CancellationToken token) => Write();
        public Task<IReadOnlyList<PackId>> ListPacksAsync(HistoryConfigId id, CancellationToken token) => inner.ListPacksAsync(id, token);
        public Task<byte[]> DownloadPackAsync(HistoryConfigId id, PackId pack, CancellationToken token) => inner.DownloadPackAsync(id, pack, token);
        public Task UploadPackOnceAsync(HistoryConfigId id, PackId pack, byte[] bytes, CancellationToken token) => Write();
        public Task<bool> LegacyHistoryExistsAsync(HistoryConfigId id, CancellationToken token) => Task.FromResult(false);
        public Task UploadAsync(ReplicaId id, string path, CancellationToken token) => Write();
        public Task<HistoryReplicaVerification> VerifyRemoteAsync(ReplicaId id, CancellationToken token) => inner.VerifyRemoteAsync(id, token);
        public Task CommitManifestOnceAsync(HistoryReplicaManifest manifest, CancellationToken token) => Write();
        public Task DownloadAsync(HistoryReplicaManifest manifest, string path, CancellationToken token) => inner.DownloadAsync(manifest, path, token);
        public Task DeletePhysicalAsync(HistoryReplicaManifest manifest, CancellationToken token) => Write();
    }
    private sealed class NoRetirement : IHistoryReplicaRetirementGuard
    {
        public Task EnsureRetirementSafeAsync(StorageReplica replica, bool releaseVersion, CancellationToken token) => throw new InvalidOperationException();
    }
}

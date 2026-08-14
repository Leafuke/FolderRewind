using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Packaging;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class PluginPackageInstallerTests
{
    [TestMethod]
    public void NewInstallIsDisabledEvenWhenAStaleEnabledIntentExists()
    {
        Assert.IsFalse(PluginInstallIntentPolicy.ResolveAfterInstall(
            isUpdate: false,
            existingEnabledIntent: true));
        Assert.IsTrue(PluginInstallIntentPolicy.ResolveAfterInstall(
            isUpdate: true,
            existingEnabledIntent: true));
        Assert.IsFalse(PluginInstallIntentPolicy.ResolveAfterInstall(
            isUpdate: true,
            existingEnabledIntent: false));
    }

    [TestMethod]
    public void EnabledIntentTransitionIsIdempotentForAnAlreadyActivePlugin()
    {
        var noOp = PluginRuntimeIntentPolicy.Decide(
            requestedEnabled: true,
            currentEnabledIntent: true,
            currentRuntimeState: PluginRuntimeState.Active);

        Assert.IsFalse(noOp.PersistIntent);
        Assert.AreEqual(PluginRuntimeIntentAction.None, noOp.RuntimeAction);

        var retry = PluginRuntimeIntentPolicy.Decide(
            requestedEnabled: true,
            currentEnabledIntent: true,
            currentRuntimeState: PluginRuntimeState.Failed);

        Assert.IsFalse(retry.PersistIntent);
        Assert.AreEqual(PluginRuntimeIntentAction.Activate, retry.RuntimeAction);
    }

    [TestMethod]
    public void EnabledIntentTransitionSeparatesPersistenceFromRuntimeWork()
    {
        var alreadyDisabled = PluginRuntimeIntentPolicy.Decide(
            requestedEnabled: false,
            currentEnabledIntent: false,
            currentRuntimeState: PluginRuntimeState.Inactive);

        Assert.IsFalse(alreadyDisabled.PersistIntent);
        Assert.AreEqual(PluginRuntimeIntentAction.None, alreadyDisabled.RuntimeAction);

        var disableInactive = PluginRuntimeIntentPolicy.Decide(
            requestedEnabled: false,
            currentEnabledIntent: true,
            currentRuntimeState: PluginRuntimeState.Inactive);

        Assert.IsTrue(disableInactive.PersistIntent);
        Assert.AreEqual(PluginRuntimeIntentAction.None, disableInactive.RuntimeAction);

        var enableInactive = PluginRuntimeIntentPolicy.Decide(
            requestedEnabled: true,
            currentEnabledIntent: false,
            currentRuntimeState: PluginRuntimeState.Inactive);

        Assert.IsTrue(enableInactive.PersistIntent);
        Assert.AreEqual(PluginRuntimeIntentAction.Activate, enableInactive.RuntimeAction);
    }

    [TestMethod]
    public async Task BundledMineRewindPackageMatchesFrozenIdentityAndHash()
    {
        var root = FindRepositoryRoot();
        var packagePath = Path.Combine(root, "FolderRewind", "Assets", "Plugins", "MineRewind-1.9.0.frplugin");
        var package = await PluginPackageValidator.ValidateAsync(
            packagePath,
            "6fdcf3022058a3b8ddc5e1eeab95b7d9e500e49d3dc9db81ba588467c38b4d66");

        Assert.AreEqual("com.folderrewind.minerewind", package.Manifest.Contract.PluginId.Value);
        Assert.AreEqual("1.9.0", package.Manifest.Contract.Version);
        Assert.AreEqual(3, package.Manifest.Contract.RequiredApi.Major);
        Assert.AreEqual(0, package.Manifest.Contract.RequiredApi.Minor);
        Assert.IsFalse(package.Entries.Any(value =>
            value.CanonicalPath.EndsWith("FolderRewind.Plugin.Abstractions.dll", StringComparison.OrdinalIgnoreCase)));

        using var archive = ZipFile.OpenRead(packagePath);
        using var settingsStream = archive.GetEntry("settings.schema.json")!.Open();
        using var settingsDocument = JsonDocument.Parse(settingsStream);
        var firstSetting = settingsDocument.RootElement.GetProperty("settings")[0];
        Assert.AreEqual(
            "自动发现 Minecraft 存档",
            firstSetting.GetProperty("localizedDisplayName").GetProperty("zh-CN").GetString());
    }

    [TestMethod]
    public async Task LegacyFlatPayloadMovesToRecoverableNonExecutableQuarantine()
    {
        using var root = PackageTemporaryDirectory.Create("M5-LegacyQuarantine");
        var pluginRoot = Path.Combine(root.Path, "plugins", "com.example.package");
        Directory.CreateDirectory(Path.Combine(pluginRoot, "versions", "1.0.0"));
        await File.WriteAllTextAsync(Path.Combine(pluginRoot, "install-state.v1.json"), "v3");
        await File.WriteAllTextAsync(Path.Combine(pluginRoot, "manifest.json"), "legacy");
        await File.WriteAllTextAsync(Path.Combine(pluginRoot, "Plugin.dll"), "legacy-code");

        var result = await LegacyPluginQuarantineService.QuarantineFlatPayloadAsync(
            new PluginId("com.example.package"),
            pluginRoot,
            Path.Combine(root.Path, "legacy-quarantine"));

        Assert.IsTrue(result.Quarantined);
        Assert.IsTrue(Directory.Exists(Path.Combine(pluginRoot, "versions", "1.0.0")));
        Assert.IsTrue(File.Exists(Path.Combine(pluginRoot, "install-state.v1.json")));
        Assert.IsFalse(File.Exists(Path.Combine(pluginRoot, "Plugin.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(result.QuarantinePath, "Plugin.dll")));
        var receipt = await File.ReadAllTextAsync(Path.Combine(result.QuarantinePath, "quarantine-receipt.json"));
        StringAssert.Contains(receipt, "\"executable\": false");
    }

    [TestMethod]
    public async Task CanceledLegacyQuarantineLeavesFlatPayloadUntouched()
    {
        using var root = PackageTemporaryDirectory.Create("M5-LegacyQuarantineCanceled");
        var pluginRoot = Path.Combine(root.Path, "plugins", "com.example.package");
        Directory.CreateDirectory(pluginRoot);
        await File.WriteAllTextAsync(Path.Combine(pluginRoot, "manifest.json"), "legacy");
        await File.WriteAllTextAsync(Path.Combine(pluginRoot, "Plugin.dll"), "legacy-code");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            LegacyPluginQuarantineService.QuarantineFlatPayloadAsync(
                new PluginId("com.example.package"),
                pluginRoot,
                Path.Combine(root.Path, "legacy-quarantine"),
                cancellation.Token).AsTask());

        Assert.IsTrue(File.Exists(Path.Combine(pluginRoot, "manifest.json")));
        Assert.IsTrue(File.Exists(Path.Combine(pluginRoot, "Plugin.dll")));
    }

    [TestMethod]
    public async Task MigrationStateIsAtomicAndReadBackVerified()
    {
        using var root = PackageTemporaryDirectory.Create("M5-MigrationState");
        var pluginId = new PluginId("com.example.package");
        var store = new PluginMigrationStateStore(Path.Combine(root.Path, ".migration"));
        var started = DateTimeOffset.UtcNow;
        var inProgress = new PluginMigrationState(
            1,
            pluginId,
            PluginMigrationStatus.InProgress,
            "InstallingPackage",
            started,
            started,
            PreservedEnabledIntent: true);
        await store.WriteAsync(inProgress);

        var completed = inProgress with
        {
            Status = PluginMigrationStatus.Completed,
            Phase = "Completed",
            UpdatedAtUtc = started.AddSeconds(1),
            InstalledVersion = "1.9.0",
            QuarantinePath = "legacy-quarantine/com.example.package"
        };
        await store.WriteAsync(completed);

        Assert.AreEqual(completed, await store.ReadAsync(pluginId));
        Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(root.Path, ".migration")).Count());
    }

    [TestMethod]
    public async Task ValidatorRejectsTraversalCollisionBombAndBundledAbstractionsBeforeExtraction()
    {
        using var root = PackageTemporaryDirectory.Create("M5-PackageReject");
        var traversal = CreatePackage(root.Path, "traversal", "1.0.0", archive => Add(archive, "../escape.dll", "x"));
        var collision = CreatePackage(root.Path, "collision", "1.0.0", archive =>
        {
            Add(archive, "Data.bin", "a");
            Add(archive, "data.BIN", "b");
        });
        var bundled = CreatePackage(root.Path, "bundled", "1.0.0", archive =>
            Add(archive, "lib/FolderRewind.Plugin.Abstractions.dll", "contract"));
        var bomb = CreatePackage(root.Path, "bomb", "1.0.0", archive =>
            Add(archive, "zeros.bin", new string('0', 100_000)));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PluginPackageValidator.ValidateAsync(traversal).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PluginPackageValidator.ValidateAsync(collision).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PluginPackageValidator.ValidateAsync(bundled).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PluginPackageValidator.ValidateAsync(
            bomb,
            limits: new PluginPackageLimits(100, 1_000_000, 1_000_000, 2, 100_000)).AsTask());
        Assert.IsFalse(File.Exists(Path.Combine(root.Path, "escape.dll")));
    }

    [TestMethod]
    public async Task InstallAndUpdateRetainCurrentAndPreviousKnownGood()
    {
        using var root = PackageTemporaryDirectory.Create("M5-VersionedInstall");
        var package1 = CreatePackage(root.Path, "v1", "1.0.0");
        var package2 = CreatePackage(root.Path, "v2", "1.1.0");
        var installer = new PluginPackageInstaller(Path.Combine(root.Path, "plugins"));

        var first = await installer.InstallAsync(package1, PluginInstallProvenance.Manual);
        var second = await installer.InstallAsync(package2, PluginInstallProvenance.OfficialCatalog);

        Assert.AreEqual("1.1.0", second.State.CurrentVersion);
        Assert.AreEqual("1.0.0", second.State.PreviousKnownGoodVersion);
        Assert.IsTrue(Directory.Exists(first.InstalledPath));
        Assert.IsTrue(Directory.Exists(second.InstalledPath));
        var state = await installer.ReadStateAsync(new PluginId("com.example.package"));
        Assert.AreEqual("1.1.0", state!.CurrentVersion);
        Assert.HasCount(2, state.Versions);
    }

    [TestMethod]
    public async Task OwnedArtifactCompatibilityIsCheckedBeforeCandidateIsWritten()
    {
        using var root = PackageTemporaryDirectory.Create("M5-OwnedArtifacts");
        var package = CreatePackage(root.Path, "candidate", "2.0.0");
        var plugins = Path.Combine(root.Path, "plugins");
        var installer = new PluginPackageInstaller(plugins);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => installer.InstallAsync(
            package,
            PluginInstallProvenance.Manual,
            validateOwnedArtifactsAsync: (_, _) => throw new InvalidOperationException("reachable format unsupported")).AsTask());

        Assert.IsFalse(Directory.Exists(Path.Combine(plugins, "com.example.package")));
    }

    [TestMethod]
    public async Task RuntimeSwitchFailureCanRollbackPointerToPreviousKnownGood()
    {
        using var root = PackageTemporaryDirectory.Create("M5-PointerRollback");
        var package1 = CreatePackage(root.Path, "v1", "1.0.0");
        var package2 = CreatePackage(root.Path, "v2", "2.0.0");
        var installer = new PluginPackageInstaller(Path.Combine(root.Path, "plugins"));
        await installer.InstallAsync(package1, PluginInstallProvenance.Manual);
        await installer.InstallAsync(package2, PluginInstallProvenance.Manual);

        var rolledBack = await installer.RollbackToPreviousKnownGoodAsync(new PluginId("com.example.package"));

        Assert.AreEqual("1.0.0", rolledBack.CurrentVersion);
        Assert.AreEqual("2.0.0", rolledBack.PreviousKnownGoodVersion);
    }

    [TestMethod]
    public async Task CodeOnlyRemovalDoesNotTouchExternalPluginData()
    {
        using var root = PackageTemporaryDirectory.Create("M5-CodeOnlyUninstall");
        var package = CreatePackage(root.Path, "v1", "1.0.0");
        var plugins = Path.Combine(root.Path, "plugins");
        var data = Path.Combine(root.Path, "plugin-data", "com.example.package");
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(data, "state.bin"), "preserved");
        var installer = new PluginPackageInstaller(plugins);
        await installer.InstallAsync(package, PluginInstallProvenance.Manual);

        await installer.RemoveInstalledCodeAsync(new PluginId("com.example.package"));

        Assert.IsFalse(Directory.Exists(Path.Combine(plugins, "com.example.package")));
        Assert.IsTrue(File.Exists(Path.Combine(data, "state.bin")));
    }

    [TestMethod]
    public async Task FailedCandidateActivationRestoresKnownGoodAndRemovesCandidate()
    {
        using var root = PackageTemporaryDirectory.Create("M5-ActivationRollback");
        var package1 = CreatePackage(root.Path, "v1", "1.0.0");
        var package2 = CreatePackage(root.Path, "v2", "2.0.0");
        var installer = new PluginPackageInstaller(Path.Combine(root.Path, "plugins"));
        await installer.InstallAsync(package1, PluginInstallProvenance.Manual);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => installer.InstallAsync(
            package2,
            PluginInstallProvenance.Manual,
            validateActivationAsync: (_, _, _) => throw new InvalidOperationException("candidate failed")).AsTask());

        var state = await installer.ReadStateAsync(new PluginId("com.example.package"));
        Assert.AreEqual("1.0.0", state!.CurrentVersion);
        Assert.IsFalse(Directory.Exists(Path.Combine(root.Path, "plugins", "com.example.package", "versions", "2.0.0")));
    }

    [TestMethod]
    public async Task RecoveryDeletesUncommittedCandidateAndMarksJournalRolledBack()
    {
        using var root = PackageTemporaryDirectory.Create("M5-InstallRecovery");
        var plugins = Path.Combine(root.Path, "plugins");
        var candidate = Path.Combine(plugins, "com.example.package", "versions", "9.0.0");
        Directory.CreateDirectory(candidate);
        var transaction = Path.Combine(plugins, ".transactions", "crash");
        Directory.CreateDirectory(transaction);
        var journal = new PluginInstallTransactionJournal(
            "crash",
            new PluginId("com.example.package"),
            "9.0.0",
            string.Empty,
            PluginInstallTransactionPhase.CandidateSelected,
            candidate,
            DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(
            Path.Combine(transaction, "journal.json"),
            JsonSerializer.Serialize(journal));

        await new PluginPackageInstaller(plugins).RecoverAsync();

        Assert.IsFalse(Directory.Exists(candidate));
        var json = await File.ReadAllTextAsync(Path.Combine(transaction, "journal.json"));
        Assert.IsTrue(json.Contains("RolledBack", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TrustedHashMismatchDoesNotCreateInstallState()
    {
        using var root = PackageTemporaryDirectory.Create("M5-Hash");
        var package = CreatePackage(root.Path, "hash", "1.0.0");
        var plugins = Path.Combine(root.Path, "plugins");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new PluginPackageInstaller(plugins)
            .InstallAsync(package, PluginInstallProvenance.OfficialCatalog, new string('0', 64)).AsTask());

        Assert.IsFalse(Directory.Exists(Path.Combine(plugins, "com.example.package")));
    }

    private static string CreatePackage(
        string directory,
        string name,
        string version,
        Action<ZipArchive>? customize = null)
    {
        var path = Path.Combine(directory, name + ".frplugin");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(archive, "manifest.json", $$"""
            {
              "manifestVersion": 3,
              "pluginId": "com.example.package",
              "version": "{{version}}",
              "name": "Package",
              "description": "Fixture",
              "pluginApi": { "major": 3, "minor": 0 },
              "entryAssembly": "Plugin.dll",
              "entryType": "Fixture.Plugin",
              "settingsSchema": "settings.schema.json",
              "architectures": ["any"],
              "configKinds": [],
              "requestedHostServices": [],
              "capabilities": [],
              "artifactFormats": [],
              "artifactTransformers": [],
              "restoreStrategies": [],
              "hasBackupCompletionObserver": false
            }
            """);
        Add(archive, "settings.schema.json", "{}");
        Add(archive, "Plugin.dll", "fixture");
        customize?.Invoke(archive);
        return path;
    }

    private static void Add(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.SmallestSize);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "FolderRewind.Plugin.Runtime"))
                && Directory.Exists(Path.Combine(directory.FullName, "FolderRewind")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("FolderRewind repository root was not found.");
    }

    private sealed class PackageTemporaryDirectory : IDisposable
    {
        private PackageTemporaryDirectory(string path) => Path = path;
        public string Path { get; }
        public static PackageTemporaryDirectory Create(string name)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new PackageTemporaryDirectory(path);
        }
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}

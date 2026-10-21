using System.IO.Compression;
using System.Text.Json;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Loading;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MineRewindPerformanceIntegrationTests
{
    [TestMethod]
    public async Task ShippedPolicyUsesFileDeltaAndPreservesLockedExcludedFile()
    {
        await using var f = new HistoryChainRewriteFixture(); await f.InitializeAsync();
        var target = Path.Combine(f.Root, "world"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "level.dat"), "synthetic discovery marker, not a real Minecraft world");
        var exclusions = await ReadPackagedPolicyAsync(target);
        CollectionAssert.Contains(exclusions, "session.lock"); CollectionAssert.Contains(exclusions, "voxy/**");
        var boundary = new EffectiveSourceBoundarySnapshot(EffectiveBoundaryScopeMode.All, [], EffectiveBoundaryFilterMode.Blacklist, exclusions, false);
        var node = await f.AddAsync(null, dir => { File.WriteAllText(Path.Combine(dir, "level.dat"), "restored synthetic data"); File.WriteAllText(Path.Combine(dir, "current.txt"), "restored"); }, boundary: boundary);
        var lockPath = Path.Combine(target, "session.lock"); File.WriteAllText(lockPath, "excluded original");
        var identity = WindowsRestoreFileSystem.Identity(lockPath);
        File.WriteAllText(Path.Combine(target, "stale.txt"), "remove");
        using var gameLock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        using var verification = new ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced);
        var restore = new HistoryRestoreService(f.History, f.Engine,
            async token => new RepresentationEnvironment((await f.History.LocalReplicaCatalogStore.LoadAsync(token)).Value!.Entries, [], []),
            new FileSystemHistoryRestoreMutationBackend()) { UseTargetVolumeWorkspaces = true, VerificationContext = verification };
        var result = await restore.RestoreVersionAsync(node.Version.VersionId, new(f.Source, target, boundary),
            (await f.History.WorkspaceStore.LoadAsync()).Value!, HistoryRestoreApplyMode.Clean);
        Assert.IsTrue(result.Succeeded, result.Diagnostic);
        Assert.AreEqual(identity, WindowsRestoreFileSystem.Identity(lockPath));
        Assert.IsFalse(File.Exists(Path.Combine(target, "stale.txt")));
        Assert.AreEqual("restored", File.ReadAllText(Path.Combine(target, "current.txt")));
        var stats = HistoryOperationDiagnostics.GetRecent().Last();
        Assert.AreEqual("FileDelta", stats.Observations["mutation-strategy"]);
        Assert.AreEqual(0L, stats.Counters.GetValueOrDefault("copy-tree-bytes"));
    }
    internal static async Task<string[]> ReadPackagedPolicyAsync(string world)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AGENTS.md"))) repo = repo.Parent;
        Assert.IsNotNull(repo);
        var package = Directory.GetFiles(Path.Combine(repo.FullName, "FolderRewind", "Assets", "Plugins"), "MineRewind-*.frplugin").Single();
        Assert.IsTrue(File.Exists(package));
        var root = Path.Combine(Path.GetTempPath(), "fr-minerewind-package-" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(package, root);
        try
        {
            return await InvokePolicyAsync(root, world);
        }
        finally
        {
            // collectible ALC 的卸载不等于立即释放 Windows DLL 映射；异步栈退出后
            // 再回收并有限重试，避免把暂时的映射占用误报为策略/还原失败。
            for (int attempt = 0; ; attempt++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                try { Directory.Delete(root, true); break; }
                catch (Exception error) when (attempt < 19 && error is IOException or UnauthorizedAccessException)
                { await Task.Delay(50); }
            }
        }
    }
    private static async Task<string[]> InvokePolicyAsync(string root, string world)
    {
        var id = new PluginId("com.folderrewind.minerewind");
        using var plugin = PluginAssemblyLoader.Load(new(id, root, "MineRewind.dll", "MineRewind.MinecraftSavesPlugin", new(3, 9)));
        var folder = new FolderSnapshot(Guid.NewGuid(), world, "synthetic world", new Dictionary<StateOwnerId, ProviderStateSnapshot>());
        var config = new ConfigSnapshot("performance-integration", new("test"), new(new(id.Value), "minecraft-saves"), "test", [folder], new Dictionary<StateOwnerId, ProviderStateSnapshot>());
        var context = new ActivationContext(id, config);
        await plugin.Instance.ActivateAsync(context, default);
        var capability = context.Capabilities.OfType<IFilePolicyCapability>().Single();
        var result = await capability.ResolveAsync(new(config, folder), new(id, new UnusedHostServices(), default, default));
        await plugin.Instance.DeactivateAsync(default);
        return result.RequiredExclusions.ToArray();
    }
    private sealed class ActivationContext(PluginId id, ConfigSnapshot config) : IPluginActivationContext
    {
        public PluginId PluginId => id;
        public PluginSettingsSnapshot Settings => new(id, new Dictionary<string, JsonElement>());
        public IReadOnlyList<ConfigSnapshot> Configs => [config];
        internal List<IPluginCapability> Capabilities { get; } = [];
        public void RegisterCapability<T>(T capability) where T : class, IPluginCapability => Capabilities.Add(capability);
    }
    private sealed class UnusedHostServices : IPluginHostServices
    {
        public IReadOnlyConfigQueryService Configs => throw new InvalidOperationException();
        public IBackupRequestService Backups => throw new InvalidOperationException();
        public IRestoreRequestService Restores => throw new InvalidOperationException();
        public IHistoryQueryService History => throw new InvalidOperationException();
        public IPluginNotificationService Notifications => throw new InvalidOperationException();
        public IKnotLinkHostService KnotLink => throw new InvalidOperationException();
        public IPluginDataStore DataStore => throw new InvalidOperationException();
        public IPluginTemporaryStorage TemporaryStorage => throw new InvalidOperationException();
        public IPluginLogger Logger => throw new InvalidOperationException();
    }
}

using System.Reflection;
using System.Runtime.Loader;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Loading;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class PluginAssemblyLoaderTests
{
    [TestMethod]
    public void TwoPluginsUseSharedContractAndIsolatedPrivateDependencyVersions()
    {
        using var one = PluginAssemblyLoader.Load(Request(
            "com.folderrewind.fixture-one",
            "PluginOne",
            "Fixture.PluginOne.dll",
            "Fixture.PluginOne.EntryPlugin"));
        using var two = PluginAssemblyLoader.Load(Request(
            "com.folderrewind.fixture-two",
            "PluginTwo",
            "Fixture.PluginTwo.dll",
            "Fixture.PluginTwo.EntryPlugin"));

        Assert.IsInstanceOfType<IFolderRewindPlugin>(one.Instance);
        Assert.IsInstanceOfType<IFolderRewindPlugin>(two.Instance);
        Assert.AreSame(typeof(IFolderRewindPlugin).Assembly, one.Instance.GetType().GetInterface(typeof(IFolderRewindPlugin).FullName!)!.Assembly);
        Assert.AreEqual("1.0.0", DependencyVersion(one.Instance));
        Assert.AreEqual("2.0.0", DependencyVersion(two.Instance));

        var dependencyOne = DependencyAssembly(one.EntryAssembly);
        var dependencyTwo = DependencyAssembly(two.EntryAssembly);
        Assert.AreNotSame(dependencyOne, dependencyTwo);
        Assert.AreEqual(1, dependencyOne.GetName().Version!.Major);
        Assert.AreEqual(2, dependencyTwo.GetName().Version!.Major);
        Assert.AreNotSame(AssemblyLoadContext.GetLoadContext(dependencyOne), AssemblyLoadContext.GetLoadContext(dependencyTwo));
    }

    [TestMethod]
    public void BundledAbstractionsDllIsRejectedBeforeEntryAssemblyLoads()
    {
        var source = FixtureOutput("PluginOne");
        using var temp = new TemporaryDirectory();
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(temp.Path, Path.GetFileName(file)));
        }
        File.Copy(
            typeof(IFolderRewindPlugin).Assembly.Location,
            Path.Combine(temp.Path, "FolderRewind.Plugin.Abstractions.dll"),
            overwrite: true);

        var request = new PluginLoadRequest(
            new PluginId("com.folderrewind.fixture-one"),
            temp.Path,
            "Fixture.PluginOne.dll",
            "Fixture.PluginOne.EntryPlugin",
            new PluginApiVersion(3, 0));

        var error = Assert.ThrowsExactly<InvalidDataException>(() => PluginAssemblyLoader.Load(request));
        StringAssert.Contains(error.Message, "must not contain");
    }

    [TestMethod]
    public void ManifestApiMinorAndMajorCompatibilityIsEnforcedBeforeLoad()
    {
        var supportedMinor = Request(
            "com.folderrewind.fixture-one",
            "PluginOne",
            "Fixture.PluginOne.dll",
            "Fixture.PluginOne.EntryPlugin") with
        {
            RequiredApiVersion = new PluginApiVersion(3, 1)
        };
        var newerMinor = supportedMinor with { RequiredApiVersion = new PluginApiVersion(3, 2) };
        var wrongMajor = supportedMinor with { RequiredApiVersion = new PluginApiVersion(2, 0) };

        using var loaded = PluginAssemblyLoader.Load(supportedMinor);
        Assert.ThrowsExactly<InvalidOperationException>(() => PluginAssemblyLoader.Load(newerMinor));
        Assert.ThrowsExactly<InvalidOperationException>(() => PluginAssemblyLoader.Load(wrongMajor));
    }

    [TestMethod]
    public void EntryAssemblyCannotEscapePluginRoot()
    {
        var request = Request(
            "com.folderrewind.fixture-one",
            "PluginOne",
            "..\\PluginTwo\\Fixture.PluginTwo.dll",
            "Fixture.PluginTwo.EntryPlugin");

        Assert.ThrowsExactly<InvalidDataException>(() => PluginAssemblyLoader.Load(request));
    }

    [TestMethod]
    public void DisposingLoadedPluginReleasesCollectibleLoadContext()
    {
        var weakReference = LoadAndDispose();

        for (var attempt = 0; attempt < 10 && weakReference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.IsFalse(weakReference.IsAlive);
    }

    [TestMethod]
    public async Task UnloadAsyncReleasesPluginFilesBeforeDirectoryRemoval()
    {
        using var copy = new TemporaryDirectory();
        CopyDirectory(FixtureOutput("PluginOne"), copy.Path);
        var loaded = PluginAssemblyLoader.Load(new PluginLoadRequest(
            new PluginId("com.folderrewind.fixture-one"),
            copy.Path,
            "Fixture.PluginOne.dll",
            "Fixture.PluginOne.EntryPlugin",
            new PluginApiVersion(3, 0)));

        Assert.IsTrue(await loaded.UnloadAsync());

        Directory.Delete(copy.Path, recursive: true);
        Assert.IsFalse(Directory.Exists(copy.Path));
    }

    private static WeakReference LoadAndDispose()
    {
        var loaded = PluginAssemblyLoader.Load(Request(
            "com.folderrewind.fixture-one",
            "PluginOne",
            "Fixture.PluginOne.dll",
            "Fixture.PluginOne.EntryPlugin"));
        var weakReference = loaded.LoadContextWeakReference;
        loaded.Dispose();
        return weakReference;
    }

    private static PluginLoadRequest Request(
        string pluginId,
        string fixture,
        string assembly,
        string entryType)
        => new(new PluginId(pluginId), FixtureOutput(fixture), assembly, entryType, new PluginApiVersion(3, 0));

    private static string FixtureOutput(string fixture)
        => Path.Combine(
            RepositoryRoot(),
            "FolderRewind.Plugin.Runtime.Tests",
            "Fixtures",
            fixture,
            "bin",
            BuildConfiguration(),
            "net10.0");

    private static string BuildConfiguration()
        => new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
           ?? throw new DirectoryNotFoundException("Test build configuration was not found.");

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "FolderRewind.slnx")))
        {
            current = current.Parent;
        }
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string DependencyVersion(IFolderRewindPlugin plugin)
        => (string)(plugin.GetType().GetProperty("DependencyVersion")?.GetValue(plugin)
                    ?? throw new MissingMemberException("DependencyVersion"));

    private static Assembly DependencyAssembly(Assembly entryAssembly)
    {
        var context = AssemblyLoadContext.GetLoadContext(entryAssembly)!;
        return context.Assemblies.Single(assembly => assembly.GetName().Name == "Fixture.PrivateDependency");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FolderRewind.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}

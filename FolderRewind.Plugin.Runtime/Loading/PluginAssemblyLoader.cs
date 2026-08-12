using System.Reflection;
using System.Runtime.Loader;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Loading;

public sealed record PluginLoadRequest(
    PluginId PluginId,
    string RootDirectory,
    string EntryAssembly,
    string EntryType,
    PluginApiVersion RequiredApiVersion);

public sealed class LoadedPluginAssembly : IDisposable
{
    private PluginLoadContext? _loadContext;
    private IFolderRewindPlugin? _instance;
    private Assembly? _entryAssembly;

    internal LoadedPluginAssembly(
        PluginLoadRequest request,
        IFolderRewindPlugin instance,
        Assembly entryAssembly,
        PluginLoadContext loadContext)
    {
        Request = request;
        _instance = instance;
        _entryAssembly = entryAssembly;
        _loadContext = loadContext;
        LoadContextWeakReference = new WeakReference(loadContext, trackResurrection: false);
    }

    public PluginLoadRequest Request { get; }
    public IFolderRewindPlugin Instance
        => _instance ?? throw new ObjectDisposedException(nameof(LoadedPluginAssembly));
    public Assembly EntryAssembly
        => _entryAssembly ?? throw new ObjectDisposedException(nameof(LoadedPluginAssembly));
    public WeakReference LoadContextWeakReference { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _instance, null);
        Interlocked.Exchange(ref _entryAssembly, null);
        Interlocked.Exchange(ref _loadContext, null)?.Unload();
    }
}

public static class PluginAssemblyLoader
{
    private const string AbstractionsAssemblyName = "FolderRewind.Plugin.Abstractions";
    private const string AbstractionsFileName = AbstractionsAssemblyName + ".dll";

    public static LoadedPluginAssembly Load(PluginLoadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.RequiredApiVersion.IsSatisfiedBy(PluginApiVersion.HostVersion))
        {
            throw new InvalidOperationException(
                $"Plugin requires API {request.RequiredApiVersion}, but Host provides {PluginApiVersion.HostVersion}.");
        }

        var root = Path.GetFullPath(request.RootDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Plugin root does not exist: {root}");
        }

        RejectBundledAbstractions(root);
        var entryPath = ResolveContainedPath(root, request.EntryAssembly);
        if (!File.Exists(entryPath))
        {
            throw new FileNotFoundException("Plugin entry assembly was not found.", entryPath);
        }

        var context = new PluginLoadContext(entryPath);
        try
        {
            var assembly = context.LoadFromAssemblyPath(entryPath);
            ValidateContractReference(assembly);
            var entryType = assembly.GetType(request.EntryType, throwOnError: true, ignoreCase: false)
                ?? throw new TypeLoadException($"Plugin entry type '{request.EntryType}' was not found.");
            if (!typeof(IFolderRewindPlugin).IsAssignableFrom(entryType)
                || entryType.IsAbstract
                || entryType.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new InvalidOperationException(
                    $"Plugin entry type '{request.EntryType}' must be a concrete IFolderRewindPlugin with a public parameterless constructor.");
            }

            var instance = (IFolderRewindPlugin?)Activator.CreateInstance(entryType)
                ?? throw new InvalidOperationException("Plugin entry constructor returned null.");
            return new LoadedPluginAssembly(request, instance, assembly, context);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    private static void RejectBundledAbstractions(string root)
    {
        var bundled = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), AbstractionsFileName, StringComparison.OrdinalIgnoreCase));
        if (bundled is not null)
        {
            throw new InvalidDataException(
                $"Plugin payload must not contain the shared contract assembly '{AbstractionsFileName}'.");
        }
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Plugin entry assembly must be a non-empty relative path.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("Plugin entry assembly escapes the plugin root.");
        }

        return fullPath;
    }

    private static void ValidateContractReference(Assembly assembly)
    {
        var reference = assembly.GetReferencedAssemblies()
            .SingleOrDefault(name => string.Equals(name.Name, AbstractionsAssemblyName, StringComparison.Ordinal));
        if (reference is null)
        {
            throw new InvalidOperationException("Plugin entry assembly does not reference FolderRewind.Plugin.Abstractions.");
        }
        if (reference.Version?.Major != PluginApiVersion.HostVersion.Major)
        {
            throw new InvalidOperationException(
                $"Plugin contract assembly major '{reference.Version?.Major}' is incompatible with API {PluginApiVersion.HostVersion}.");
        }
    }
}

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string entryAssemblyPath)
        : base($"FolderRewind.Plugin:{Path.GetFileNameWithoutExtension(entryAssemblyPath)}:{Guid.NewGuid():N}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(
                assemblyName.Name,
                typeof(IFolderRewindPlugin).Assembly.GetName().Name,
                StringComparison.Ordinal))
        {
            return typeof(IFolderRewindPlugin).Assembly;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}

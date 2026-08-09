using FolderRewind.Models;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using ValveKeyValue;

namespace FolderRewind.Services.Discovery;

public interface ILauncherInstallationScanner
{
    GameStore Store { get; }
    IReadOnlyList<DetectedGameInstallation> Scan(IEnumerable<string> roots);
}

public interface ILauncherInstallationDiscoveryService
{
    IReadOnlyList<DetectedGameInstallation> Scan(
        IReadOnlyDictionary<GameStore, IReadOnlyList<string>> configuredRoots,
        IReadOnlyList<string>? disabledAutoRoots = null);
}

public sealed class LauncherInstallationDiscoveryService : ILauncherInstallationDiscoveryService
{
    private readonly IReadOnlyList<ILauncherInstallationScanner> _scanners;

    public LauncherInstallationDiscoveryService(IEnumerable<ILauncherInstallationScanner>? scanners = null)
    {
        _scanners = (scanners ?? new ILauncherInstallationScanner[]
        {
            new SteamInstallationScanner(),
            new GogInstallationScanner(),
            new EpicInstallationScanner()
        }).ToList();
    }

    public IReadOnlyList<DetectedGameInstallation> Scan(
        IReadOnlyDictionary<GameStore, IReadOnlyList<string>> configuredRoots,
        IReadOnlyList<string>? disabledAutoRoots = null)
    {
        var defaults = GameLibraryRootDetector.DetectDefaults();
        var disabled = new HashSet<string>(
            (disabledAutoRoots ?? Array.Empty<string>()).Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);
        var result = new List<DetectedGameInstallation>();
        foreach (var scanner in _scanners)
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (defaults.TryGetValue(scanner.Store, out var defaultRoots))
            {
                roots.UnionWith(defaultRoots.Where(root =>
                    Directory.Exists(root) && !disabled.Contains(NormalizePath(root))));
            }
            if (configuredRoots.TryGetValue(scanner.Store, out var customRoots))
            {
                roots.UnionWith(customRoots.Where(Directory.Exists));
            }

            result.AddRange(scanner.Scan(roots));
        }

        return result
            .GroupBy(item => $"{item.Store}|{item.StoreGameId}|{NormalizePath(item.BasePath)}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }
}

public sealed class SteamInstallationScanner : ILauncherInstallationScanner
{
    public GameStore Store => GameStore.Steam;

    public IReadOnlyList<DetectedGameInstallation> Scan(IEnumerable<string> roots)
    {
        var steamAppsRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
        {
            var steamApps = ResolveSteamAppsRoot(root);
            if (Directory.Exists(steamApps))
            {
                steamAppsRoots.Add(steamApps);
                AddLibraryFolders(steamApps, steamAppsRoots);
            }
        }

        var userIds = steamAppsRoots
            .Select(path => Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "userdata"))
            .Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateDirectories(path))
            .Select(Path.GetFileName)
            .Where(value => !string.IsNullOrWhiteSpace(value) && value.All(char.IsDigit))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<DetectedGameInstallation>();
        foreach (var steamApps in steamAppsRoots)
        {
            foreach (var manifestPath in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    using var stream = File.OpenRead(manifestPath);
                    var document = KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(stream);
                    var root = document.Root;
                    if (!TryScalar(root, "appid", out var appId)
                        || !TryScalar(root, "installdir", out var installDir))
                    {
                        continue;
                    }

                    TryScalar(root, "name", out var name);
                    var libraryRoot = Path.GetDirectoryName(steamApps) ?? steamApps;
                    result.Add(new DetectedGameInstallation
                    {
                        Store = GameStore.Steam,
                        StoreGameId = appId,
                        DisplayName = name,
                        RootPath = libraryRoot,
                        BasePath = Path.Combine(steamApps, "common", installDir),
                        InstalledGameName = installDir,
                        StoreUserIds = userIds
                    });
                }
                catch (Exception) when (File.Exists(manifestPath))
                {
                    // A single malformed or concurrently updated ACF must not abort the store scan.
                }
            }
        }

        return result;
    }

    private static string ResolveSteamAppsRoot(string root)
    {
        var fullPath = Path.GetFullPath(root);
        if (string.Equals(Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar)), "steamapps", StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        return Path.Combine(fullPath, "steamapps");
    }

    private static void AddLibraryFolders(string steamAppsRoot, ISet<string> roots)
    {
        var path = Path.Combine(steamAppsRoot, "libraryfolders.vdf");
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var document = KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(stream);
            foreach (var pair in document.Root)
            {
                if (!pair.Key.All(char.IsDigit)
                    || !TryScalar(pair.Value, "path", out var libraryPath))
                {
                    continue;
                }

                var candidate = Path.Combine(libraryPath, "steamapps");
                if (Directory.Exists(candidate))
                {
                    roots.Add(candidate);
                }
            }
        }
        catch (Exception) when (File.Exists(path))
        {
        }
    }

    private static bool TryScalar(KVObject parent, string key, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetValue(key, out var item) || item.IsCollection || item.IsArray || item.IsNull)
        {
            return false;
        }

        value = item.ToString().Trim();
        return !string.IsNullOrWhiteSpace(value);
    }
}

public sealed class EpicInstallationScanner : ILauncherInstallationScanner
{
    public GameStore Store => GameStore.Epic;

    public IReadOnlyList<DetectedGameInstallation> Scan(IEnumerable<string> roots)
    {
        var result = new List<DetectedGameInstallation>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            var manifestsRoot = ResolveManifestsRoot(root);
            if (!Directory.Exists(manifestsRoot))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(manifestsRoot, "*.item", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    using var document = JsonDocument.Parse(stream);
                    var json = document.RootElement;
                    var installPath = GetString(json, "InstallLocation");
                    if (string.IsNullOrWhiteSpace(installPath))
                    {
                        continue;
                    }

                    var appId = GetString(json, "CatalogItemId");
                    if (string.IsNullOrWhiteSpace(appId))
                    {
                        appId = GetString(json, "AppName");
                    }
                    result.Add(new DetectedGameInstallation
                    {
                        Store = GameStore.Epic,
                        StoreGameId = appId,
                        DisplayName = GetString(json, "DisplayName"),
                        RootPath = Path.GetDirectoryName(installPath) ?? string.Empty,
                        BasePath = installPath,
                        InstalledGameName = Path.GetFileName(installPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    });
                }
                catch (Exception) when (File.Exists(path))
                {
                }
            }
        }

        return result;
    }

    private static string ResolveManifestsRoot(string root)
    {
        if (string.Equals(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)), "Manifests", StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        var nested = Path.Combine(root, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        return Directory.Exists(nested) ? nested : root;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }
}

public sealed class GogInstallationScanner : ILauncherInstallationScanner
{
    public GameStore Store => GameStore.Gog;

    public IReadOnlyList<DetectedGameInstallation> Scan(IEnumerable<string> roots)
    {
        var result = ScanRegistry();
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                if (result.Any(item => PathsEqual(item.BasePath, directory)))
                {
                    continue;
                }

                result.Add(new DetectedGameInstallation
                {
                    Store = GameStore.Gog,
                    DisplayName = Path.GetFileName(directory),
                    RootPath = root,
                    BasePath = directory,
                    InstalledGameName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                });
            }
        }

        return result;
    }

    private static List<DetectedGameInstallation> ScanRegistry()
    {
        var result = new List<DetectedGameInstallation>();
        if (!OperatingSystem.IsWindows())
        {
            return result;
        }

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var gamesKey = baseKey.OpenSubKey("SOFTWARE\\GOG.com\\Games");
                if (gamesKey == null)
                {
                    continue;
                }

                foreach (var subKeyName in gamesKey.GetSubKeyNames())
                {
                    using var gameKey = gamesKey.OpenSubKey(subKeyName);
                    var path = gameKey?.GetValue("path") as string;
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    result.Add(new DetectedGameInstallation
                    {
                        Store = GameStore.Gog,
                        StoreGameId = gameKey?.GetValue("gameID")?.ToString() ?? subKeyName,
                        DisplayName = gameKey?.GetValue("gameName") as string ?? string.Empty,
                        RootPath = Path.GetDirectoryName(path) ?? string.Empty,
                        BasePath = path,
                        InstalledGameName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    });
                }
            }
            catch (Exception)
            {
            }
        }

        return result
            .GroupBy(item => $"{item.StoreGameId}|{item.BasePath}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public static class GameLibraryRootDetector
{
    public static IReadOnlyDictionary<GameStore, IReadOnlyList<string>> DetectDefaults()
    {
        var result = new Dictionary<GameStore, IReadOnlyList<string>>();
        var steam = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            AddWindowsSteamRoots(steam);
        }
        result[GameStore.Steam] = steam.ToList();

        var epic = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic",
            "EpicGamesLauncher",
            "Data",
            "Manifests");
        result[GameStore.Epic] = new[] { epic };

        result[GameStore.Gog] = Array.Empty<string>();
        return result;
    }

    [SupportedOSPlatform("windows")]
    private static void AddWindowsSteamRoots(ISet<string> output)
    {
        AddRegistryString(output, RegistryHive.CurrentUser, RegistryView.Default, "Software\\Valve\\Steam", "SteamPath");
        AddRegistryString(output, RegistryHive.LocalMachine, RegistryView.Registry64, "Software\\WOW6432Node\\Valve\\Steam", "InstallPath");
    }

    [SupportedOSPlatform("windows")]
    private static void AddRegistryString(
        ISet<string> output,
        RegistryHive hive,
        RegistryView view,
        string keyPath,
        string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(keyPath);
            if (key?.GetValue(valueName) is string value && Directory.Exists(value))
            {
                output.Add(value);
            }
        }
        catch (Exception)
        {
        }
    }
}

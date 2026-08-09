using FolderRewind.Models;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using ValveKeyValue;
using static FolderRewind.Services.Discovery.LauncherScanDiagnosticFactory;

namespace FolderRewind.Services.Discovery;

public interface ILauncherInstallationScanner
{
    GameStore Store { get; }
    LauncherInstallationScanResult Scan(IEnumerable<string> roots, CancellationToken cancellationToken);
}

public interface ILauncherInstallationDiscoveryService
{
    LauncherInstallationScanResult Scan(
        IReadOnlyDictionary<GameStore, IReadOnlyList<string>> configuredRoots,
        IReadOnlyList<string>? disabledAutoRoots,
        CancellationToken cancellationToken);
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

    public LauncherInstallationScanResult Scan(
        IReadOnlyDictionary<GameStore, IReadOnlyList<string>> configuredRoots,
        IReadOnlyList<string>? disabledAutoRoots,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var defaults = GameLibraryRootDetector.DetectDefaults();
        var disabled = new HashSet<string>(
            (disabledAutoRoots ?? Array.Empty<string>()).Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);
        var installations = new List<DetectedGameInstallation>();
        var diagnostics = new List<DiscoveryDiagnostic>();
        foreach (var scanner in _scanners)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

            var scan = scanner.Scan(roots, cancellationToken);
            installations.AddRange(scan.Installations);
            diagnostics.AddRange(scan.Diagnostics);
        }

        return new LauncherInstallationScanResult
        {
            Installations = installations
                .GroupBy(item => $"{item.Store}|{item.StoreGameId}|{NormalizePath(item.BasePath)}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList(),
            Diagnostics = diagnostics
        };
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

    public LauncherInstallationScanResult Scan(IEnumerable<string> roots, CancellationToken cancellationToken)
    {
        var diagnostics = new List<DiscoveryDiagnostic>();
        var steamAppsRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots ?? Array.Empty<string>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }
                var steamApps = ResolveSteamAppsRoot(root);
                if (!Directory.Exists(steamApps))
                {
                    continue;
                }
                steamAppsRoots.Add(steamApps);
                AddLibraryFolders(steamApps, steamAppsRoots, diagnostics, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add(ScanDiagnostic(Store, root, ex));
            }
        }

        var clientRoots = steamAppsRoots
            .Select(path => Path.GetDirectoryName(path) ?? string.Empty)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var userIds = DiscoverUserIds(clientRoots, diagnostics, cancellationToken);
        var activeUserId = DiscoverActiveUserId(clientRoots, diagnostics, cancellationToken);
        if (!string.IsNullOrWhiteSpace(activeUserId)
            && !userIds.Contains(activeUserId, StringComparer.OrdinalIgnoreCase))
        {
            userIds.Insert(0, activeUserId);
        }

        var result = new List<DetectedGameInstallation>();
        foreach (var steamApps in steamAppsRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<string> manifests;
            try
            {
                manifests = Directory.EnumerateFiles(steamApps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add(ScanDiagnostic(Store, steamApps, ex));
                continue;
            }

            foreach (var manifestPath in manifests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var stream = File.OpenRead(manifestPath);
                    var document = KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(stream);
                    var root = document.Root;
                    if (!TryScalar(root, "appid", out var appId)
                        || !TryScalar(root, "installdir", out var installDir))
                    {
                        diagnostics.Add(ScanDiagnostic(Store, manifestPath, "format", "Steam manifest is missing appid or installdir."));
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
                        StoreUserIds = userIds,
                        ActiveStoreUserId = activeUserId
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    diagnostics.Add(ScanDiagnostic(Store, manifestPath, ex));
                }
            }
        }

        return new LauncherInstallationScanResult { Installations = result, Diagnostics = diagnostics };
    }

    private static string ResolveSteamAppsRoot(string root)
    {
        var fullPath = Path.GetFullPath(root);
        return string.Equals(
            Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            "steamapps",
            StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : Path.Combine(fullPath, "steamapps");
    }

    private static void AddLibraryFolders(
        string steamAppsRoot,
        ISet<string> roots,
        ICollection<DiscoveryDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(steamAppsRoot, "libraryfolders.vdf");
        if (!File.Exists(path))
        {
            return;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(path);
            var document = KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(stream);
            foreach (var pair in document.Root)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!pair.Key.All(char.IsDigit) || !TryScalar(pair.Value, "path", out var libraryPath))
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            diagnostics.Add(ScanDiagnostic(GameStore.Steam, path, ex));
        }
    }

    private static List<string> DiscoverUserIds(
        IEnumerable<string> clientRoots,
        ICollection<DiscoveryDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var clientRoot in clientRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var userdata = Path.Combine(clientRoot, "userdata");
            if (!Directory.Exists(userdata))
            {
                continue;
            }
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(userdata))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var id = Path.GetFileName(directory);
                    if (!string.IsNullOrWhiteSpace(id) && id.All(char.IsDigit))
                    {
                        output.Add(id);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add(ScanDiagnostic(GameStore.Steam, userdata, ex));
            }
        }
        return output.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string DiscoverActiveUserId(
        IEnumerable<string> clientRoots,
        ICollection<DiscoveryDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(string AccountId, long Timestamp)>();
        foreach (var clientRoot in clientRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(clientRoot, "config", "loginusers.vdf");
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                using var stream = File.OpenRead(path);
                var document = KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(stream);
                foreach (var pair in document.Root)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryScalar(pair.Value, "MostRecent", out var mostRecent)
                        || mostRecent != "1"
                        || !TryConvertSteamId64(pair.Key, out var accountId))
                    {
                        continue;
                    }
                    TryScalar(pair.Value, "Timestamp", out var timestampText);
                    _ = long.TryParse(timestampText, out var timestamp);
                    candidates.Add((accountId, timestamp));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add(ScanDiagnostic(GameStore.Steam, path, ex));
            }
        }
        return candidates
            .OrderByDescending(candidate => candidate.Timestamp)
            .Select(candidate => candidate.AccountId)
            .FirstOrDefault() ?? string.Empty;
    }

    internal static bool TryConvertSteamId64(string value, out string accountId)
    {
        accountId = string.Empty;
        if (!ulong.TryParse(value, out var steamId64))
        {
            return false;
        }
        accountId = ((uint)(steamId64 & uint.MaxValue)).ToString();
        return true;
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

    public LauncherInstallationScanResult Scan(IEnumerable<string> roots, CancellationToken cancellationToken)
    {
        var result = new List<DetectedGameInstallation>();
        var diagnostics = new List<DiscoveryDiagnostic>();
        foreach (var root in roots ?? Array.Empty<string>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }
                var manifestsRoot = ResolveManifestsRoot(root);
                if (!Directory.Exists(manifestsRoot))
                {
                    continue;
                }
                IReadOnlyList<string> manifests;
                try
                {
                    manifests = Directory.EnumerateFiles(manifestsRoot, "*.item", SearchOption.TopDirectoryOnly).ToList();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    diagnostics.Add(ScanDiagnostic(Store, manifestsRoot, ex));
                    continue;
                }
                foreach (var path in manifests)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var stream = File.OpenRead(path);
                        using var document = JsonDocument.Parse(stream);
                        var json = document.RootElement;
                        var installPath = GetString(json, "InstallLocation");
                        if (string.IsNullOrWhiteSpace(installPath))
                        {
                            diagnostics.Add(ScanDiagnostic(Store, path, "format", "Epic manifest is missing InstallLocation."));
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
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        diagnostics.Add(ScanDiagnostic(Store, path, ex));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add(ScanDiagnostic(Store, root, ex));
            }
        }
        return new LauncherInstallationScanResult { Installations = result, Diagnostics = diagnostics };
    }

    private static string ResolveManifestsRoot(string root)
    {
        if (string.Equals(
                Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                "Manifests",
                StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }
        var nested = Path.Combine(root, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        return Directory.Exists(nested) ? nested : root;
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
}

public sealed class GogInstallationScanner : ILauncherInstallationScanner
{
    public GameStore Store => GameStore.Gog;

    public LauncherInstallationScanResult Scan(IEnumerable<string> roots, CancellationToken cancellationToken)
    {
        var diagnostics = new List<DiscoveryDiagnostic>();
        var result = ScanRegistry(diagnostics, cancellationToken);
        foreach (var root in roots ?? Array.Empty<string>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }
                foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add(ScanDiagnostic(Store, root, ex));
            }
        }
        return new LauncherInstallationScanResult
        {
            Installations = result
                .GroupBy(item => $"{item.StoreGameId}|{item.BasePath}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList(),
            Diagnostics = diagnostics
        };
    }

    private static List<DetectedGameInstallation> ScanRegistry(
        ICollection<DiscoveryDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var result = new List<DetectedGameInstallation>();
        if (!OperatingSystem.IsWindows())
        {
            return result;
        }
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = $"HKLM ({view})\\SOFTWARE\\GOG.com\\Games";
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
                    cancellationToken.ThrowIfCancellationRequested();
                    try
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
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        diagnostics.Add(ScanDiagnostic(GameStore.Gog, $"{root}\\{subKeyName}", ex));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add(ScanDiagnostic(GameStore.Gog, root, ex));
            }
        }
        return result;
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

internal static class LauncherScanDiagnosticFactory
{
    public static DiscoveryDiagnostic ScanDiagnostic(GameStore store, string rootPath, Exception exception) =>
        ScanDiagnostic(store, rootPath, ErrorCategory(exception), exception.Message);

    public static DiscoveryDiagnostic ScanDiagnostic(GameStore store, string rootPath, string category, string message) => new()
    {
        Severity = DiscoveryDiagnosticSeverity.Warning,
        Code = "launcher-scan-failed",
        ProviderId = store.ToString().ToLowerInvariant(),
        RootPath = rootPath,
        Category = category,
        Message = $"Could not scan {store} source '{rootPath}': {message}"
    };

    private static string ErrorCategory(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "permission",
        IOException => "io",
        JsonException or InvalidDataException or FormatException => "format",
        _ => "unexpected"
    };
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
        catch
        {
        }
    }
}

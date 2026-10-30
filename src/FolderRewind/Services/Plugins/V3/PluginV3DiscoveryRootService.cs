using FolderRewind.Plugin.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.Services.Plugins.V3;

internal static class PluginV3DiscoveryRootService
{
    public static async Task<IReadOnlyList<string>> BuildDefaultRootsAsync(PluginId pluginId)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddExistingDirectory(roots, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        AddExistingDirectory(roots, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        // ObservableCollection belongs to the UI Dispatcher. Discovery workers only
        // consume this immutable path snapshot.
        var managedPaths = new List<string>();
        var rememberedPaths = new List<string>();
        await UiDispatcherService.RunOnUiAsync(() =>
        {
            if (ConfigService.CurrentConfig.GlobalSettings.GameDiscovery.PluginRoots
                .TryGetValue(pluginId.Value, out var remembered))
                rememberedPaths.AddRange(remembered ?? new List<string>());
            foreach (var config in ConfigService.CurrentConfig.BackupConfigs.Where(value =>
                         string.Equals(value.Kind.OwnerId, pluginId.Value, StringComparison.OrdinalIgnoreCase)))
            {
                managedPaths.AddRange(config.SourceFolders.Select(folder => folder.Path));
            }
        }).ConfigureAwait(false);

        foreach (var path in rememberedPaths) AddExistingDirectory(roots, path);
        foreach (var path in managedPaths)
        {
            AddExistingDirectory(roots, path);
            var parent = TryGetParent(path);
            AddExistingDirectory(roots, parent);
            AddExistingDirectory(roots, TryGetParent(parent));
        }

        return roots.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static async Task RememberUserRootsAsync(PluginId pluginId, IEnumerable<string> paths)
    {
        var roots = NormalizeUserRoots(paths);
        if (roots.Count == 0) return;
        await UiDispatcherService.RunOnUiAsync(() =>
        {
            var settings = ConfigService.CurrentConfig.GlobalSettings.GameDiscovery;
            var previous = settings.PluginRoots;
            previous.TryGetValue(pluginId.Value, out var known);
            var combined = (known ?? new List<string>()).Concat(roots)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(OnboardingOperationBudgets.DiscoveryCandidates).ToList();
            if (known != null && known.SequenceEqual(combined, StringComparer.OrdinalIgnoreCase)) return;
            var updated = new Dictionary<string, List<string>>(previous, StringComparer.OrdinalIgnoreCase)
            {
                [pluginId.Value] = combined
            };
            settings.PluginRoots = updated;
            var save = ConfigService.SaveWithResult(publishSavedEvent: false);
            if (!save.Success)
            {
                settings.PluginRoots = previous;
                LogService.LogWarning($"Could not remember discovery roots for '{pluginId.Value}': {save.ErrorMessage}",
                    "PluginV3Discovery");
                return;
            }
            ConfigService.PublishSaved();
        }).ConfigureAwait(false);
    }

    public static IReadOnlyList<string> NormalizeUserRoots(IEnumerable<string>? paths)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths ?? Array.Empty<string>())
        {
            AddExistingDirectory(roots, path);
        }
        return roots.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? TryGetParent(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Directory.GetParent(path)?.FullName; }
        catch { return null; }
    }

    private static void AddExistingDirectory(ISet<string> roots, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            roots.Add(Path.GetFullPath(path));
        }
    }
}

#if MINECRAFT_REAL_UI_ACCEPTANCE
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Packaging;
using FolderRewind.Services.Plugins;
using FolderRewind.Services.Plugins.V3;
using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FolderRewind.Services.Development;

// Isolated opt-in bootstrap only: the application retains its production pages and services.
internal static class MinecraftRealUiAcceptance
{
    internal static void Start(Window window)
    {
        if (Environment.GetEnvironmentVariable("FOLDERREWIND_REAL_MINECRAFT_UI") != "1") return;
        var data = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_DATA_ROOT");
        if (!AppRuntimeInfo.IsMsiDistribution || string.IsNullOrWhiteSpace(data) || !Path.IsPathFullyQualified(data))
            throw new InvalidOperationException("Real Minecraft acceptance requires an isolated MSI data root.");
        var profile = Directory.GetParent(Path.GetFullPath(data))?.FullName;
        if (profile is null || !string.Equals(Path.GetFileName(data), "appdata", StringComparison.OrdinalIgnoreCase)
            || !ConfigService.ConfigDirectory.StartsWith(Path.GetFullPath(data).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(profile, "real-ui-owner"))
            || File.ReadAllText(Path.Combine(profile, "real-ui-owner")).Trim() != "minerewind-phase3"
            || ConfigService.CurrentConfig.BackupConfigs.Count != 0)
            throw new InvalidOperationException("Real Minecraft acceptance requires a fresh marked profile.");
        TaskObserver.Observe(InitializeAsync(window, profile), nameof(MinecraftRealUiAcceptance));
    }

    private static async Task InitializeAsync(Window window, string profile)
    {
        try
        {
            using var request = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(profile, "real-ui.json")));
            var run = Path.GetFullPath(request.RootElement.GetProperty("runRoot").GetString()!);
            string Contained(string path)
            {
                var full = Path.GetFullPath(path);
                if (!full.StartsWith(run.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Acceptance paths must be inside the run root.");
                return full;
            }
            Contained(profile);
            var package = Contained(request.RootElement.GetProperty("package").GetString()!);
            PluginService.Initialize();
            await PluginService.Initialization;
            var installed = await PluginV3PackageService.InstallAsync(package, PluginInstallProvenance.Manual,
                request.RootElement.GetProperty("sha256").GetString());
            if (!installed.Success || !(await PluginV3PackageService.SetEnabledAsync(new PluginId("com.folderrewind.minerewind"), true)).Success)
                throw new IOException("Candidate installation or activation failed.");
            var configs = new[] { "Core Full", "Native Full" }.Select(name => new BackupConfig
            {
                Id = Guid.NewGuid().ToString(), Name = name,
                DestinationPath = Contained(Path.Combine(profile, "repository", name.Replace(' ', '-'))),
                Kind = new() { OwnerId = "com.folderrewind.minerewind", KindId = "minecraft-saves" }
            }).ToArray();
            configs[1].ArtifactTransformPolicy = new ArtifactTransformPolicySettings
            {
                ExperimentalEnabled = true,
                Transformer = new() { PluginId = "com.folderrewind.minerewind", TransformerId = "minecraft-world-full" },
                Parameters = { ["representation"] = JsonSerializer.SerializeToElement("incremental-v1") },
                FailureBehavior = PersistedArtifactTransformFailureBehavior.RequireTransform
            };
            foreach (var config in configs)
                foreach (var world in request.RootElement.GetProperty("worlds").EnumerateArray())
                {
                    var path = Contained(world.GetProperty("path").GetString()!);
                    if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
                    config.SourceFolders.Add(new ManagedFolder { Id = Guid.NewGuid().ToString(), Path = path,
                        DisplayName = world.GetProperty("name").GetString()! });
                }
            if (configs[0].SourceFolders.Count == 0) throw new InvalidDataException("No copied worlds supplied.");
            ConfigService.CurrentConfig.GlobalSettings.EnableNotifications = false;
            ConfigService.CurrentConfig.GlobalSettings.Plugins.AutoCheckUpdates = false;
            foreach (var config in configs) ConfigService.CurrentConfig.BackupConfigs.Add(config);
            var save = await ConfigService.SaveAsync();
            if (!save.Success) throw new IOException("Unable to persist isolated configuration.");
            if (!NavigationService.NavigateTo("Manager")) throw new InvalidOperationException("Manager navigation failed.");
            // Expose a read-only runtime observation when requested by the external UI test.
            var timer = window.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(200);
            timer.Tick += (_, _) =>
            {
                var requestPath = Path.Combine(profile, "real-ui-observe.request");
                if (!File.Exists(requestPath)) return;
                File.Delete(requestPath);
                var snapshot = PluginV3RuntimeService.Runtime.GetSnapshot(new PluginId("com.folderrewind.minerewind"));
                var observation = Path.Combine(profile, "real-ui-observation.json");
                var temporary = observation + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new
                { activeLeases = snapshot.ActiveLeases, observedUtc = DateTimeOffset.UtcNow }));
                File.Move(temporary, observation, overwrite: true);
            };
            window.Closed += (_, _) => timer.Stop();
            timer.Start();
            await File.WriteAllTextAsync(Path.Combine(profile, "real-ui-ready.json"), JsonSerializer.Serialize(new
            {
                status = "READY", configs = configs.Select(config => new { name = config.Name, configId = config.Id,
                    repository = config.DestinationPath,
                    worlds = config.SourceFolders.Select(f => new { id = f.Id, name = f.DisplayName, path = f.Path }).ToArray() }).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(profile, "real-ui-error.txt"), error.ToString());
        }
    }
}
#endif

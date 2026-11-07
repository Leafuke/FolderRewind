#if MINECRAFT_COORDINATION_UI_ACCEPTANCE
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Packaging;
using FolderRewind.Services.KnotLink;
using FolderRewind.Services.Plugins;
using FolderRewind.Services.Plugins.V3;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Development;

// Opt-in, isolated acceptance output only. No hook or test support is compiled into the distribution.
internal static class MinecraftCoordinationUiAcceptance
{
    private static readonly PluginId Plugin = new("com.folderrewind.minerewind");
    internal static void Start(Window window)
    {
        var root = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_DATA_ROOT");
        if (!AppRuntimeInfo.IsMsiDistribution || root is null || !Path.IsPathFullyQualified(root)
            || !ConfigService.ConfigDirectory.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(root, "coordination-ui-owner"))
            || File.ReadAllText(Path.Combine(root, "coordination-ui-owner")).Trim() != "minerewind-phase1") return;
        TaskObserver.Observe(InitializeAsync(window, root), nameof(MinecraftCoordinationUiAcceptance));
    }

    private static async Task InitializeAsync(Window window, string root)
    {
        try
        {
            using var request = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "coordination-ui.json")));
            PluginService.Initialize();
            await PluginService.Initialization;
            var installed = await PluginV3PackageService.InstallAsync(request.RootElement.GetProperty("package").GetString()!,
                PluginInstallProvenance.Manual, request.RootElement.GetProperty("sha256").GetString());
            if (!installed.Success) throw new IOException("Candidate installation failed.");
            if (!(await PluginV3PackageService.SetEnabledAsync(Plugin, true)).Success) throw new IOException("Candidate activation failed.");
            NavigationService.NavigateTo("Settings", NavigationService.SettingsKnotLinkTarget);
            PluginCapabilityLease<IBackupRunCoordinationCapability>? lease = null;
            IBackupRunCoordinationSession? session = null;
            var sendsSucceed = false;
            var timer = window.DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(100);
            var busy = false;
            timer.Tick += async (_, _) =>
            {
                var path = Path.Combine(root, "coordination-command");
                if (busy || !File.Exists(path)) return;
                busy = true;
                try
                {
                    if (new FileInfo(path).Length > 128) throw new InvalidDataException();
                    var command = (await File.ReadAllTextAsync(path)).Trim(); File.Delete(path);
                    switch (command)
                    {
                        case "uncertain": KnotLinkCoordinationChannel.Gate.MarkUncertain(); break;
                        case "hold":
                            lease ??= PluginV3RuntimeService.Runtime.TryAcquire<IBackupRunCoordinationCapability>(Plugin)!;
                            var folder = new FolderSnapshot(Guid.NewGuid(), root, "fixture", new Dictionary<StateOwnerId, ProviderStateSnapshot>());
                            var config = new ConfigSnapshot("coordination-ui", new("1"), lease.Capability.Kind, "fixture", [folder], new Dictionary<StateOwnerId, ProviderStateSnapshot>());
                            session ??= await lease.Capability.CreateSessionAsync(new(Guid.NewGuid(), config,
                                [new(folder, new Whole())], ConsistencyIntent.Prefer), lease.Context);
                            break;
                        case "release":
                            if (session is not null) { await session.CompleteAsync(new(OperationOutcome.Success, [], BackupTerminalDispatch.NotRequired), default); await session.DisposeAsync(); session = null; }
                            lease?.Dispose(); lease = null; break;
                        case "failed-send":
                            sendsSucceed = false;
                            await KnotLinkCoordinationChannel.Gate.DispatchAsync(null, _ => Task.FromResult(sendsSucceed), default); break;
                        case "allow-send": sendsSucceed = true; break;
                        case "reset-race":
                            await File.WriteAllTextAsync(Path.Combine(root, "reset-race-result"), (await PluginV3CoordinationRecoveryService.ResetAsync(default)).ToString()); break;
                        case "restore-changed":
                            NotificationService.ShowError(new RestoreCoordinationChangedException().Message); break;
                        case "close": window.Close(); break;
                    }
                    await File.WriteAllTextAsync(Path.Combine(root, "coordination-command-done"), command);
                }
                catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(root, "coordination-error.txt"), ex.ToString()); }
                finally { busy = false; }
            };
            window.Closed += (_, _) => timer.Stop(); timer.Start();
            await File.WriteAllTextAsync(Path.Combine(root, "coordination-ready"), "ready");
        }
        catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(root, "coordination-error.txt"), ex.ToString()); }
    }

    private sealed class Whole : IBackupSourceBoundaryView
    {
        public bool ContainsFile(string path) => true;
        public bool MayIntersectDirectory(string path) => true;
    }
}
#endif

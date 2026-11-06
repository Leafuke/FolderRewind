#if CLOUD_CREDENTIAL_UI_ACCEPTANCE
using FolderRewind.Models;
using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace FolderRewind.Services.Development;

// Compiled only in the isolated fault/acceptance output, and restricted to a marked private test package.
internal static class CloudCredentialUiAcceptance
{
    private static string? _root;
    internal static void Start(Window window)
    {
        if (!AppRuntimeInfo.IsPackaged || !Package.Current.Id.Name.StartsWith("Leafuke.FolderRewind.Issue66Test", StringComparison.Ordinal)) return;
        var root = Path.Combine(ConfigService.ConfigDirectory, "issue66-acceptance");
        if (!File.Exists(Path.Combine(root, "acceptance-owner"))) return;
        _root = root;
        var timer = window.DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(150);
        var busy = false;
        timer.Tick += async (_, _) =>
        {
            if (busy) return;
            var path = Path.Combine(root, "command");
            if (!File.Exists(path)) return;
            busy = true;
            try
            {
                if (new FileInfo(path).Length > 128) throw new InvalidDataException();
                var command = (await File.ReadAllTextAsync(path)).Trim(); File.Delete(path);
                var project = ConfigService.CurrentConfig.BackupConfigs.Single();
                switch (command)
                {
                    case "project":
                        Require(NavigationService.NavigateTo("CloudProject", new CloudSetupNavigationParameter(project.Id, Intent: CloudSetupIntent.ProjectStatus))); break;
                    case "missing":
                        Require(NavigationService.NavigateTo("CloudSetup", new CloudSetupNavigationParameter(project.Id, ManageConnection: true, ProfileId: "missing"))); break;
                    case "recovery-missing":
                        Require(NavigationService.NavigateTo("CloudRecovery", new CloudSetupNavigationParameter(project.Id, ProfileId: "missing", Intent: CloudSetupIntent.Recover))); break;
                    case "edit":
                        Require(NavigationService.NavigateTo("CloudSetup", new CloudSetupNavigationParameter(project.Id, ManageConnection: true, ProfileId: project.Cloud.ConnectionProfileId))); break;
                    case "connections": Require(NavigationService.NavigateTo("CloudConnections")); break;
                    case "add-profile":
                        var id = project.Cloud.ConnectionProfileId ?? throw new InvalidDataException();
                        Require(ConfigService.CurrentConfig.CloudConnections.All(p => p.Id != id));
                        ConfigService.CurrentConfig.CloudConnections.Add(new CloudConnectionProfile
                        {
                            Id = id, DisplayName = "Issue66 legacy", Scope = CloudConnectionScope.Project, OwnerProjectId = project.Id,
                            CredentialReference = Path.Combine(root, "legacy.conf"), RemoteName = "old",
                            ExecutablePath = ConfigService.CurrentConfig.GlobalSettings.RcloneExecutablePath,
                            Access = CloudConnectionAccess.ConnectedUnverified, CredentialOwnership = CloudCredentialOwnership.ExternalReference
                        });
                        Require((await ConfigService.SaveAsync()).Success); break;
                    case "remove-profile":
                        ConfigService.CurrentConfig.CloudConnections.Clear(); Require((await ConfigService.SaveAsync()).Success); break;
                    case "hold": await File.WriteAllTextAsync(Path.Combine(root, "hold"), "hold"); break;
                    case "release": File.Delete(Path.Combine(root, "hold")); break;
                    default: throw new InvalidDataException();
                }
                await File.WriteAllTextAsync(Path.Combine(root, "response"), "PASS " + command);
            }
            catch (Exception error)
            {
                await File.WriteAllTextAsync(Path.Combine(root, "response"), "FAIL " + error.GetType().Name);
            }
            finally { busy = false; }
        };
        window.Closed += (_, _) => timer.Stop(); timer.Start();
    }
    internal static async Task PauseInitializationAsync(CancellationToken token)
    {
        if (_root is not { } root || !File.Exists(Path.Combine(root, "hold"))) return;
        await File.WriteAllTextAsync(Path.Combine(root, "initialization-entered"), "entered", token);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(30));
        while (File.Exists(Path.Combine(root, "hold"))) await Task.Delay(50, budget.Token);
    }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException(); }
}
#endif

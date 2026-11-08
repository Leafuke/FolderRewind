#if MINECRAFT_COORDINATION_UI_ACCEPTANCE
using FolderRewind.Controls;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Packaging;
using FolderRewind.Services.Plugins;
using FolderRewind.Services.Plugins.V3;
using FolderRewind.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Development;

// Uses the exact installed candidate and production dialog/Canvas in a marked, isolated MSI profile.
// The enclosing symbol itself requires fault-injection and native-acceptance build properties.
internal static class MinecraftReadUiAcceptance
{
    internal static void Start(Window window)
    {
        var root = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_DATA_ROOT");
        if (!AppRuntimeInfo.IsMsiDistribution || root is null || !Path.IsPathFullyQualified(root)
            || !ConfigService.ConfigDirectory.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(root, "read-ui-owner"))
            || File.ReadAllText(Path.Combine(root, "read-ui-owner")).Trim() != "minerewind-phase2") return;
        TaskObserver.Observe(InitializeAsync(window, root), nameof(MinecraftReadUiAcceptance));
    }

    private static async Task InitializeAsync(Window window, string root)
    {
        try
        {
            using var request = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "read-ui.json")));
            PluginService.Initialize(); await PluginService.Initialization;
            var installed = await PluginV3PackageService.InstallAsync(request.RootElement.GetProperty("package").GetString()!,
                PluginInstallProvenance.Manual, request.RootElement.GetProperty("sha256").GetString());
            if (!installed.Success || !(await PluginV3PackageService.SetEnabledAsync(new("com.folderrewind.minerewind"), true)).Success)
                throw new IOException("Candidate installation or activation failed.");
            var world = Path.Combine(root, "fixture-world"); Directory.CreateDirectory(Path.Combine(world, "region"));
            WriteLevel(Path.Combine(world, "level.dat"));
            for (var x = -1; x <= 1; x++) for (var z = -1; z <= 1; z++)
                await File.WriteAllBytesAsync(Path.Combine(world, "region", $"r.{x}.{z}.mca"), new byte[8192]);
            var folder = new ManagedFolder { Id = Guid.NewGuid().ToString(), Path = world, DisplayName = "MineRewind phase 2" };
            var config = new BackupConfig { Id = Guid.NewGuid().ToString(), Name = "Read UI fixture", DestinationPath = Path.Combine(root, "repository"),
                Kind = new() { OwnerId = "com.folderrewind.minerewind", KindId = "minecraft-saves" } };
            config.SourceFolders.Add(folder); ConfigService.CurrentConfig.BackupConfigs.Add(config);
            var delayed = new DelayedMetadata();
            var delayedConfig = new BackupConfig { Id = Guid.NewGuid().ToString(), Name = "Delayed metadata",
                Kind = new() { OwnerId = delayed.Id.Value, KindId = "metadata" } };
            delayedConfig.SourceFolders.Add(folder);
            using (var official = PluginV3RuntimeService.Runtime.TryAcquire<IFolderMetadataCapability>(new("com.folderrewind.minerewind"))!)
                if (!(await PluginV3RuntimeService.Runtime.ActivateAsync(new(delayed.Id, () => delayed,
                    new(delayed.Id, new Dictionary<string, JsonElement>()), [], official.Context.HostServices, new NoWrites()))).Success)
                    throw new InvalidOperationException("Delayed metadata fixture activation failed.");
            // An older non-cooperative provider result must not replace a newer load, even without cancellation.
            var revisionVm = new ViewModels.FolderDetailsDialogViewModel();
            var older = revisionVm.LoadAsync(delayedConfig, folder, default);
            await delayed.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await revisionVm.LoadAsync(delayedConfig, folder, default);
            delayed.Release.TrySetResult(); await older;
            if (!revisionVm.Sections.SelectMany(s => s.Items).Any(i => i.Value == "Latest")
                || revisionVm.Sections.SelectMany(s => s.Items).Any(i => i.Value == "Late"))
                throw new InvalidOperationException("A stale metadata revision was displayed.");
            delayed.Reset();
            await File.WriteAllTextAsync(Path.Combine(root, "read-ui-revision-checked.txt"), "PASS");
            var content = new Grid { Padding = new Thickness(24), RowSpacing = 12 };
            content.RowDefinitions.Add(new() { Height = GridLength.Auto }); content.RowDefinitions.Add(new() { Height = GridLength.Auto });
            content.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 }; content.Children.Add(actions);
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap }; AutomationProperties.SetAutomationId(status, "ReadUiStatus");
            Grid.SetRow(status, 1); content.Children.Add(status);
            var maps = new Grid { ColumnSpacing = 12 }; maps.ColumnDefinitions.Add(new()); maps.ColumnDefinitions.Add(new());
            Grid.SetRow(maps, 2); content.Children.Add(maps);
            var left = new SpatialPreviewCanvas { ShowGrid = true, MaximumConcurrentRequests = 2, MaximumCachedTiles = 64 };
            var right = new SpatialPreviewCanvas { ShowGrid = true, MaximumConcurrentRequests = 2, MaximumCachedTiles = 64 };
            AutomationProperties.SetAutomationId(left, "ReadUiLeftMap"); AutomationProperties.SetAutomationId(right, "ReadUiRightMap");
            maps.Children.Add(left); Grid.SetColumn(right, 1); maps.Children.Add(right);
            left.StatusChanged += text => status.Text = text; right.StatusChanged += text => status.Text = text;
            PluginV3SpatialPreview? session = null;
            SpatialPreviewLayer? layer = null;
            FolderDetailsDialog? dialog = null;
            var busy = false;
            async Task Guard(Func<Task> action)
            {
                if (busy) return; busy = true;
                try { await action(); } catch (Exception error) { await WriteAsync("error", error.ToString()); }
                finally { busy = false; }
            }
            void Button(string id, string label, Func<Task> action)
            {
                var button = new Button { Content = label }; AutomationProperties.SetAutomationId(button, id);
                button.Click += async (_, _) => await Guard(action); actions.Children.Add(button);
            }
            Button("ReadUiDetails", "Details", async () =>
            {
                dialog = new FolderDetailsDialog(); AutomationProperties.SetAutomationId(dialog, "ReadUiDetailsDialog");
                var loading = dialog.InitializeAsync(config, folder);
                if (dialog.ViewModel.Sections.Count != 1) throw new InvalidDataException("Base metadata was not immediately available.");
                var showing = AppDialogService.Default.ShowCustomAsync(dialog, content.XamlRoot);
                try
                {
                    await loading;
                    if (!dialog.ViewModel.Sections.SelectMany(s => s.Items).Any(i => i.Value == "Phase 2 NBT"))
                        throw new InvalidDataException("Candidate metadata was not displayed.");
                    await WriteAsync("details-loaded", "PASS");
                }
                catch (OperationCanceledException) { }
                await showing;
                var count = dialog.ViewModel.Sections.Count;
                await Task.Delay(200);
                if (count != dialog.ViewModel.Sections.Count) throw new InvalidDataException("Closed dialog received late metadata.");
                dialog = null; await WriteAsync("details-closed", "PASS");
            });
            Button("ReadUiMaps", "Maps", async () =>
            {
                if (session is not null) { left.Clear(); right.Clear(); await session.CloseAsync(); }
                session = PluginV3SpatialPreview.Open(config.Id, Guid.Parse(folder.Id));
                layer = (await session.DescribeAsync(default)).Layers.First();
                left.SetSource(session, layer, 64, true, "surface", true); right.SetSource(session, layer, 64, true, "surface", true);
                await WriteAsync("maps-open", "PASS");
            });
            Button("ReadUiSlowDetails", "Slow details", async () =>
            {
                dialog = new FolderDetailsDialog(); var loading = dialog.InitializeAsync(delayedConfig, folder);
                var showing = AppDialogService.Default.ShowCustomAsync(dialog, content.XamlRoot);
                await delayed.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await WriteAsync("slow-started", "PASS");
                await showing; delayed.Release.TrySetResult();
                var canceled = false;
                try { await loading; } catch (OperationCanceledException) { canceled = true; }
                if (!canceled || dialog.ViewModel.Sections.SelectMany(s => s.Items).Any(i => i.Value == "Late"))
                    throw new InvalidOperationException("A closed dialog accepted a non-cooperative result.");
                dialog = null; await WriteAsync("slow-closed", "PASS");
            });
            Button("ReadUiChange", "Change views", async () =>
            {
                if (session is null || layer is null) throw new InvalidOperationException("Open maps first.");
                for (var i = 0; i < 20; i++)
                {
                    left.Zoom(i % 2 == 0 ? 2 : .5); right.Locate(i * 16, -i * 16, true);
                    left.SetSource(session, layer, 60 + i, false, i % 2 == 0 ? "slice" : "surface", true);
                    right.SetSource(session, layer, 60 + i, false, i % 2 == 0 ? "cave" : "surface", true);
                }
                await WriteAsync("views-changed", "PASS");
            });
            Button("ReadUiCloseMaps", "Close maps", async () =>
            {
                left.Clear(); right.Clear(); if (session is not null) await session.CloseAsync(); session = null;
                if (PluginV3RuntimeService.Runtime.GetSnapshot(new("com.folderrewind.minerewind")).ActiveLeases != 0)
                    throw new InvalidOperationException("Preview leases were not drained.");
                await WriteAsync("maps-closed", "PASS");
            });
            Button("ReadUiExit", "Exit", () => { window.Close(); return Task.CompletedTask; });
            window.Closed += (_, _) => { left.Clear(); right.Clear(); if (session is not null) TaskObserver.Observe(session.CloseAsync(), nameof(MinecraftReadUiAcceptance)); };
            window.Content = content; await WriteAsync("ready", "PASS");

            Task WriteAsync(string name, string value) => File.WriteAllTextAsync(Path.Combine(root, "read-ui-" + name + ".txt"), value);
        }
        catch (Exception error) { await File.WriteAllTextAsync(Path.Combine(root, "read-ui-error.txt"), error.ToString()); }
    }
    private static void WriteLevel(string path)
    {
        using var output = File.Create(path);
        void Number(long value, int count) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); output.Write(bytes[(8 - count)..]); }
        void String(string text) { var bytes = Encoding.UTF8.GetBytes(text); Number(bytes.Length, 2); output.Write(bytes); }
        void Tag(byte type, string name) { output.WriteByte(type); String(name); }
        Tag(10, ""); Tag(10, "Data"); Tag(8, "LevelName"); String("Phase 2 NBT");
        Tag(3, "GameType"); Number(1, 4); Tag(4, "RandomSeed"); Number(42, 8);
        Tag(4, "Time"); Number(48000, 8); Tag(3, "DataVersion"); Number(3465, 4);
        output.WriteByte(0); output.WriteByte(0);
    }
    private sealed class NoWrites : IPluginActivationStore
    {
        public ValueTask CommitAsync(PluginActivationCommit commit, CancellationToken token)
        { if (commit.ProviderStatePatches.Count > 0) throw new NotSupportedException(); return ValueTask.CompletedTask; }
    }
    private sealed class DelayedMetadata : IFolderRewindPlugin, IFolderMetadataCapability
    {
        internal PluginId Id { get; } = new("folderrewind.late-metadata");
        public ConfigKindRef Kind => new(new(Id.Value), "metadata");
        internal TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        private readonly int _uiThread = Environment.CurrentManagedThreadId;
        internal void Reset() { _calls = 0; Started = new(TaskCreationOptions.RunContinuationsAsynchronously); Release = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public ValueTask<PluginActivationResult> ActivateAsync(IPluginActivationContext context, CancellationToken token)
        { context.RegisterCapability<IFolderMetadataCapability>(this); return ValueTask.FromResult(PluginActivationResult.Empty); }
        public ValueTask DeactivateAsync(CancellationToken token) { Release.TrySetResult(); return ValueTask.CompletedTask; }
        public ValueTask<FolderMetadataResult> ReadAsync(FolderMetadataRequest request, PluginInvocationContext context)
        {
            if (Environment.CurrentManagedThreadId == _uiThread) throw new InvalidOperationException("Metadata blocked the UI thread.");
            var value = "Latest";
            if (Interlocked.Increment(ref _calls) == 1) { Started.TrySetResult(); Release.Task.GetAwaiter().GetResult(); value = "Late"; }
            return ValueTask.FromResult(new FolderMetadataResult([new("fixture", new("Fixture", new Dictionary<string, string>()),
                new(value, new Dictionary<string, string>()))], []));
        }
    }
}
#endif

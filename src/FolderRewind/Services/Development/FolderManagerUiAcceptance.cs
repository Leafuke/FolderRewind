#if FOLDER_MANAGER_UI_ACCEPTANCE
using FolderRewind.Models;
using FolderRewind.Services.Plugins.V3;
using FolderRewind.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FolderRewind.Services.Development;

// Separate from the ten-minute workload; runs only with its own explicit opt-in.
internal static class FolderManagerUiAcceptance
{
    private static string? _root;
    internal static void Prepare()
    {
        if (Environment.GetEnvironmentVariable("FOLDERREWIND_MANAGER_UI") != "1") return;
        var data = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_DATA_ROOT");
        if (!AppRuntimeInfo.IsMsiDistribution || string.IsNullOrWhiteSpace(data) || !Path.IsPathFullyQualified(data))
            throw new InvalidOperationException("Manager acceptance requires an isolated MSI data root.");
        var full = Path.GetFullPath(data);
        var root = Directory.GetParent(full)?.FullName;
        if (root is null || !File.Exists(Path.Combine(root, "acceptance-owner")) ||
            !string.Equals(Path.GetFileName(full), "appdata", StringComparison.OrdinalIgnoreCase) ||
            ConfigService.CurrentConfig.BackupConfigs.Count != 0)
            throw new InvalidOperationException("Manager acceptance requires a fresh marked root.");
        _root = root;
        for (var c = 0; c < 2; c++)
        {
            var config = new BackupConfig { Id = new Guid(c + 200, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0).ToString(),
                Name = $"Manager fixture {c}", DestinationPath = Path.Combine(root, "repository", c.ToString()) };
            for (var i = 0; i < 500; i++)
                config.SourceFolders.Add(new ManagedFolder { Id = new Guid(c + 10, 0, 0, 0, 0, 0, 0, 0, 0, (byte)(i >> 8), (byte)i).ToString(),
                    Path = Path.Combine(root, "source", c.ToString(), i.ToString()), DisplayName = $"Source {i:D4}" });
            ConfigService.CurrentConfig.BackupConfigs.Add(config);
        }
        ConfigService.CurrentConfig.GlobalSettings.EnableNotifications = false;
        ConfigService.CurrentConfig.GlobalSettings.Plugins.AutoCheckUpdates = false;
    }
    internal static void Start(Window window)
    {
        if (_root is null) return;
        _ = window.DispatcherQueue.TryEnqueue(() => TaskObserver.Observe(RunAsync(window), nameof(FolderManagerUiAcceptance)));
    }
    private static async Task RunAsync(Window window)
    {
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_root!, "ready.txt"), "Use winapp to open Manager, scroll, and select a visible folder; then write reset.request.");
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (!File.Exists(Path.Combine(_root!, "reset.request")) && DateTime.UtcNow < deadline) await Task.Delay(100);
            if (!File.Exists(Path.Combine(_root!, "reset.request"))) throw new InvalidOperationException("No reset request.");
            var page = Find<FolderManagerPage>(window.Content).FirstOrDefault() ?? throw new InvalidOperationException("Manager page missing.");
            var list = Find<ListView>(page).FirstOrDefault(x => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(x) == "FolderManagerFolderList")
                ?? throw new InvalidOperationException("Folder list missing.");
            var scroll = Find<ScrollViewer>(list).FirstOrDefault() ?? throw new InvalidOperationException("Scroll viewer missing.");
            var panel = list.ItemsPanelRoot as ItemsStackPanel ?? throw new InvalidOperationException("Unexpected list panel.");
            var first = panel.FirstVisibleIndex;
            var anchor = page.ViewModel.CurrentFoldersView[first] as ManagedFolder ?? throw new InvalidOperationException("Visible folder missing.");
            var container = list.ContainerFromIndex(first) as FrameworkElement ?? throw new InvalidOperationException("Visible container missing.");
            var beforeTop = container.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point()).Y;
            var selected = page.ViewModel.SelectedFolderItem ?? throw new InvalidOperationException("Select a folder before the reset.");
            if (scroll.VerticalOffset <= 0) throw new InvalidOperationException("Scroll before the reset.");
            var config = page.ViewModel.CurrentConfig ?? throw new InvalidOperationException("No current project.");
            var resets = 0;
            void Changed(object? _, NotifyCollectionChangedEventArgs e) { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; }
            page.ViewModel.CurrentFoldersView.CollectionChanged += Changed;
            try
            {
                config.SourceFolders.Move(0, config.SourceFolders.Count - 1);
                page.ViewModel.RefreshCurrentFoldersView();
                await Task.Delay(1200);
            }
            finally { page.ViewModel.CurrentFoldersView.CollectionChanged -= Changed; }
            var afterFirst = panel.FirstVisibleIndex;
            var afterAnchor = page.ViewModel.CurrentFoldersView[afterFirst] as ManagedFolder;
            var afterContainer = list.ContainerFromIndex(afterFirst) as FrameworkElement;
            var afterTop = afterContainer?.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point()).Y;
            var selectedId = page.ViewModel.SelectedFolderItem?.Id;
            var uiSelectedId = (list.SelectedItem as ManagedFolder)?.Id;
            if (resets < 1 || afterAnchor?.Id != anchor.Id || afterTop is null || Math.Abs(afterTop.Value - beforeTop) > 3 ||
                selectedId != selected.Id || uiSelectedId != selected.Id)
                throw new InvalidOperationException($"Reset did not preserve viewport or selection: resets={resets}, anchor={anchor.Id}/{afterAnchor?.Id}, top={beforeTop:F2}/{afterTop:F2}, selection={selected.Id}/{selectedId}/{uiSelectedId}.");
            await File.WriteAllTextAsync(Path.Combine(_root!, "reset-result.json"), JsonSerializer.Serialize(new
            { status = "PASS", resets, beforeTop, afterTop, anchorId = anchor.Id, selectedId, uiSelectedId }));
            var replaceDeadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(Path.Combine(_root!, "replace.request")) && DateTime.UtcNow < replaceDeadline) await Task.Delay(100);
            if (!File.Exists(Path.Combine(_root!, "replace.request"))) throw new InvalidOperationException("No model replacement request.");
            var replacementFirst = panel.FirstVisibleIndex;
            var replacementAnchor = (ManagedFolder)page.ViewModel.CurrentFoldersView[replacementFirst];
            var replacementContainer = list.ContainerFromIndex(replacementFirst) as FrameworkElement
                ?? throw new InvalidOperationException("Replacement anchor container missing.");
            var replacementBeforeTop = replacementContainer.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point()).Y;
            var previousSelected = page.ViewModel.SelectedFolderItem ?? throw new InvalidOperationException("Selection lost before model replacement.");
            var replacements = ConfigSnapshotMapper.Copy(config).SourceFolders;
            var replacementSelected = replacements.Single(folder => folder.Id == previousSelected.Id);
            replacementSelected.Path += "-model-replacement";
            var replacementResets = 0;
            void Replaced(object? _, NotifyCollectionChangedEventArgs e) { if (e.Action == NotifyCollectionChangedAction.Reset) replacementResets++; }
            page.ViewModel.CurrentFoldersView.CollectionChanged += Replaced;
            try { config.SourceFolders = replacements; await Task.Delay(1200); }
            finally { page.ViewModel.CurrentFoldersView.CollectionChanged -= Replaced; }
            var replacementAfterFirst = panel.FirstVisibleIndex;
            var replacementAfterAnchor = page.ViewModel.CurrentFoldersView[replacementAfterFirst] as ManagedFolder;
            var replacementAfterContainer = list.ContainerFromIndex(replacementAfterFirst) as FrameworkElement;
            var replacementAfterTop = replacementAfterContainer?.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point()).Y;
            var vmReplacement = page.ViewModel.SelectedFolderItem;
            var uiReplacement = list.SelectedItem as ManagedFolder;
            if (replacementResets != 1 || replacementAfterAnchor?.Id != replacementAnchor.Id || replacementAfterTop is null ||
                Math.Abs(replacementAfterTop.Value - replacementBeforeTop) > 3 ||
                ReferenceEquals(previousSelected, replacementSelected) ||
                !ReferenceEquals(vmReplacement, replacementSelected) || !ReferenceEquals(uiReplacement, replacementSelected) ||
                vmReplacement?.Path == previousSelected.Path || page.ViewModel.CurrentFoldersView.Count != 500 ||
                !page.ViewModel.CurrentFoldersView.SequenceEqual(replacements.Cast<object>()))
                throw new InvalidOperationException($"Model replacement failed: resets={replacementResets}, anchor={replacementAnchor.Id}/{replacementAfterAnchor?.Id}, top={replacementBeforeTop}/{replacementAfterTop}, VM/UI={vmReplacement?.Id}/{uiReplacement?.Id}.");
            await File.WriteAllTextAsync(Path.Combine(_root!, "replacement-result.json"), JsonSerializer.Serialize(new
            { status = "PASS", replacementResets, replacementBeforeTop, replacementAfterTop, anchorId = replacementAnchor.Id,
                selectedId = replacementSelected.Id, oldPath = previousSelected.Path, newPath = replacementSelected.Path,
                vmAndUiSelectedNewInstance = true, allModelsReplaced = replacements.All(folder => page.ViewModel.CurrentFoldersView.Contains(folder)) }, new JsonSerializerOptions { WriteIndented = true }));
            var switchDeadline = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(Path.Combine(_root!, "switch.request")) && DateTime.UtcNow < switchDeadline) await Task.Delay(100);
            if (!File.Exists(Path.Combine(_root!, "switch.request"))) throw new InvalidOperationException("No switch request.");
            // Queue another reset restore, then switch before its callback can run.
            config.SourceFolders.Move(0, config.SourceFolders.Count - 1);
            page.ViewModel.RefreshCurrentFoldersView();
            var next = ConfigService.CurrentConfig.BackupConfigs[1];
            page.ViewModel.CurrentConfig = next;
            page.ViewModel.CurrentConfig = config;
            page.ViewModel.CurrentConfig = next;
            await Task.Delay(300);
            if (!ReferenceEquals(page.ViewModel.CurrentConfig, next) || page.ViewModel.SelectedFolderItem is not null ||
                page.ViewModel.CurrentFoldersView.Count != 500)
                throw new InvalidOperationException("Cross-project switch retained an old selection or wrong projection.");
            var allocation = VerifyPreviewAllocations(next);
            await File.WriteAllTextAsync(Path.Combine(_root!, "manager-results.json"), JsonSerializer.Serialize(new
            { status = "PASS", resets, beforeTop, afterTop, anchorId = anchor.Id, selectedId, uiSelectedId, replacementResets,
                replacementBeforeTop, replacementAfterTop, modelReplacementPassed = true,
                crossProjectSelectionCleared = true, previewAllocations = allocation }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(_root!, "manager-results.json"), JsonSerializer.Serialize(new
            { status = "FAIL", error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    private static object VerifyPreviewAllocations(BackupConfig config)
    {
        long Measure(int payloadBytes)
        {
            var payload = JsonSerializer.SerializeToElement(new { data = new string('x', payloadBytes) });
            foreach (var folder in config.SourceFolders)
                folder.ProviderStates["folderrewind.performance"] = new ProviderStatePayload { SchemaVersion = 1, Data = payload };
            var origin = PluginV3ModelMapper.ToSnapshot(config);
            var id = Guid.Parse(config.SourceFolders[0].Id);
            for (var i = 0; i < 10; i++)
                if (!PluginV3SpatialPreview.IsCurrent(origin, id)) throw new InvalidOperationException("Warmup source changed.");
            var start = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
                if (!PluginV3SpatialPreview.IsCurrent(origin, id)) throw new InvalidOperationException("Preview source changed.");
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
        var small = Measure(64);
        var large = Measure(16 * 1024);
        if (small > 1024 * 1024 || large > 1024 * 1024 || large > small + 64 * 1024)
            throw new InvalidOperationException($"Preview currentness allocated too much: small={small}, large={large}.");
        return new { iterations = 1000, sources = config.SourceFolders.Count, smallPayloadBytes = 64,
            largePayloadBytes = 16 * 1024, smallAllocatedBytes = small, largeAllocatedBytes = large };
    }
    private static IEnumerable<T> Find<T>(object? root) where T : DependencyObject
    {
        if (root is not DependencyObject node) yield break;
        if (node is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in Find<T>(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
}
#endif

using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static class HistoryMergeInteraction
{
    private sealed record Row(MergeConflict Conflict, MergeResolution? Resolution)
    {
        public string Label => $"{(Resolution is null ? "□" : "✓")} {Conflict.Subject.SourceId} · {I18n.GetString("Merge_Conflict_" + Conflict.Kind)}\n"
            + (Conflict.Subject.Paths.IsEmpty ? I18n.GetString("Merge_WholeSource") : string.Join("\n", Conflict.Subject.Paths))
            + (Resolution is null ? "" : $" · {I18n.GetString("Merge_" + Resolution.Choice)}");
    }
    public static async Task ShowAsync(BackupConfig config, BranchId? source, CancellationToken token)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token);
        var restore = await NativeHistoryApplicationService.CreateRestoreServiceAsync(config, token);
        await restore.RecoverIncompleteAsync(token);
        var service = new HistoryMergeService(runtime, restore);
        MergeSession? session = null;
        int offset = 0;
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var sessions = new ComboBox { Header = I18n.GetString("Merge_Sessions"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var list = new ListView { SelectionMode = ListViewSelectionMode.Multiple, DisplayMemberPath = nameof(Row.Label) };
        var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 120 };
        var detailText = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 120 };
        var detailsContent = new StackPanel { Spacing = 8 };
        detailsContent.Children.Add(new TextBlock { Text = I18n.GetString("Merge_Explanation"), TextWrapping = TextWrapping.Wrap });
        detailsContent.Children.Add(detailText);
        detailsContent.Children.Add(preview);
        var details = new Expander
        {
            Header = I18n.GetString("Merge_Details"), HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new ScrollViewer { Content = detailsContent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        var controls = new List<AppBarButton>();
        var commands = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, IsDynamicOverflowEnabled = true };
        var content = new Grid { RowSpacing = 8 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            content.RowDefinitions.Add(new RowDefinition { Height = height });
        var statusScroller = new ScrollViewer { Content = status, MaxHeight = 64, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var children = new FrameworkElement[] { sessions, statusScroller, commands, list, details };
        for (int i = 0; i < children.Length; i++) { Grid.SetRow(children[i], i); content.Children.Add(children[i]); }
        var dialog = new ContentDialog
        {
            Title = I18n.GetString("Merge_Title"), Content = content,
            PrimaryButtonText = I18n.GetString("Merge_Apply"), CloseButtonText = I18n.GetString("Merge_Close"),
            DefaultButton = ContentDialogButton.Close
        };
        // Match the dialog template's width cap to the bounded content plus its padding.
        dialog.Resources["ContentDialogMaxWidth"] = 768d;
        AutomationProperties.SetAutomationId(dialog, "HistoryMergeDialog");
        AutomationProperties.SetAutomationId(sessions, "MergeSessions");
        AutomationProperties.SetAutomationId(list, "MergeConflicts");
        AutomationProperties.SetName(list, I18n.GetString("Merge_Title"));
        AutomationProperties.SetAutomationId(status, "MergeStatus");
        AutomationProperties.SetAutomationId(details, "MergeDetails");
        AutomationProperties.SetAutomationId(detailText, "MergeDiagnosticDetails");
        AutomationProperties.SetName(detailText, I18n.GetString("Merge_Details"));
        AutomationProperties.SetAutomationId(preview, "MergePreview");
        AutomationProperties.SetName(preview, I18n.GetString("Merge_Details"));
        var xamlRoot = MainWindowService.GetXamlRoot() ?? throw new InvalidOperationException("Merge requires an active window.");
        void Resize()
        {
            content.Width = Math.Max(0, Math.Min(720, xamlRoot.Size.Width - 96));
            content.Height = Math.Max(160, Math.Min(640, xamlRoot.Size.Height - 200));
            ((ScrollViewer)details.Content).MaxHeight = Math.Max(24, Math.Min(240, content.Height - 264));
        }
        // At small heights the expanded details replace the list, so neither is squeezed offscreen.
        details.Expanding += (_, _) =>
        {
            list.Visibility = Visibility.Collapsed;
            content.RowDefinitions[3].Height = new GridLength(0);
            content.RowDefinitions[4].Height = new GridLength(1, GridUnitType.Star);
        };
        details.Collapsed += (_, _) =>
        {
            list.Visibility = Visibility.Visible;
            content.RowDefinitions[3].Height = new GridLength(1, GridUnitType.Star);
            content.RowDefinitions[4].Height = GridLength.Auto;
        };
        void RootChanged(XamlRoot _, XamlRootChangedEventArgs args) => Resize();
        bool busy = false;
        void UpdateControls()
        {
            controls.ForEach(b => b.IsEnabled = !busy && MergeSessionActions.Allowed((string)b.Tag, session?.State, source is not null));
            dialog.IsPrimaryButtonEnabled = !busy && session?.State == MergeSessionState.Ready;
        }
        dialog.Closing += (_, args) => { if (busy && !token.IsCancellationRequested) args.Cancel = true; };
        void ShowDetails(string text)
        {
            detailText.Text = text;
            details.IsExpanded = !string.IsNullOrWhiteSpace(text);
        }
        string DiagnosticText(HistoryMergeDiagnostic diagnostic) => I18n.GetString("Merge_Diagnostic_" + diagnostic.Code)
            + "\n" + I18n.GetString(diagnostic.NextActionKey)
            + (string.IsNullOrWhiteSpace(diagnostic.Detail) ? "" : "\n" + diagnostic.Detail)
            + (diagnostic.SourceId is null ? "" : $"\nSource: {diagnostic.SourceId} · Version: {diagnostic.VersionId} · Representation: {diagnostic.RepresentationId}");
        void RefreshSessions()
        {
            sessions.ItemsSource = runtime.MergeSessions.List().Select(s => new ComboBoxItem
            { Content = $"{s.Plan.Theirs.Name} → {s.Plan.Ours.Name} · {I18n.GetString("Merge_State_" + s.State)} · {s.Id}", Tag = s.Id }).ToArray();
        }
        void Refresh()
        {
            if (session is null) { list.ItemsSource = null; UpdateControls(); return; }
            session = runtime.MergeSessions.Load(session.Id);
            detailText.Text = string.Empty;
            preview.Text = string.Empty;
            details.IsExpanded = false;
            list.ItemsSource = runtime.MergeSessions.Conflicts(session, offset, 100).Select(c => new Row(c.Conflict, c.Resolution)).ToArray();
            status.Text = $"{session.Plan.Theirs.Name} → {session.Plan.Ours.Name} · {I18n.GetString("Merge_Mode_" + session.Plan.Mode)}\n"
                + $"{I18n.GetString("Merge_State_" + session.State)} · {I18n.GetString("Merge_Page")} {offset / 100 + 1}\n"
                + $"{I18n.GetString("Merge_Base")}: {session.Plan.BaseCheckpointId?.ToString() ?? "—"}";
            if (session.Diagnostic is not null) ShowDetails(DiagnosticText(session.Diagnostic));
            if (runtime.MaintenanceDiagnostic is not null) status.Text += "\n" + I18n.GetString("Merge_Diagnostic_PostActionWarning");
            UpdateControls();
        }
        async Task Execute(Func<Task> action)
        {
            if (busy) return;
            busy = true; UpdateControls(); sessions.IsEnabled = false;
            try { await action(); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                status.Text = I18n.GetString("Merge_Cancelled");
            }
            catch (Exception ex)
            {
                LogService.LogError($"Merge config={config.Id} session={session?.Id} revision={session?.Revision}", "Merge", ex);
                var diagnostic = ex is HistoryMergeBlockedException blocked ? DiagnosticText(blocked.Diagnostic) : ex.Message;
                try
                {
                    RefreshSessions();
                    if (session is not null) Refresh();
                }
                catch (Exception refreshError)
                {
                    // A damaged Session DB must not throw a second exception out of the UI error handler.
                    LogService.LogError($"Merge config={config.Id} session={session?.Id} stage=refresh-after-failure", "Merge", refreshError);
                    diagnostic += "\n" + refreshError.Message;
                    session = null;
                    list.ItemsSource = null;
                }
                status.Text = I18n.GetString("Merge_Diagnostic_PreparationFailed");
                ShowDetails(diagnostic);
            }
            finally { busy = false; UpdateControls(); sessions.IsEnabled = true; dialog.CloseButtonText = I18n.GetString("Merge_Close"); }
        }
        void Button(bool primary, string key, Func<Task> action)
        {
            var button = new AppBarButton { Label = I18n.GetString(key), Tag = key };
            AutomationProperties.SetAutomationId(button, key);
            AutomationProperties.SetName(button, I18n.GetString(key));
            button.Click += async (_, _) => await Execute(async () => { status.Text = I18n.GetString("Merge_Working") + " " + I18n.GetString(key); await action(); });
            controls.Add(button);
            if (primary) commands.PrimaryCommands.Add(button); else commands.SecondaryCommands.Add(button);
        }
        Button(false, "Merge_New", async () =>
        {
            if (source is null) throw new InvalidOperationException(I18n.GetString("Merge_SelectBranch"));
            session = await NativeHistoryApplicationService.StartMergeAsync(config, source.Value, token);
            offset = 0; RefreshSessions(); Refresh();
            if (session is null) status.Text = I18n.GetString("Merge_NoOp");
        });
        Button(false, "Merge_Recompute", async () =>
        {
            if (session is null) return;
            session = await NativeHistoryApplicationService.RecomputeMergeAsync(config, session, token);
            offset = 0; RefreshSessions(); Refresh();
        });
        Button(false, "Merge_PrepareReplicas", async () =>
        {
            if (session is null) return;
            session = await NativeHistoryApplicationService.PrepareMergeReplicasAsync(config, session, token);
            RefreshSessions(); Refresh();
        });
        Button(false, "Merge_Resume", async () =>
        {
            if (session is null) return;
            await restore.RecoverIncompleteAsync(token);
            session = runtime.MergeSessions.Load(session.Id);
            if (session.State == MergeSessionState.Preparing) session = await service.PrepareAsync(session, token);
            RefreshSessions(); Refresh();
        });
        Button(false, "Merge_Abandon", async () =>
        {
            await using var lease = await runtime.MutationGate.EnterAsync(token);
            if (session is not null) session = runtime.MergeSessions.Update(session, MergeSessionState.Abandoned);
            var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(token)).Value;
            if (catalog is not null) runtime.MergeSessions.CleanupTerminalArtifacts(catalog);
            RefreshSessions(); Refresh();
        });
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            await Execute(async () =>
            {
                if (session is null) return;
                var result = await NativeHistoryApplicationService.ApplyMergeAsync(config, session, token);
                RefreshSessions(); Refresh();
                status.Text = I18n.GetString("Merge_Result_" + result.Status);
                ShowDetails(result.MergeDiagnostic is null ? result.Diagnostic : DiagnosticText(result.MergeDiagnostic) + "\n" + result.Diagnostic);
            });
        };
        foreach (var choice in new[] { MergeResolutionChoice.Ours, MergeResolutionChoice.Theirs })
            Button(true, "Merge_" + choice, () =>
            {
                if (session is null) return Task.CompletedTask;
                session = runtime.MergeSessions.ResolveBatch(session, list.SelectedItems.Cast<Row>().Select(row =>
                    new MergeResolution(session.Plan.Revision, row.Conflict.Id, row.Conflict.InputSignature, choice)).ToArray());
                Refresh(); return Task.CompletedTask;
            });
        Button(false, "Merge_Manual", async () =>
        {
            if (session is null || list.SelectedItems.Count != 1) return;
            var row = (Row)list.SelectedItem;
            var path = await MainWindowService.PickFilePathAsync(I18n.GetString("Merge_Manual"), "merge-manual", ["*"]);
            if (path is not null) session = await service.ImportManualAsync(session, row.Conflict, path, token);
            Refresh();
        });
        Button(false, "Merge_Previous", () => { offset = Math.Max(0, offset - 100); Refresh(); return Task.CompletedTask; });
        Button(false, "Merge_Next", () =>
        {
            if (session is not null && runtime.MergeSessions.Conflicts(session, offset + 100, 1).Count != 0) offset += 100;
            Refresh(); return Task.CompletedTask;
        });
        foreach (var side in new[] { "Base", "Ours", "Theirs" })
            Button(false, "Merge_Preview" + side, async () =>
            {
                if (list.SelectedItems.Count != 1) return;
                details.IsExpanded = true;
                var conflict = ((Row)list.SelectedItem).Conflict;
                var files = side == "Base" ? conflict.Base : side == "Ours" ? conflict.Ours : conflict.Theirs;
                if (files.Count != 1) { preview.Text = string.Join("\n", files.Keys); return; }
                var file = files.Single();
                await using var stream = new FileStream(file.Value.Handle, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] bytes = new byte[Math.Min(stream.Length, 65536)]; int count = await stream.ReadAsync(bytes, token);
                preview.Text = file.Key + $" ({stream.Length} bytes)\n" + (bytes.Take(count).Contains((byte)0)
                    ? Convert.ToHexString(bytes.AsSpan(0, Math.Min(count, 512))) : System.Text.Encoding.UTF8.GetString(bytes, 0, count));
            });
        sessions.SelectionChanged += async (_, _) =>
        {
            if (sessions.SelectedItem is ComboBoxItem { Tag: Guid id })
                await Execute(() => { session = runtime.MergeSessions.Load(id); offset = 0; Refresh(); return Task.CompletedTask; });
        };
        RefreshSessions(); UpdateControls();
        Resize();
        xamlRoot.Changed += RootChanged;
        try { await AppDialogService.Default.ShowCustomAsync(dialog, xamlRoot, token); }
        finally { xamlRoot.Changed -= RootChanged; }
    }
}

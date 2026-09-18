using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        public string Label => $"{(Resolution is null ? "□" : "✓")} {Conflict.Subject.SourceId} · {Conflict.Kind}\n"
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
        var list = new ListView { Height = 260, SelectionMode = ListViewSelectionMode.Multiple, DisplayMemberPath = nameof(Row.Label) };
        var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110 };
        var controls = new List<Button>();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var pages = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var content = new StackPanel { Spacing = 8, Width = 720 };
        content.Children.Add(new TextBlock { Text = I18n.GetString("Merge_Explanation"), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(sessions); content.Children.Add(actions); content.Children.Add(status);
        content.Children.Add(list); content.Children.Add(choices); content.Children.Add(pages); content.Children.Add(preview);
        var dialog = new ContentDialog { Title = I18n.GetString("Merge_Title"), Content = content, CloseButtonText = I18n.GetString("Merge_Close") };
        void RefreshSessions()
        {
            sessions.ItemsSource = runtime.MergeSessions.List().Select(s => new ComboBoxItem
            { Content = $"{s.Plan.Theirs.Name} → {s.Plan.Ours.Name} · {I18n.GetString("Merge_State_" + s.State)} · {s.Id}", Tag = s.Id }).ToArray();
        }
        void Refresh()
        {
            if (session is null) { list.ItemsSource = null; return; }
            session = runtime.MergeSessions.Load(session.Id);
            list.ItemsSource = runtime.MergeSessions.Conflicts(session, offset, 100).Select(c => new Row(c.Conflict, c.Resolution)).ToArray();
            status.Text = $"{session.Plan.Theirs.Name} → {session.Plan.Ours.Name} · {session.Plan.Mode}\n"
                + $"{I18n.GetString("Merge_State_" + session.State)} · {I18n.GetString("Merge_Page")} {offset / 100 + 1}\n"
                + $"{I18n.GetString("Merge_Base")}: {session.Plan.BaseCheckpointId?.ToString() ?? "—"}";
        }
        bool busy = false;
        async Task Execute(Func<Task> action)
        {
            if (busy) return;
            busy = true; controls.ForEach(b => b.IsEnabled = false); sessions.IsEnabled = false;
            dialog.CloseButtonText = "";
            try { await action(); }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { busy = false; controls.ForEach(b => b.IsEnabled = true); sessions.IsEnabled = true; dialog.CloseButtonText = I18n.GetString("Merge_Close"); }
        }
        void Button(StackPanel panel, string key, Func<Task> action)
        {
            var button = new Button { Content = I18n.GetString(key) };
            button.Click += async (_, _) => await Execute(action);
            controls.Add(button); panel.Children.Add(button);
        }
        Button(actions, "Merge_New", async () =>
        {
            if (source is null) throw new InvalidOperationException(I18n.GetString("Merge_SelectBranch"));
            session = await NativeHistoryApplicationService.StartMergeAsync(config, source.Value, token);
            offset = 0; RefreshSessions(); Refresh();
            if (session is null) status.Text = I18n.GetString("Merge_NoOp");
        });
        Button(actions, "Merge_Recompute", async () =>
        {
            if (session is null) return;
            session = await NativeHistoryApplicationService.RecomputeMergeAsync(config, session, token);
            offset = 0; RefreshSessions(); Refresh();
        });
        Button(actions, "Merge_Resume", async () =>
        {
            if (session is null) return;
            await restore.RecoverIncompleteAsync(token);
            session = runtime.MergeSessions.Load(session.Id);
            if (session.State == MergeSessionState.Preparing) session = await service.PrepareAsync(session, token);
            RefreshSessions(); Refresh();
        });
        Button(actions, "Merge_Abandon", async () =>
        {
            await using var lease = await runtime.MutationGate.EnterAsync(token);
            if (session is not null) session = runtime.MergeSessions.Update(session, MergeSessionState.Abandoned);
            var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(token)).Value;
            if (catalog is not null) runtime.MergeSessions.CleanupTerminalArtifacts(catalog);
            RefreshSessions(); Refresh();
        });
        Button(actions, "Merge_Apply", async () =>
        {
            if (session is null) return;
            var result = await NativeHistoryApplicationService.ApplyMergeAsync(config, session, token);
            RefreshSessions(); Refresh();
            status.Text += "\n" + I18n.GetString("Merge_Result_" + result.Status) + "\n" + result.Diagnostic;
        });
        foreach (var choice in new[] { MergeResolutionChoice.Ours, MergeResolutionChoice.Theirs })
            Button(choices, "Merge_" + choice, () =>
            {
                if (session is null) return Task.CompletedTask;
                foreach (var row in list.SelectedItems.Cast<Row>().ToArray())
                    session = runtime.MergeSessions.Resolve(session, new(session.Plan.Revision, row.Conflict.Id, row.Conflict.InputSignature, choice));
                Refresh(); return Task.CompletedTask;
            });
        Button(choices, "Merge_Manual", async () =>
        {
            if (session is null || list.SelectedItems.Count != 1) return;
            var row = (Row)list.SelectedItem;
            var path = await MainWindowService.PickFilePathAsync(I18n.GetString("Merge_Manual"), "merge-manual", ["*"]);
            if (path is not null) session = await service.ImportManualAsync(session, row.Conflict, path, token);
            Refresh();
        });
        Button(pages, "Merge_Previous", () => { offset = Math.Max(0, offset - 100); Refresh(); return Task.CompletedTask; });
        Button(pages, "Merge_Next", () =>
        {
            if (session is not null && runtime.MergeSessions.Conflicts(session, offset + 100, 1).Count != 0) offset += 100;
            Refresh(); return Task.CompletedTask;
        });
        foreach (var side in new[] { "Base", "Ours", "Theirs" })
            Button(pages, "Merge_Preview" + side, async () =>
            {
                if (list.SelectedItems.Count != 1) return;
                var conflict = ((Row)list.SelectedItem).Conflict;
                var files = side == "Base" ? conflict.Base : side == "Ours" ? conflict.Ours : conflict.Theirs;
                if (files.Count != 1) { preview.Text = string.Join("\n", files.Keys); return; }
                var file = files.Single();
                await using var stream = new FileStream(file.Value.Handle, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] bytes = new byte[Math.Min(stream.Length, 65536)]; int count = await stream.ReadAsync(bytes, token);
                preview.Text = file.Key + $" ({stream.Length} bytes)\n" + (bytes.Take(count).Contains((byte)0)
                    ? Convert.ToHexString(bytes.AsSpan(0, Math.Min(count, 512))) : System.Text.Encoding.UTF8.GetString(bytes, 0, count));
            });
        sessions.SelectionChanged += (_, _) =>
        {
            if (sessions.SelectedItem is ComboBoxItem { Tag: Guid id }) { session = runtime.MergeSessions.Load(id); offset = 0; Refresh(); }
        };
        RefreshSessions();
        await AppDialogService.Default.ShowCustomAsync(dialog, MainWindowService.GetXamlRoot(), token);
    }
}

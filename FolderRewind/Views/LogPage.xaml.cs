using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.IO;
using Windows.ApplicationModel.DataTransfer;

namespace FolderRewind.Views
{
    public sealed partial class LogPage : Page
    {
        private readonly ObservableCollection<LogEntry> _allEntries = new();
        public ObservableCollection<LogEntry> FilteredEntries { get; } = new();

        private bool _isLive = true;
        private string _keyword = string.Empty;
        private LogLevel? _filterLevel;

        public LogPage()
        {
            this.InitializeComponent();
            LoadSnapshot();
            LogService.EntryPublished += OnEntryPublished;
            this.Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            LogService.EntryPublished -= OnEntryPublished;
        }

        private void LoadSnapshot()
        {
            foreach (var entry in LogService.GetEntriesSnapshot())
            {
                _allEntries.Add(entry);
            }

            RefreshFiltered();
            ScrollToEnd();
        }

        private void OnEntryPublished(LogEntry entry)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                _allEntries.Add(entry);
                TrimLocalBuffer();

                if (!_isLive) return;
                if (!Passes(entry)) return;

                FilteredEntries.Add(entry);
                ScrollToEnd();
            });
        }

        private void TrimLocalBuffer()
        {
            const int localMax = 5000;
            if (_allEntries.Count <= localMax) return;

            var remove = _allEntries.Count - localMax;
            for (int i = 0; i < remove; i++)
            {
                _allEntries.RemoveAt(0);
            }

            RefreshFiltered();
        }

        private void RefreshFiltered()
        {
            FilteredEntries.Clear();

            foreach (var entry in _allEntries)
            {
                if (Passes(entry))
                {
                    FilteredEntries.Add(entry);
                }
            }

            if (_isLive)
            {
                ScrollToEnd();
            }
        }

        private bool Passes(LogEntry entry)
        {
            if (_filterLevel.HasValue && entry.Level != _filterLevel.Value)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_keyword))
            {
                var text = $"{entry.Message} {entry.Exception} {entry.Source}";
                if (text.IndexOf(_keyword, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }

            return true;
        }

        private void ScrollToEnd()
        {
            if (AutoScrollToggle?.IsOn != true) return;
            if (FilteredEntries.Count == 0) return;

            LogList.ScrollIntoView(FilteredEntries[^1]);
        }

        private void OnLevelChanged(object sender, SelectionChangedEventArgs e)
        {
            var tag = (LevelFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            _filterLevel = tag switch
            {
                "Info" => LogLevel.Info,
                "Warning" => LogLevel.Warning,
                "Error" => LogLevel.Error,
                "Debug" => LogLevel.Debug,
                _ => null
            };

            RefreshFiltered();
        }

        private void OnKeywordChanged(object sender, TextChangedEventArgs e)
        {
            _keyword = SearchBox.Text ?? string.Empty;
            RefreshFiltered();
        }

        private void OnLiveToggled(object sender, RoutedEventArgs e)
        {
            _isLive = LiveToggle.IsOn;
            if (_isLive)
            {
                RefreshFiltered();
            }
        }

        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            LogService.Clear();
            _allEntries.Clear();
            FilteredEntries.Clear();
        }

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            var entries = LogList.SelectedItems.Cast<LogEntry>().ToList();
            if (entries.Count == 0)
            {
                entries = FilteredEntries.ToList();
            }

            if (entries.Count == 0) return;

            var text = string.Join(Environment.NewLine, entries.Select(ToTextLine));
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }

        private static string ToTextLine(LogEntry entry)
        {
            var source = string.IsNullOrWhiteSpace(entry.Source) ? string.Empty : $"[{entry.Source}] ";
            var exception = string.IsNullOrWhiteSpace(entry.Exception) ? string.Empty : $" | {entry.Exception}";
            return $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{entry.Level}] {source}{entry.Message}{exception}";
        }

        private void OnOpenFolderClick(object sender, RoutedEventArgs e)
        {
            LogService.OpenLogFolder();
        }

        private void OnOpenFileClick(object sender, RoutedEventArgs e)
        {
            var path = LogService.GetLogFilePath();
            try
            {
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, string.Empty);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch
            {
            }
        }
    }
}

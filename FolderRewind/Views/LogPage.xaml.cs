using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using Microsoft.UI.Dispatching;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;

namespace FolderRewind.Views
{
    public sealed partial class LogPage : Page
    {
        private readonly LogPresentationController _presentation = new();
        public ObservableCollection<LogEntry> FilteredEntries => _presentation.FilteredEntries;
        private Action<LogEntry>? _entryPublishedHandler;

        // 该页面启用了 NavigationCacheMode=Required。
        // 仅在构造函数里订阅事件 + 在 Unloaded 里退订，会导致：
        // 1) 页面被缓存后再次返回不会重新执行构造函数；
        // 2) 但离开页面时可能触发 Unloaded，从而退订事件；
        // 最终表现为“日志仍在写入，但日志页列表不再更新”。
        // 因此这里改为在 OnNavigatedTo/OnNavigatedFrom 进行订阅管理。
        private bool _isSubscribed;
        private readonly ConcurrentQueue<(LogEntry Entry, long Generation)> _pending = new();
        private DispatcherQueueTimer? _batchTimer;

        private string _keyword = string.Empty;
        private LogLevel? _filterLevel;

        public LogPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            EnsureSubscribed();

            // 每次进入页面都同步一次快照：
            // - 避免离开页面期间产生的日志丢失
            // - 也避免缓存页面导致的“只初始化一次”问题
            ReloadSnapshot();
        }

        protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            Unsubscribe();
        }

        private void EnsureSubscribed()
        {
            if (_isSubscribed) return;
            var generation = _presentation.BeginSession();
            _entryPublishedHandler = entry => OnEntryPublished(entry, generation);
            LogService.EntryPublished += _entryPublishedHandler;
            _isSubscribed = true;
            _batchTimer ??= DispatcherQueue.CreateTimer();
            _batchTimer.Interval = TimeSpan.FromMilliseconds(100);
            _batchTimer.Tick += OnBatchTick;
            _batchTimer.Start();
        }

        private void Unsubscribe()
        {
            if (!_isSubscribed) return;
            LogService.EntryPublished -= _entryPublishedHandler;
            _entryPublishedHandler = null;
            _presentation.EndSession();
            _isSubscribed = false;
            if (_batchTimer is not null) { _batchTimer.Stop(); _batchTimer.Tick -= OnBatchTick; }
            _pending.Clear();
        }

        private void ReloadSnapshot()
        {
            _presentation.LoadSnapshot(LogService.GetEntriesSnapshot());
            ScrollToEnd();
        }

        private void OnEntryPublished(LogEntry entry, long generation) => _pending.Enqueue((entry, generation));

        private void OnBatchTick(DispatcherQueueTimer sender, object args)
        {
            var changed = false;
            for (var count = 0; count < 200 && _pending.TryDequeue(out var item); count++)
                changed |= _presentation.Append(item.Entry, item.Generation);
            if (changed) ScrollToEnd();
        }

        private void RefreshFiltered()
        {
            _presentation.SetFilter(_filterLevel, _keyword);
            if (_presentation.IsLive)
            {
                ScrollToEnd();
            }
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
            _presentation.SetLive(LiveToggle.IsOn);
            if (_presentation.IsLive) ScrollToEnd();
        }

        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            Unsubscribe();
            LogService.Clear();
            EnsureSubscribed();
            ReloadSnapshot();
        }

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            var entries = LogList.SelectedItems.Cast<LogEntry>().ToList();
            if (entries.Count == 0)
            {
                entries = FilteredEntries.ToList();
            }

            if (entries.Count == 0) return;

            var text = string.Join(Environment.NewLine, entries.Select(LogService.FormatEntry));
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }


        private void OnOpenFolderClick(object sender, RoutedEventArgs e)
        {
            LogService.OpenLogFolder();
        }

        private async void OnOpenFileClick(object sender, RoutedEventArgs e)
        {
            var path = LogService.GetLogFilePath();
            try
            {
                await LogService.FlushAsync();
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

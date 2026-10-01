using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

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
        }

        private void Unsubscribe()
        {
            if (!_isSubscribed) return;
            LogService.EntryPublished -= _entryPublishedHandler;
            _entryPublishedHandler = null;
            _presentation.EndSession();
            _isSubscribed = false;
        }

        private void ReloadSnapshot()
        {
            _presentation.LoadSnapshot(LogService.GetEntriesSnapshot());
            ScrollToEnd();
        }

        private void OnEntryPublished(LogEntry entry, long generation)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (_presentation.Append(entry, generation)) ScrollToEnd();
            });
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

    internal class LogLevelToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush InfoBrush = new(Color.FromArgb(255, 37, 99, 235));
        private static readonly SolidColorBrush WarningBrush = new(Color.FromArgb(255, 180, 83, 9));
        private static readonly SolidColorBrush ErrorBrush = new(Color.FromArgb(255, 185, 28, 28));
        private static readonly SolidColorBrush DebugBrush = new(Color.FromArgb(255, 71, 85, 105));
        private static readonly SolidColorBrush NeutralBrush = new(Color.FromArgb(255, 75, 85, 99));

        private static readonly SolidColorBrush InfoBackground = CreateTint(InfoBrush);
        private static readonly SolidColorBrush WarningBackground = CreateTint(WarningBrush);
        private static readonly SolidColorBrush ErrorBackground = CreateTint(ErrorBrush);
        private static readonly SolidColorBrush DebugBackground = CreateTint(DebugBrush);
        private static readonly SolidColorBrush NeutralBackground = CreateTint(NeutralBrush);

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var mode = parameter as string;

            if (string.Equals(mode, "background", StringComparison.OrdinalIgnoreCase))
            {
                return value is LogLevel levelBg ? GetBackgroundBrush(levelBg) : NeutralBackground;
            }

            return value is LogLevel level ? GetAccentBrush(level) : NeutralBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }

        private static Brush GetAccentBrush(LogLevel level)
        {
            return level switch
            {
                LogLevel.Info => InfoBrush,
                LogLevel.Warning => WarningBrush,
                LogLevel.Error => ErrorBrush,
                LogLevel.Debug => DebugBrush,
                _ => NeutralBrush
            };
        }

        private static Brush GetBackgroundBrush(LogLevel level)
        {
            return level switch
            {
                LogLevel.Info => InfoBackground,
                LogLevel.Warning => WarningBackground,
                LogLevel.Error => ErrorBackground,
                LogLevel.Debug => DebugBackground,
                _ => NeutralBackground
            };
        }

        private static SolidColorBrush CreateTint(SolidColorBrush source)
        {
            var c = source.Color;
            return new SolidColorBrush(Color.FromArgb(28, c.R, c.G, c.B));
        }
    }
}

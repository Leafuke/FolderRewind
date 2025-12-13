using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Text; // ���� RichEditBox ����
using System;

namespace FolderRewind.Views
{
    public sealed partial class LogWindow : Window
    {
        public LogWindow()
        {
            this.InitializeComponent();
            // 同步主题到子窗口
            ThemeService.ApplyThemeToWindow(this);
            ThemeService.ThemeChanged += OnThemeChanged;

            // 先加载历史日志快照（打开窗口后不再是空白）
            LoadSnapshot();

            // 实时接收日志
            LogService.LogReceived += OnLogReceived;

            // ���ڹر�ʱȡ�����ģ���ֹ�ڴ�й©
            this.Closed += (s, e) =>
            {
                LogService.LogReceived -= OnLogReceived;
                ThemeService.ThemeChanged -= OnThemeChanged;
            };
        }

        private void OnThemeChanged(ElementTheme theme)
        {
            ThemeService.ApplyThemeToWindow(this);
        }

        private void LoadSnapshot()
        {
            var lines = LogService.GetSnapshot();
            if (lines.Count == 0) return;

            var text = string.Join("\r", lines);
            LogBox.Document.SetText(TextSetOptions.None, text);
            if (AutoScrollCheck.IsChecked == true)
            {
                LogBox.Document.Selection.SetRange(int.MaxValue, int.MaxValue);
                LogBox.Document.Selection.ScrollIntoView(PointOptions.None);
            }
        }

        private void OnLogReceived(string message)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                LogBox.Document.GetText(TextGetOptions.None, out string currentText);

                string timePrefix = $"[{DateTime.Now:HH:mm:ss}] ";

                LogBox.Document.Selection.SetRange(int.MaxValue, int.MaxValue);
                LogBox.Document.Selection.Text = timePrefix + message + "\r";

                if (AutoScrollCheck.IsChecked == true)
                {
                    LogBox.Document.Selection.ScrollIntoView(PointOptions.None);
                }
            });
        }

        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            LogBox.Document.SetText(TextSetOptions.None, "");
            LogService.Clear();
        }
    }
}
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderRewind.Views
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsPage()
        {
            this.InitializeComponent();
        }

        // 示例：处理主题切换
        private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is RadioButtons rb && rb.SelectedItem is RadioButton item)
            {
                string tag = item.Content.ToString();
                // 实际逻辑：设置 ElementTheme
                // (Application.Current as App).m_window.Content.RequestedTheme = ...
            }
        }
    }
}
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FolderRewind.Services.Plugins
{
    public sealed class PluginHotkeyDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Description { get; set; }

        /// <summary>默认手势字符串，如 "Alt+Ctrl+S"。为空表示默认不绑定。</summary>
        public string? DefaultGesture { get; set; }

        /// <summary>true=全局热键（RegisterHotKey）；false=应用内快捷键（KeyboardAccelerator）。</summary>
        public bool IsGlobalHotkey { get; set; }
    }

    public interface IFolderRewindHotkeyProvider
    {
        IReadOnlyList<PluginHotkeyDefinition> GetHotkeyDefinitions();

        Task OnHotkeyInvokedAsync(
            string hotkeyId,
            bool isGlobalHotkey,
            IReadOnlyDictionary<string, string> settingsValues,
            PluginHostContext hostContext);
    }
}

using System.Collections.Generic;

namespace FolderRewind.Models
{
    /// <summary>
    /// 用户自定义快捷键/热键绑定。
    /// Key: HotkeyDefinition.Id -> GestureString (例如 "Ctrl+S")；空字符串表示禁用。
    /// </summary>
    public class HotkeySettings : ObservableObject
    {
        private Dictionary<string, string> _bindings = new();

        public Dictionary<string, string> Bindings
        {
            get => _bindings;
            set => _bindings = value ?? new Dictionary<string, string>();
        }
    }
}

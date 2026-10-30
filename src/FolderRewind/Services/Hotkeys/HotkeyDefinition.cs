namespace FolderRewind.Services.Hotkeys
{
    public enum HotkeyScope
    {
        Shortcut = 0,
        GlobalHotkey = 1,
    }

    public sealed class HotkeyDefinition
    {
        public string Id { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string? Description { get; init; }
        public string DefaultGesture { get; init; } = string.Empty;
        public HotkeyScope Scope { get; init; } = HotkeyScope.Shortcut;

        public string? OwnerPluginId { get; init; }
        public string? OwnerPluginName { get; init; }
    }
}

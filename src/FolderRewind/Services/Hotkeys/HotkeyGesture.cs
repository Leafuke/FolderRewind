using System;
using Windows.System;

namespace FolderRewind.Services.Hotkeys
{
    [Flags]
    public enum HotkeyModifiers
    {
        None = 0,
        Ctrl = 1,
        Alt = 2,
        Shift = 4,
        Win = 8,
    }

    public readonly struct HotkeyGesture : IEquatable<HotkeyGesture>
    {
        public HotkeyModifiers Modifiers { get; }
        public VirtualKey Key { get; }

        public HotkeyGesture(HotkeyModifiers modifiers, VirtualKey key)
        {
            Modifiers = modifiers;
            Key = key;
        }

        public bool Equals(HotkeyGesture other) => Modifiers == other.Modifiers && Key == other.Key;
        public override bool Equals(object? obj) => obj is HotkeyGesture other && Equals(other);
        public override int GetHashCode() => HashCode.Combine((int)Modifiers, (int)Key);

        public static bool operator ==(HotkeyGesture left, HotkeyGesture right) => left.Equals(right);
        public static bool operator !=(HotkeyGesture left, HotkeyGesture right) => !left.Equals(right);

        public VirtualKeyModifiers ToVirtualKeyModifiers()
        {
            VirtualKeyModifiers mods = VirtualKeyModifiers.None;
            if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) mods |= VirtualKeyModifiers.Control;
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) mods |= VirtualKeyModifiers.Menu;
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) mods |= VirtualKeyModifiers.Shift;
            if (Modifiers.HasFlag(HotkeyModifiers.Win)) mods |= VirtualKeyModifiers.Windows;
            return mods;
        }

        public override string ToString() => HotkeyParser.Format(this);

        public static bool TryParse(string? text, out HotkeyGesture gesture) => HotkeyParser.TryParse(text, out gesture);
    }
}

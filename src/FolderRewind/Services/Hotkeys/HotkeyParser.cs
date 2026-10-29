using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Windows.System;

namespace FolderRewind.Services.Hotkeys
{
    public static class HotkeyParser
    {
        private static readonly Dictionary<string, VirtualKey> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Enter"] = VirtualKey.Enter,
            ["Return"] = VirtualKey.Enter,
            ["Esc"] = VirtualKey.Escape,
            ["Escape"] = VirtualKey.Escape,
            ["Space"] = VirtualKey.Space,
            ["Tab"] = VirtualKey.Tab,
            ["Backspace"] = VirtualKey.Back,
            ["Del"] = VirtualKey.Delete,
            ["Delete"] = VirtualKey.Delete,
            ["Ins"] = VirtualKey.Insert,
            ["Insert"] = VirtualKey.Insert,
            ["Home"] = VirtualKey.Home,
            ["End"] = VirtualKey.End,
            ["PageUp"] = VirtualKey.PageUp,
            ["PgUp"] = VirtualKey.PageUp,
            ["PageDown"] = VirtualKey.PageDown,
            ["PgDn"] = VirtualKey.PageDown,
            ["Up"] = VirtualKey.Up,
            ["Down"] = VirtualKey.Down,
            ["Left"] = VirtualKey.Left,
            ["Right"] = VirtualKey.Right,
        };

        public static bool TryParse(string? text, out HotkeyGesture gesture)
        {
            gesture = default;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) return false;

            HotkeyModifiers mods = HotkeyModifiers.None;
            string? keyToken = null;

            foreach (var raw in parts)
            {
                var token = raw.Trim();
                if (token.Length == 0) continue;

                if (IsModifier(token, out var m))
                {
                    mods |= m;
                    continue;
                }

                keyToken = token;
            }

            if (string.IsNullOrWhiteSpace(keyToken)) return false;

            if (!TryParseKey(keyToken!, out var key)) return false;

            gesture = new HotkeyGesture(mods, key);
            return true;
        }

        private static bool IsModifier(string token, out HotkeyModifiers modifier)
        {
            modifier = HotkeyModifiers.None;
            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifier = HotkeyModifiers.Ctrl;
                    return true;
                case "alt":
                case "menu":
                    modifier = HotkeyModifiers.Alt;
                    return true;
                case "shift":
                    modifier = HotkeyModifiers.Shift;
                    return true;
                case "win":
                case "windows":
                    modifier = HotkeyModifiers.Win;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryParseKey(string token, out VirtualKey key)
        {
            key = VirtualKey.None;

            if (NamedKeys.TryGetValue(token, out key)) return true;

            // F1..F24
            if (token.Length >= 2 && (token[0] == 'F' || token[0] == 'f') && int.TryParse(token[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var f))
            {
                if (f >= 1 && f <= 24)
                {
                    key = (VirtualKey)((int)VirtualKey.F1 + (f - 1));
                    return true;
                }
            }

            // 0..9
            if (token.Length == 1 && char.IsDigit(token[0]))
            {
                key = token[0] switch
                {
                    '0' => VirtualKey.Number0,
                    '1' => VirtualKey.Number1,
                    '2' => VirtualKey.Number2,
                    '3' => VirtualKey.Number3,
                    '4' => VirtualKey.Number4,
                    '5' => VirtualKey.Number5,
                    '6' => VirtualKey.Number6,
                    '7' => VirtualKey.Number7,
                    '8' => VirtualKey.Number8,
                    '9' => VirtualKey.Number9,
                    _ => VirtualKey.None
                };
                return key != VirtualKey.None;
            }

            // A..Z
            if (token.Length == 1 && token[0] >= 'A' && token[0] <= 'Z')
            {
                key = (VirtualKey)token[0];
                return true;
            }
            if (token.Length == 1 && token[0] >= 'a' && token[0] <= 'z')
            {
                key = (VirtualKey)char.ToUpperInvariant(token[0]);
                return true;
            }

            if (Enum.TryParse<VirtualKey>(token, ignoreCase: true, out var parsed) && parsed != VirtualKey.None)
            {
                key = parsed;
                return true;
            }

            return false;
        }

        public static string Format(HotkeyGesture gesture)
        {
            var parts = new List<string>();
            if (gesture.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
            if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            if (gesture.Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");

            parts.Add(FormatKey(gesture.Key));
            return string.Join("+", parts);
        }

        private static string FormatKey(VirtualKey key)
        {
            if (key >= VirtualKey.F1 && key <= VirtualKey.F24)
            {
                var n = (int)key - (int)VirtualKey.F1 + 1;
                return "F" + n;
            }

            if (key >= VirtualKey.A && key <= VirtualKey.Z)
            {
                return ((char)key).ToString();
            }

            if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9)
            {
                var n = (int)key - (int)VirtualKey.Number0;
                return n.ToString(CultureInfo.InvariantCulture);
            }

            var kv = NamedKeys.FirstOrDefault(k => k.Value == key);
            if (!string.IsNullOrWhiteSpace(kv.Key)) return kv.Key;

            return key.ToString();
        }
    }
}

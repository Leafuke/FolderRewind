using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace FolderRewind.Services.Hotkeys
{
    internal sealed class NativeHotkeyService : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const int GWL_WNDPROC = -4;

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        private delegate IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private readonly IntPtr _hwnd;
        private readonly WndProc _newWndProc;
        private IntPtr _oldWndProc;
        private bool _hooked;

        private int _nextId = 0x2000;
        private readonly Dictionary<int, Func<bool>> _callbacks = new();
        private readonly Dictionary<string, int> _idByHotkeyId = new(StringComparer.OrdinalIgnoreCase);

        public NativeHotkeyService(Window window)
        {
            _hwnd = WindowNative.GetWindowHandle(window);
            _newWndProc = WindowProc;
        }

        public void Hook()
        {
            if (_hooked) return;
            try
            {
                _oldWndProc = GetWindowLongPtr(_hwnd, GWL_WNDPROC);
                var newPtr = Marshal.GetFunctionPointerForDelegate(_newWndProc);
                SetWindowLongPtr(_hwnd, GWL_WNDPROC, newPtr);
                _hooked = true;
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("Hotkeys_GlobalHookFailed", ex.Message), nameof(NativeHotkeyService), ex);
            }
        }

        public void ClearAll()
        {
            foreach (var kv in _callbacks)
            {
                try { UnregisterHotKey(_hwnd, kv.Key); } catch { }
            }
            _callbacks.Clear();
            _idByHotkeyId.Clear();
        }

        public bool RegisterOrUpdate(string hotkeyId, HotkeyGesture gesture, Func<bool> callback)
        {
            if (string.IsNullOrWhiteSpace(hotkeyId)) return false;

            // If already registered, remove first
            if (_idByHotkeyId.TryGetValue(hotkeyId, out var existing))
            {
                try { UnregisterHotKey(_hwnd, existing); } catch { }
                _callbacks.Remove(existing);
                _idByHotkeyId.Remove(hotkeyId);
            }

            var id = _nextId++;
            uint mods = ToNativeModifiers(gesture.Modifiers) | MOD_NOREPEAT;
            uint vk = (uint)gesture.Key;

            bool ok = RegisterHotKey(_hwnd, id, mods, vk);
            if (!ok)
            {
                var err = Marshal.GetLastWin32Error();
                LogService.Log(I18n.Format("Hotkeys_RegisterGlobalFailed", hotkeyId, gesture.ToString(), err));
                return false;
            }

            _callbacks[id] = callback;
            _idByHotkeyId[hotkeyId] = id;
            return true;
        }

        private static uint ToNativeModifiers(HotkeyModifiers modifiers)
        {
            uint m = 0;
            if (modifiers.HasFlag(HotkeyModifiers.Alt)) m |= MOD_ALT;
            if (modifiers.HasFlag(HotkeyModifiers.Ctrl)) m |= MOD_CONTROL;
            if (modifiers.HasFlag(HotkeyModifiers.Shift)) m |= MOD_SHIFT;
            if (modifiers.HasFlag(HotkeyModifiers.Win)) m |= MOD_WIN;
            return m;
        }

        private IntPtr WindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (msg == WM_HOTKEY)
                {
                    int id = wParam.ToInt32();
                    if (_callbacks.TryGetValue(id, out var cb))
                    {
                        try
                        {
                            // 返回true表示已处理该热键了，直接忽略
                            if (cb()) return IntPtr.Zero;
                        }
                        catch (Exception ex)
                        {
                            LogService.LogError(I18n.Format("Hotkeys_InvokeFailed", ex.Message), nameof(NativeHotkeyService), ex);
                        }
                    }
                }
            }
            catch
            {
            }

            return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            try
            {
                ClearAll();
            }
            catch
            {
            }

            try
            {
                if (_hooked && _oldWndProc != IntPtr.Zero)
                {
                    SetWindowLongPtr(_hwnd, GWL_WNDPROC, _oldWndProc);
                }
            }
            catch
            {
            }
        }
    }
}

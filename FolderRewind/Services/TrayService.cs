using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace FolderRewind.Services
{
    // 纯 Win32 托盘实现（但貌似问题很大……
    public static class TrayService
    {
        private const uint TRAY_ICON_ID = 1;

        private static bool _initialized;
        private static IntPtr _mainHwnd;
        private static IntPtr _messageHwnd;
        private static WndProc? _wndProc;

        private static readonly uint WM_TRAY = WM_APP + 1;

        private const int CMD_SHOW = 1001;
        private const int CMD_EXIT = 1002;

        public static Action? ExitRequested { get; set; }

        public static bool IsInitialized => _initialized;

        public static void EnsureInitialized(Window window)
        {
            if (_initialized) return;
            if (window == null) return;

            _mainHwnd = WindowNative.GetWindowHandle(window);

            // 创建一个隐藏消息窗口接收托盘回调
            _wndProc = WndProcImpl;
            _messageHwnd = CreateMessageWindow(_wndProc);
            if (_messageHwnd == IntPtr.Zero)
            {
                return;
            }

            var data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _messageHwnd,
                uID = TRAY_ICON_ID,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAY,
                hIcon = LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION),
                szTip = "FolderRewind"
            };

            Shell_NotifyIcon(NIM_ADD, ref data);

            // 设为新版本行为（更稳定的鼠标事件）
            data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, ref data);

            _initialized = true;
        }

        public static void HideToTray(Window window)
        {
            EnsureInitialized(window);
            RunOnUI(() =>
            {
                try
                {
                    ShowWindow(_mainHwnd, SW_HIDE);
                }
                catch
                {
                }
            });
        }

        public static void Show()
        {
            RunOnUI(() =>
            {
                try
                {
                    ShowWindow(_mainHwnd, SW_SHOW);
                    SetForegroundWindow(_mainHwnd);
                    App._window?.Activate();
                }
                catch
                {
                }
            });
        }

        public static void Dispose()
        {
            try
            {
                if (_messageHwnd != IntPtr.Zero)
                {
                    var data = new NOTIFYICONDATA
                    {
                        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                        hWnd = _messageHwnd,
                        uID = TRAY_ICON_ID
                    };
                    Shell_NotifyIcon(NIM_DELETE, ref data);

                    DestroyWindow(_messageHwnd);
                }
            }
            catch
            {
            }

            _initialized = false;
            _mainHwnd = IntPtr.Zero;
            _messageHwnd = IntPtr.Zero;
            _wndProc = null;
        }

        private static void RunOnUI(Action action)
        {
            try
            {
                DispatcherQueue? queue = App._window?.DispatcherQueue;
                if (queue == null || queue.HasThreadAccess)
                {
                    action();
                    return;
                }

                queue.TryEnqueue(() => action());
            }
            catch
            {
                try { action(); } catch { }
            }
        }

        private static IntPtr CreateMessageWindow(WndProc wndProc)
        {
            var hInstance = GetModuleHandle(null);
            string className = "FolderRewind_TrayMessageWindow";

            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = wndProc,
                hInstance = hInstance,
                lpszClassName = className
            };

            ushort atom = RegisterClassEx(ref wc);
            if (atom == 0)
            {
                // 可能已注册，继续尝试创建
            }

            return CreateWindowEx(
                0,
                className,
                string.Empty,
                0,
                0,
                0,
                0,
                0,
                HWND_MESSAGE,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero);
        }

        private static IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (msg == WM_TRAY)
                {
                    // lParam: 鼠标消息 (WM_LBUTTONDBLCLK / WM_RBUTTONUP ...)
                    int mouseMsg = lParam.ToInt32();
                    if (mouseMsg == WM_LBUTTONDBLCLK)
                    {
                        Show();
                        return IntPtr.Zero;
                    }

                    if (mouseMsg == WM_RBUTTONUP)
                    {
                        ShowContextMenu(hWnd);
                        return IntPtr.Zero;
                    }
                }
                else if (msg == WM_COMMAND)
                {
                    int cmd = LOWORD(wParam);
                    if (cmd == CMD_SHOW)
                    {
                        Show();
                        return IntPtr.Zero;
                    }
                    if (cmd == CMD_EXIT)
                    {
                        RunOnUI(() => ExitRequested?.Invoke());
                        return IntPtr.Zero;
                    }
                }
            }
            catch
            {
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private static void ShowContextMenu(IntPtr hWnd)
        {
            IntPtr menu = IntPtr.Zero;
            try
            {
                menu = CreatePopupMenu();
                AppendMenu(menu, MF_STRING, CMD_SHOW, I18n.GetString("Tray_Show"));
                AppendMenu(menu, MF_SEPARATOR, 0, null);
                AppendMenu(menu, MF_STRING, CMD_EXIT, I18n.GetString("Tray_Exit"));

                GetCursorPos(out var pt);

                // 让菜单在点击外部时能正确消失
                SetForegroundWindow(hWnd);

                TrackPopupMenu(menu, TPM_RIGHTBUTTON, pt.X, pt.Y, 0, hWnd, IntPtr.Zero);
            }
            catch
            {
            }
            finally
            {
                if (menu != IntPtr.Zero)
                {
                    try { DestroyMenu(menu); } catch { }
                }
            }
        }

        private static int LOWORD(IntPtr value) => value.ToInt32() & 0xFFFF;

        private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_DELETE = 0x00000002;
        private const uint NIM_SETVERSION = 0x00000004;

        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;

        private const uint NOTIFYICON_VERSION_4 = 4;

        private const uint WM_APP = 0x8000;
        private const uint WM_COMMAND = 0x0111;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        private const uint MF_STRING = 0x0000;
        private const uint MF_SEPARATOR = 0x0800;
        private const uint TPM_RIGHTBUTTON = 0x0002;

        private const int IDI_APPLICATION = 32512;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpdata);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx([In] ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, int uIDNewItem, string? lpNewItem);

        [DllImport("user32.dll")]
        private static extern bool TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);
    }
}

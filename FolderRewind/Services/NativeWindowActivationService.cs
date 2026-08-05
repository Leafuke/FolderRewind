using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace FolderRewind.Services
{
    /// <summary>
    /// 仅用于用户主动请求恢复窗口时的 Win32 前台激活兜底。
    /// </summary>
    internal static class NativeWindowActivationService
    {
        private const int SwRestore = 9;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetActiveWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr hWnd,
            out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(
            uint idAttach,
            uint idAttachTo,
            [MarshalAs(UnmanagedType.Bool)] bool attach);

        public static bool TryActivate(Window window)
        {
            ArgumentNullException.ThrowIfNull(window);

            try
            {
                var hwnd = WindowNative.GetWindowHandle(window);
                return TryActivate(hwnd);
            }
            catch (Exception ex)
            {
                LogService.LogWarning(
                    I18n.Format("Tray_ToggleFailed", ex.Message),
                    nameof(NativeWindowActivationService));
                return false;
            }
        }

        private static bool TryActivate(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            // AppWindow.Show(true) 已经负责 WinUI 层显示；这些调用补足原生窗口
            // 的可见性、Z 序和前台激活请求，且不会设置永久置顶。
            ShowWindow(hwnd, SwRestore);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SetActiveWindow(hwnd);

            if (GetForegroundWindow() == hwnd)
            {
                return true;
            }

            // 托盘回调结束后 Explorer/其他窗口可能仍拥有前台输入队列。
            // 临时附加线程只用于重试激活，完成后立即解除，避免改变长期输入状态。
            var foregroundHwnd = GetForegroundWindow();
            var currentThreadId = GetCurrentThreadId();
            var foregroundThreadId = foregroundHwnd == IntPtr.Zero
                ? 0
                : GetWindowThreadProcessId(foregroundHwnd, out _);
            var attached = false;

            try
            {
                if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
                {
                    attached = AttachThreadInput(
                        currentThreadId,
                        foregroundThreadId,
                        attach: true);
                }

                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
                SetActiveWindow(hwnd);
                return GetForegroundWindow() == hwnd;
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(
                        currentThreadId,
                        foregroundThreadId,
                        attach: false);
                }
            }
        }
    }
}

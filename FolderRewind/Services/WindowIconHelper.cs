using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Graphics.Imaging;
using Windows.Storage;
using WinRT.Interop;

namespace FolderRewind.Services
{
    public static class WindowIconHelper
    {
        private const int WM_SETICON = 0x0080;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;

        private static bool _applied;
        private static IntPtr _hIconSmall = IntPtr.Zero;
        private static IntPtr _hIconBig = IntPtr.Zero;

        public static async Task TryApplyAsync(Window window)
        {
            if (_applied) return;
            _applied = true;

            try
            {
                var hwnd = WindowNative.GetWindowHandle(window);
                if (hwnd == IntPtr.Zero) return;

                var installedPath = Package.Current.InstalledLocation.Path;
                var assetPath = Path.Combine(installedPath, "Assets", "Square44x44Logo.scale-200.png");
                if (!File.Exists(assetPath))
                {
                    assetPath = Path.Combine(installedPath, "Assets", "icon.png");
                }

                if (!File.Exists(assetPath)) return;

                _hIconSmall = await LoadHiconFromPngAsync(assetPath, 32);
                _hIconBig = await LoadHiconFromPngAsync(assetPath, 256);

                if (_hIconSmall != IntPtr.Zero)
                {
                    SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, _hIconSmall);
                }

                if (_hIconBig != IntPtr.Zero)
                {
                    SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, _hIconBig);
                }

                window.Closed += (_, __) => Cleanup();
            }
            catch
            {
                // ignore
            }
        }

        private static void Cleanup()
        {
            if (_hIconSmall != IntPtr.Zero)
            {
                DestroyIcon(_hIconSmall);
                _hIconSmall = IntPtr.Zero;
            }

            if (_hIconBig != IntPtr.Zero)
            {
                DestroyIcon(_hIconBig);
                _hIconBig = IntPtr.Zero;
            }
        }

        private static async Task<IntPtr> LoadHiconFromPngAsync(string filePath, int size)
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            using var stream = await file.OpenReadAsync();

            var decoder = await BitmapDecoder.CreateAsync(stream);
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)size,
                ScaledHeight = (uint)size,
                InterpolationMode = BitmapInterpolationMode.Fant
            };

            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            var pixels = pixelData.DetachPixelData();
            return CreateHiconFromBgra32(pixels, size, size);
        }

        private static IntPtr CreateHiconFromBgra32(byte[] bgra, int width, int height)
        {
            if (bgra == null || bgra.Length == 0) return IntPtr.Zero;

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                    biSizeImage = (uint)(width * height * 4)
                },
                bmiColors = new uint[3]
            };

            IntPtr bits;
            var hdc = GetDC(IntPtr.Zero);
            try
            {
                var hbmColor = CreateDIBSection(hdc, ref bmi, DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
                if (hbmColor == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;

                try
                {
                    Marshal.Copy(bgra, 0, bits, bgra.Length);

                    // 1-bit mask bitmap (all 0 => fully visible)
                    var hbmMask = CreateBitmap(width, height, 1, 1, IntPtr.Zero);

                    var iconInfo = new ICONINFO
                    {
                        fIcon = true,
                        xHotspot = 0,
                        yHotspot = 0,
                        hbmMask = hbmMask,
                        hbmColor = hbmColor
                    };

                    var hIcon = CreateIconIndirect(ref iconInfo);

                    if (hbmMask != IntPtr.Zero)
                    {
                        DeleteObject(hbmMask);
                    }

                    return hIcon;
                }
                finally
                {
                    DeleteObject(hbmColor);
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
            }
        }

        private const uint DIB_RGB_COLORS = 0;
        private const uint BI_RGB = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public uint[] bmiColors;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool fIcon;
            public uint xHotspot;
            public uint yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, [In] ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsPerPel, IntPtr lpvBits);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateIconIndirect([In] ref ICONINFO piconinfo);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    }
}

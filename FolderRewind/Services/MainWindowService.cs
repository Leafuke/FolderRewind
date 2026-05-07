using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.Windows.Storage.Pickers;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Graphics;
using WinRT.Interop;

namespace FolderRewind.Services
{
    public static class MainWindowService
    {
        public enum SuggestedPickerLocation
        {
            Unspecified,
            ComputerFolder,
            DocumentsLibrary,
            Downloads,
            PicturesLibrary,
            VideosLibrary,
            MusicLibrary
        }

        // 由 App.OnLaunched 注入主窗口，供非视图层按需访问窗口能力。
        private static Window? _window;
        private static Views.SponsorWindow? _sponsorWindow;

        public static void Initialize(Window? window)
        {
            if (window != null)
            {
                // 启动后可能因窗口重建再次注入，这里允许覆盖旧引用。
                _window = window;
            }
        }

        private static Window? GetMainWindow()
        {
            return _window;
        }

        public static void UpdateWindowTitle()
        {
            App.UpdateWindowTitle();
        }

        public static void ApplySponsorVisuals()
        {
            UiDispatcherService.Enqueue(() =>
            {
                var window = GetMainWindow();
                if (window == null)
                {
                    return;
                }

                ThemeService.ApplyPersonalizationToWindow(window);
                UpdateWindowTitle();
            });
        }

        public static IntPtr GetWindowHandle()
        {
            var window = GetMainWindow();
            return window == null ? IntPtr.Zero : WindowNative.GetWindowHandle(window);
        }

        public static void InitializeStoreContext(object? storeContext)
        {
            if (storeContext == null)
            {
                return;
            }

            var hwnd = GetWindowHandle();
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            try
            {
                // Store 购买/恢复会弹系统 UI，桌面 WinUI 需要绑定主窗口句柄才能稳定置前。
                InitializeWithWindow.Initialize(storeContext, hwnd);
            }
            catch (Exception ex)
            {
                LogService.LogWarning(I18n.Format("Sponsor_Log_StoreContextWindowInitFailed", ex.Message), nameof(MainWindowService));
            }
        }

        public static void OpenSponsorWindow()
        {
            UiDispatcherService.Enqueue(() =>
            {
                try
                {
                    if (_sponsorWindow != null)
                    {
                        _sponsorWindow.Activate();
                        return;
                    }

                    var sponsorWindow = new Views.SponsorWindow();
                    _sponsorWindow = sponsorWindow;
                    sponsorWindow.Closed += (_, _) => _sponsorWindow = null;

                    ConfigureSponsorWindow(sponsorWindow);
                    sponsorWindow.Activate();
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("Sponsor_Log_OpenWindowFailed", ex.Message), nameof(MainWindowService), ex);
                    NotificationService.ShowError(I18n.Format("Sponsor_OpenWindowFailed", ex.Message), I18n.GetString("Sponsor_Title"));
                }
            });
        }

        public static void CloseSponsorWindow()
        {
            UiDispatcherService.Enqueue(() =>
            {
                try
                {
                    _sponsorWindow?.Close();
                }
                catch
                {
                }
                finally
                {
                    _sponsorWindow = null;
                }
            });
        }

        public static XamlRoot? GetXamlRoot()
        {
            return GetMainWindow()?.Content?.XamlRoot;
        }

        public static void ApplyCurrentTheme()
        {
            ThemeService.ApplyThemeToWindow(GetMainWindow());
            ThemeService.ApplyPersonalizationToWindow(GetMainWindow());
        }

        public static void Resize(double width, double height)
        {
            var window = GetMainWindow();
            if (window == null)
            {
                return;
            }

            try
            {
                window.AppWindow?.Resize(new SizeInt32((int)Math.Round(width), (int)Math.Round(height)));
            }
            catch
            {
            }
        }

        public static bool IsMainWindowVisible()
        {
            try
            {
                var window = GetMainWindow();
                if (window?.AppWindow != null)
                {
                    return window.AppWindow.IsVisible;
                }

                return true;
            }
            catch
            {
                return true;
            }
        }

        public static void InitializePicker(object picker)
        {
            if (picker == null)
            {
                return;
            }

            var window = GetMainWindow();
            if (window == null)
            {
                return;
            }

            // WinUI 桌面应用中的 Picker 需要显式绑定窗口句柄，否则无法正常弹出。
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        }

        public static Task<string?> PickFolderPathAsync(
            string title,
            string settingsIdentifier,
            SuggestedPickerLocation suggestedStartLocation = SuggestedPickerLocation.ComputerFolder,
            string? suggestedStartFolder = null)
        {
            return RunModernPickerAsync(async windowId =>
            {
                var picker = new FolderPicker(windowId)
                {
                    Title = title ?? string.Empty,
                    SettingsIdentifier = settingsIdentifier ?? string.Empty,
                    SuggestedStartLocation = MapPickerLocation(suggestedStartLocation)
                };

                if (!string.IsNullOrWhiteSpace(suggestedStartFolder))
                {
                    picker.SuggestedStartFolder = suggestedStartFolder;
                }

                // Windows App SDK 2.0 的现代 Picker 直接返回路径结果，调用方不再需要依赖 StorageFolder。
                var result = await picker.PickSingleFolderAsync();
                return result?.Path;
            });
        }

        public static Task<string?> PickFilePathAsync(
            string title,
            string settingsIdentifier,
            IReadOnlyList<string> fileTypeFilters,
            SuggestedPickerLocation suggestedStartLocation = SuggestedPickerLocation.DocumentsLibrary,
            string? suggestedStartFolder = null,
            PickerViewMode viewMode = PickerViewMode.List)
        {
            return RunModernPickerAsync(async windowId =>
            {
                var picker = new FileOpenPicker(windowId)
                {
                    Title = title ?? string.Empty,
                    SettingsIdentifier = settingsIdentifier ?? string.Empty,
                    SuggestedStartLocation = MapPickerLocation(suggestedStartLocation),
                    ViewMode = viewMode
                };

                if (!string.IsNullOrWhiteSpace(suggestedStartFolder))
                {
                    picker.SuggestedStartFolder = suggestedStartFolder;
                }

                foreach (var filter in fileTypeFilters)
                {
                    if (!string.IsNullOrWhiteSpace(filter))
                    {
                        picker.FileTypeFilter.Add(filter);
                    }
                }

                var result = await picker.PickSingleFileAsync();
                return result?.Path;
            });
        }

        public static Task<string?> PickSaveFilePathAsync(
            string title,
            string settingsIdentifier,
            IReadOnlyDictionary<string, IReadOnlyList<string>> fileTypeChoices,
            string suggestedFileName,
            SuggestedPickerLocation suggestedStartLocation = SuggestedPickerLocation.DocumentsLibrary,
            string? suggestedStartFolder = null)
        {
            return RunModernPickerAsync(async windowId =>
            {
                var picker = new FileSavePicker(windowId)
                {
                    Title = title ?? string.Empty,
                    SettingsIdentifier = settingsIdentifier ?? string.Empty,
                    SuggestedStartLocation = MapPickerLocation(suggestedStartLocation),
                    SuggestedFileName = suggestedFileName ?? string.Empty
                };

                if (!string.IsNullOrWhiteSpace(suggestedStartFolder))
                {
                    picker.SuggestedStartFolder = suggestedStartFolder;
                }

                foreach (var choice in fileTypeChoices)
                {
                    picker.FileTypeChoices.Add(choice.Key, new List<string>(choice.Value));
                }

                var result = await picker.PickSaveFileAsync();
                return result?.Path;
            });
        }

        /// <summary>
        /// 赞助窗口
        /// </summary>
        private static void ConfigureSponsorWindow(Window sponsorWindow)
        {
            var appWindow = sponsorWindow.AppWindow;
            if (appWindow == null)
            {
                return;
            }

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
            }

            const int width = 560;
            const int height = 400;
            var size = new SizeInt32(width, height);
            appWindow.Resize(size);
            appWindow.Title = I18n.GetString("SponsorWindow_Title");

            var owner = GetMainWindow()?.AppWindow;
            if (owner != null)
            {
                var x = owner.Position.X + Math.Max(0, (owner.Size.Width - width) / 2);
                var y = owner.Position.Y + Math.Max(0, (owner.Size.Height - height) / 2);
                appWindow.Move(new PointInt32(x, y));
            }
        }

        private static async Task<string?> RunModernPickerAsync(Func<WindowId, Task<string?>> pickerAction)
        {
            if (pickerAction == null)
            {
                return null;
            }

            var window = GetMainWindow();
            if (window == null)
            {
                LogService.LogWarning(I18n.GetString("Picker_MainWindowUnavailable"), nameof(MainWindowService));
                NotificationService.ShowError(I18n.GetString("Picker_OpenFailed"));
                return null;
            }

            try
            {
                return await UiDispatcherService.RunOnUiAsync(async () =>
                {
                    var hwnd = WindowNative.GetWindowHandle(window);
                    var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                    return await pickerAction(windowId);
                });
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("Picker_Log_OpenFailed", ex.Message), nameof(MainWindowService), ex);
                NotificationService.ShowError(I18n.Format("Picker_OpenFailedWithReason", ex.Message));
                return null;
            }
        }

        private static PickerLocationId MapPickerLocation(SuggestedPickerLocation location)
        {
            return location switch
            {
                SuggestedPickerLocation.ComputerFolder => PickerLocationId.ComputerFolder,
                SuggestedPickerLocation.DocumentsLibrary => PickerLocationId.DocumentsLibrary,
                SuggestedPickerLocation.Downloads => PickerLocationId.Downloads,
                SuggestedPickerLocation.PicturesLibrary => PickerLocationId.PicturesLibrary,
                SuggestedPickerLocation.VideosLibrary => PickerLocationId.VideosLibrary,
                SuggestedPickerLocation.MusicLibrary => PickerLocationId.MusicLibrary,
                _ => PickerLocationId.Unspecified
            };
        }
    }
}

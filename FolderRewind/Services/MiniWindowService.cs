using FolderRewind.Models;
using FolderRewind.Services.Hotkeys;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    /// <summary>
    /// 管理所有 Mini 窗口的生命周期：创建、追踪、销毁、热键分发。
    /// </summary>
    public static class MiniWindowService
    {
        /// <summary>
        /// 已打开的 Mini 窗口列表，Key = ManagedFolder.Path（大小写不敏感）
        /// </summary>
        private static readonly Dictionary<string, Views.MiniWindow> _windows
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 最近获得焦点的 Mini 窗口（用于全局热键路由）
        /// </summary>
        private static Views.MiniWindow? _lastFocused;

        /// <summary>
        /// 热键定义 ID
        /// </summary>
        public const string Action_MiniBackup = "core.mini.backup";

        /// <summary>
        /// 打开一个新的 Mini 窗口。如果该文件夹已有窗口，则激活已有窗口。
        /// </summary>
        public static void Open(BackupConfig config, ManagedFolder folder)
        {
            if (config == null || folder == null) return;
            if (string.IsNullOrWhiteSpace(folder.Path)) return;

            if (_windows.TryGetValue(folder.Path, out var existing))
            {
                // 已有窗口，激活
                try
                {
                    existing.Activate();
                }
                catch { }
                return;
            }

            try
            {
                var context = new MiniWindowContext
                {
                    Config = config,
                    Folder = folder,
                };

                var mini = new Views.MiniWindow(context);
                _windows[folder.Path] = mini;

                mini.Closed += (_, __) =>
                {
                    _windows.Remove(folder.Path);
                    FolderWatcherService.StopWatching(folder.Path);

                    if (_lastFocused == mini)
                        _lastFocused = _windows.Values.LastOrDefault();
                };

                mini.Activated += (_, args) =>
                {
                    if (args.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated)
                    {
                        _lastFocused = mini;
                    }
                };

                // 启动 FileSystemWatcher
                FolderWatcherService.StartWatching(folder.Path);

                mini.Activate();
                _lastFocused = mini;

                LogService.Log(I18n.Format("MiniWindow_Log_Opened", folder.DisplayName ?? folder.Path));
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("MiniWindow_Log_OpenFailed", ex.Message), nameof(MiniWindowService), ex);
            }
        }

        /// <summary>
        /// 关闭指定文件夹的 Mini 窗口
        /// </summary>
        public static void Close(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return;
            if (_windows.TryGetValue(folderPath, out var win))
            {
                try { win.Close(); } catch { }
            }
        }

        /// <summary>
        /// 关闭所有 Mini 窗口
        /// </summary>
        public static void CloseAll()
        {
            foreach (var kvp in _windows.ToList())
            {
                try { kvp.Value.Close(); } catch { }
            }
            _windows.Clear();
            _lastFocused = null;
            FolderWatcherService.StopAll();
        }

        /// <summary>
        /// 判断某个文件夹是否已打开 Mini 窗口
        /// </summary>
        public static bool IsOpen(string folderPath)
        {
            return !string.IsNullOrWhiteSpace(folderPath) && _windows.ContainsKey(folderPath);
        }

        /// <summary>
        /// 获取当前所有已打开 Mini 窗口的数量
        /// </summary>
        public static int Count => _windows.Count;

        /// <summary>
        /// 注册 Mini 备份热键和处理器。在 HotkeyManager.Initialize 之后调用。
        /// </summary>
        public static void RegisterHotkey()
        {
            HotkeyManager.RegisterDefinitions(new[]
            {
                new HotkeyDefinition
                {
                    Id = Action_MiniBackup,
                    DisplayName = I18n.GetString("Hotkeys_MiniBackup_DisplayName"),
                    Description = I18n.GetString("Hotkeys_MiniBackup_Description"),
                    DefaultGesture = "Ctrl+Alt+A",
                    Scope = HotkeyScope.GlobalHotkey,
                },
            });

            HotkeyManager.RegisterHandler(Action_MiniBackup, OnMiniBackupHotkeyAsync);
        }

        private static async Task OnMiniBackupHotkeyAsync(HotkeyTrigger trigger)
        {
            var target = _lastFocused;
            if (target == null)
            {
                LogService.Log(I18n.GetString("MiniWindow_Log_NoActiveWindow"));
                return;
            }

            try
            {
                target.TriggerBackupFromHotkey();
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("MiniWindow_Log_HotkeyBackupFailed", ex.Message), nameof(MiniWindowService), ex);
            }
        }
    }
}

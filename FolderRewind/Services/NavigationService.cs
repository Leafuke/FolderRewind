using System;

namespace FolderRewind.Services
{
    public interface INavigationHost
    {
        void NavigateTo(string pageTag, object? parameter = null);
    }

    public static class NavigationService
    {
        public const string SettingsMinecraftPresetTarget = "Settings.MinecraftPreset";

        private static readonly object SyncRoot = new();
        // 当前仅持有一个导航宿主（ShellPage），在宿主切换时允许被新实例覆盖。
        private static INavigationHost? _host;

        public static void Initialize(INavigationHost host)
        {
            if (host == null)
            {
                return;
            }

            lock (SyncRoot)
            {
                // 以最后一次注册为准，适配窗口重建或页面重载。
                _host = host;
            }
        }

        public static void Clear(INavigationHost host)
        {
            if (host == null)
            {
                return;
            }

            lock (SyncRoot)
            {
                // 只清自己，避免误清掉后续新注册的宿主。
                if (ReferenceEquals(_host, host))
                {
                    _host = null;
                }
            }
        }

        public static bool NavigateTo(string pageTag, object? parameter = null)
        {
            if (string.IsNullOrWhiteSpace(pageTag))
            {
                return false;
            }

            INavigationHost? host;
            lock (SyncRoot)
            {
                host = _host;
            }

            if (host == null)
            {
                // 启动早期或宿主卸载期间允许返回 false，调用方自行决定是否兜底。
                return false;
            }

            host.NavigateTo(pageTag, parameter);
            return true;
        }
    }
}

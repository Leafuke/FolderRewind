using System;

namespace FolderRewind.Services
{
    public interface INavigationHost
    {
        void NavigateTo(string pageTag, object? parameter = null);
    }

    public static class NavigationService
    {
        private static readonly object SyncRoot = new();
        private static INavigationHost? _host;

        public static void Initialize(INavigationHost host)
        {
            if (host == null)
            {
                return;
            }

            lock (SyncRoot)
            {
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
                return false;
            }

            host.NavigateTo(pageTag, parameter);
            return true;
        }
    }
}
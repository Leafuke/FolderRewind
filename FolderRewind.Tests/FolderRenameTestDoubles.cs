using System.Collections.ObjectModel;

namespace FolderRewind.Models
{
    public sealed class ManagedFolder
    {
        public string Path { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }

    public sealed class AutomationSettings
    {
        public string TargetFolderPath { get; set; } = string.Empty;
    }

    public sealed class BackupConfig
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string DestinationPath { get; set; } = string.Empty;
        public ObservableCollection<ManagedFolder> SourceFolders { get; set; } = [];
        public AutomationSettings Automation { get; set; } = new();
    }

    public sealed class GlobalSettings
    {
        public string LastManagerFolderPath { get; set; } = string.Empty;
        public string LastHistoryFolderPath { get; set; } = string.Empty;
    }

    public sealed class AppConfig
    {
        public ObservableCollection<BackupConfig> BackupConfigs { get; set; } = [];
        public GlobalSettings GlobalSettings { get; set; } = new();
    }

}

namespace FolderRewind.Services
{
    using FolderRewind.Models;

    public static class ConfigService
    {
        public static AppConfig CurrentConfig { get; set; } = new();
        public static Queue<ConfigSaveResult> SaveResults { get; } = new();
        public static Action? BeforeSave { get; set; }

        public static void Save()
        {
        }

        public static ConfigSaveResult SaveWithResult(bool publishSavedEvent = true)
        {
            BeforeSave?.Invoke();
            return SaveResults.Count > 0
                ? SaveResults.Dequeue()
                : new ConfigSaveResult { Success = true };
        }

        public static Task<ConfigSaveResult> SaveAsync(bool publishSavedEvent = true, CancellationToken cancellationToken = default)
            => Task.FromResult(SaveWithResult(publishSavedEvent));

        internal static void PublishSaved()
        {
        }
    }

    public static class MiniWindowService
    {
        public static bool IsOpen(string path) => false;
        public static void Close(string path)
        {
        }

        public static void Open(BackupConfig config, ManagedFolder folder)
        {
        }
    }

    public static class FolderWatcherService
    {
        public static bool IsWatching(string path) => false;
        public static bool HasChanges(string path) => false;
        public static void StopWatching(string path)
        {
        }

        public static void StartWatching(string path, bool initialHasChanges = false)
        {
        }

        public static void MarkChanged(string path)
        {
        }
    }
}

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

    public sealed class HistoryItem
    {
        public string ConfigId { get; set; } = string.Empty;
        public string FolderPath { get; set; } = string.Empty;
        public string FolderName { get; set; } = string.Empty;
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

        internal static void PublishSaved()
        {
        }
    }

    public static class HistoryService
    {
        public static Queue<HistorySaveResult> SaveResults { get; } = new();
        public static int GetEntriesForConfigCallCount { get; set; }

        public static void Initialize()
        {
        }

        public static List<HistoryItem> GetEntriesForConfig(string configId)
        {
            GetEntriesForConfigCallCount++;
            return [];
        }

        internal static HistoryFolderIdentityUpdate UpdateFolderIdentities(
            IReadOnlyList<FolderRenameReferencePlan> references)
            => new();

        internal static void RestoreFolderIdentities(
            IReadOnlyList<HistoryFolderIdentitySnapshot> snapshots)
        {
        }

        internal static Task<HistorySaveResult> SaveNowAsync(
            bool publishChangedEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                SaveResults.Count > 0
                    ? SaveResults.Dequeue()
                    : new HistorySaveResult { Success = true });
        }

        internal static void PublishChanged()
        {
        }
    }

    public static class BackupStoragePathService
    {
        public static bool TryResolveStorageFolderName(
            string displayName,
            string fallbackPath,
            out string storageFolderName)
        {
            storageFolderName = string.IsNullOrWhiteSpace(displayName)
                ? Path.GetFileName(Path.TrimEndingDirectorySeparator(fallbackPath))
                : displayName.Trim();
            return !string.IsNullOrWhiteSpace(storageFolderName);
        }

        public static bool TryBuildPathWithinRoot(
            string rootPath,
            string childName,
            out string fullPath)
        {
            fullPath = Path.GetFullPath(Path.Combine(rootPath, childName));
            return IsPathInsideRoot(fullPath, rootPath);
        }

        public static bool IsPathInsideRoot(string candidatePath, string rootPath)
        {
            string root = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string candidate = Path.GetFullPath(candidatePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
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

namespace FolderRewind.Services
{
    public enum BackupInvocationSource
    {
        Unknown = 0,
        Manual = 1,
        Automatic = 2,
        Remote = 3,
        PluginHotkey = 4,
        Internal = 5
    }

    public sealed class BackupInvocationOptions
    {
        public BackupInvocationSource Source { get; init; } = BackupInvocationSource.Unknown;

        public bool PreferApplicationConsistentSnapshot { get; init; }

        public static BackupInvocationOptions Default { get; } = new();

        public static BackupInvocationOptions ForManual() => new()
        {
            Source = BackupInvocationSource.Manual,
            PreferApplicationConsistentSnapshot = true
        };

        public static BackupInvocationOptions ForAutomatic() => new()
        {
            Source = BackupInvocationSource.Automatic,
            PreferApplicationConsistentSnapshot = true
        };

        public static BackupInvocationOptions ForRemote(bool preferApplicationConsistentSnapshot = true) => new()
        {
            Source = BackupInvocationSource.Remote,
            PreferApplicationConsistentSnapshot = preferApplicationConsistentSnapshot
        };

        public static BackupInvocationOptions ForPluginHotkey() => new()
        {
            Source = BackupInvocationSource.PluginHotkey,
            PreferApplicationConsistentSnapshot = true
        };

        public static BackupInvocationOptions ForInternal() => new()
        {
            Source = BackupInvocationSource.Internal,
            PreferApplicationConsistentSnapshot = false
        };

        public BackupInvocationOptions WithApplicationConsistentSnapshot(bool prefer = true) => new()
        {
            Source = Source,
            PreferApplicationConsistentSnapshot = prefer
        };
    }
}

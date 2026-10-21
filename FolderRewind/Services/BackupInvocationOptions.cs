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
        public string Comment { get; init; } = string.Empty;
        public bool Protect { get; init; }
        public bool RequestSemanticCheckpoint { get; init; }
        internal BackupConfigurationHandoff? ConfigurationHandoff { get; init; }
        internal bool ForceFullCapture { get; init; }
        internal System.Collections.Generic.IReadOnlyDictionary<History.Domain.SourceId, History.Application.HistoryRestoreSourceBinding>? PhysicalSourceBindings { get; init; }

        public static BackupInvocationOptions Default { get; } = new();

        public static BackupInvocationOptions ForManual(string? comment = null) => new()
        {
            Source = BackupInvocationSource.Manual,
            PreferApplicationConsistentSnapshot = true,
            Comment = comment ?? string.Empty
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
        internal static BackupInvocationOptions ForSafetySnapshot(System.Collections.Generic.IReadOnlyList<History.Application.HistoryRestoreSourceBinding>? bindings = null) => new()
        {
            Source = BackupInvocationSource.Internal,
            ForceFullCapture = true,
            PhysicalSourceBindings = bindings is null ? null : System.Linq.Enumerable.ToDictionary(bindings, b => b.SourceId)
        };

        public BackupInvocationOptions WithApplicationConsistentSnapshot(bool prefer = true) => new()
        {
            Source = Source,
            PreferApplicationConsistentSnapshot = prefer,
            Comment = Comment,
            Protect = Protect,
            RequestSemanticCheckpoint = RequestSemanticCheckpoint, ConfigurationHandoff = ConfigurationHandoff, ForceFullCapture = ForceFullCapture, PhysicalSourceBindings = PhysicalSourceBindings
        };

        public BackupInvocationOptions WithComment(string? comment) => new()
        {
            Source = Source,
            PreferApplicationConsistentSnapshot = PreferApplicationConsistentSnapshot,
            Comment = comment ?? string.Empty,
            Protect = Protect,
            RequestSemanticCheckpoint = RequestSemanticCheckpoint, ConfigurationHandoff = ConfigurationHandoff, ForceFullCapture = ForceFullCapture, PhysicalSourceBindings = PhysicalSourceBindings
        };

        public BackupInvocationOptions WithProtection(bool protect) => new()
        {
            Source = Source, PreferApplicationConsistentSnapshot = PreferApplicationConsistentSnapshot,
            Comment = Comment, Protect = protect, RequestSemanticCheckpoint = RequestSemanticCheckpoint, ConfigurationHandoff = ConfigurationHandoff, ForceFullCapture = ForceFullCapture, PhysicalSourceBindings = PhysicalSourceBindings
        };

        public BackupInvocationOptions WithSemanticCheckpoint() => new()
        {
            Source = Source, PreferApplicationConsistentSnapshot = PreferApplicationConsistentSnapshot,
            Comment = Comment, Protect = Protect, RequestSemanticCheckpoint = true, ConfigurationHandoff = ConfigurationHandoff, ForceFullCapture = ForceFullCapture, PhysicalSourceBindings = PhysicalSourceBindings
        };
    }
}

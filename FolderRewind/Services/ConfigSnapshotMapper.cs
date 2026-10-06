using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace FolderRewind.Services;

/// <summary>Explicit detached copy of persisted configuration fields. Keep in sync with AppJsonContext models.</summary>
internal static class ConfigSnapshotMapper
{
    public static AppConfig Copy(AppConfig? value) => value is null ? null! : new AppConfig
    {
        Legacy182UpgradePending = value.Legacy182UpgradePending,
        SchemaVersion = value.SchemaVersion,
        GlobalSettings = Copy(value.GlobalSettings),
        BackupConfigs = value.BackupConfigs is null ? null! : new ObservableCollection<BackupConfig>(value.BackupConfigs.Select(item0 => Copy(item0))),
        BackupPresets = value.BackupPresets is null ? null! : new ObservableCollection<BackupPreset>(value.BackupPresets.Select(item0 => Copy(item0))),
        SchemaExtensions = value.SchemaExtensions is null ? null! : value.SchemaExtensions.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.SchemaExtensions.Comparer),
    };

    public static GlobalSettings Copy(GlobalSettings? value) => value is null ? null! : new GlobalSettings
    {
        Language = value.Language,
        ThemeIndex = value.ThemeIndex,
        SevenZipPath = value.SevenZipPath,
        RcloneExecutablePath = value.RcloneExecutablePath,
        OpenListRuntime = Copy(value.OpenListRuntime),
        DefaultCloudRemoteBasePath = value.DefaultCloudRemoteBasePath,
        DefaultBackupRootPath = value.DefaultBackupRootPath,
        AutoDownloadMissingCloudBackupsBeforeRestore = value.AutoDownloadMissingCloudBackupsBeforeRestore,
        RunOnStartup = value.RunOnStartup,
        SilentStartup = value.SilentStartup,
        EnableFileLogging = value.EnableFileLogging,
        LogRetentionDays = value.LogRetentionDays,
        MaxLogFileSizeMb = value.MaxLogFileSizeMb,
        IsNavPaneOpen = value.IsNavPaneOpen,
        StartupWidth = value.StartupWidth,
        StartupHeight = value.StartupHeight,
        NavPaneWidth = value.NavPaneWidth,
        FontFamily = value.FontFamily,
        BaseFontSize = value.BaseFontSize,
        HomeSortMode = value.HomeSortMode,
        LastManagerConfigId = value.LastManagerConfigId,
        LastManagerFolderPath = value.LastManagerFolderPath,
        LastHistoryConfigId = value.LastHistoryConfigId,
        LastHistoryFolderPath = value.LastHistoryFolderPath,
        LastHistoryPresentationMode = value.LastHistoryPresentationMode,
        LastHistoryViewMode = value.LastHistoryViewMode,
        SponsorAccentColorIndex = value.SponsorAccentColorIndex,
        SponsorBackdropIndex = value.SponsorBackdropIndex,
        SponsorTitleText = value.SponsorTitleText,
        SponsorTitleIconGlyph = value.SponsorTitleIconGlyph,
        SponsorBackgroundEnabled = value.SponsorBackgroundEnabled,
        SponsorBackgroundImagePath = value.SponsorBackgroundImagePath,
        SponsorBackgroundStretchIndex = value.SponsorBackgroundStretchIndex,
        SponsorBackgroundImageOpacity = value.SponsorBackgroundImageOpacity,
        SponsorBackgroundOverlayOpacity = value.SponsorBackgroundOverlayOpacity,
        CompletionSoundIndex = value.CompletionSoundIndex,
        CompletionSoundCustomPath = value.CompletionSoundCustomPath,
        SponsorEntitlementCached = value.SponsorEntitlementCached,
        SponsorEntitlementLastVerifiedUtc = value.SponsorEntitlementLastVerifiedUtc,
        UseHistoryStatusColors = value.UseHistoryStatusColors,
        CloseBehavior = value.CloseBehavior,
        RememberCloseBehavior = value.RememberCloseBehavior,
        Plugins = Copy(value.Plugins),
        GameDiscovery = Copy(value.GameDiscovery),
        EnableKnotLink = value.EnableKnotLink,
        KnotLinkHost = value.KnotLinkHost,
        KnotLinkAppId = value.KnotLinkAppId,
        KnotLinkOpenSocketId = value.KnotLinkOpenSocketId,
        KnotLinkSignalId = value.KnotLinkSignalId,
        AutoStartKnotLinkServer = value.AutoStartKnotLinkServer,
        Hotkeys = Copy(value.Hotkeys),
        EnableNotifications = value.EnableNotifications,
        ToastNotificationLevel = value.ToastNotificationLevel,
        FileSizeWarningThresholdKB = value.FileSizeWarningThresholdKB,
        EnableNotices = value.EnableNotices,
        NoticeLastSeenVersion = value.NoticeLastSeenVersion,
        EnableUpdateReminder = value.EnableUpdateReminder,
        AppUpdatePreferredSource = value.AppUpdatePreferredSource,
        AppUpdateAutoFallback = value.AppUpdateAutoFallback,
        AppUpdateCustomMirrorUrl = value.AppUpdateCustomMirrorUrl,
        GitHubOAuthClientId = value.GitHubOAuthClientId,
        HasShownFirstLaunchGuide = value.HasShownFirstLaunchGuide,
        MsiShellIdentityNoticeShown = value.MsiShellIdentityNoticeShown,
        HasTriggeredInitialCoreValidation = value.HasTriggeredInitialCoreValidation,
        LastCoreValidationPassed = value.LastCoreValidationPassed,
        LastCoreValidationUtc = value.LastCoreValidationUtc,
        LastCoreValidationSummary = value.LastCoreValidationSummary,
        SchemaExtensions = value.SchemaExtensions is null ? null! : value.SchemaExtensions.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.SchemaExtensions.Comparer),
    };

    public static BackupConfig Copy(BackupConfig? value) => value is null ? null! : new BackupConfig
    {
        Id = value.Id,
        Name = value.Name,
        DestinationPath = value.DestinationPath,
        ConfigRevision = value.ConfigRevision,
        Kind = Copy(value.Kind),
        ProviderStates = value.ProviderStates is null ? null! : value.ProviderStates.ToDictionary(pair0 => pair0.Key, pair0 => Copy(pair0.Value), StringComparer.OrdinalIgnoreCase),
        HostOrigin = Copy(value.HostOrigin),
        LegacyPreservation = Copy(value.LegacyPreservation),
        RequiredPluginId = value.RequiredPluginId,
        ArtifactTransformPolicy = Copy(value.ArtifactTransformPolicy),
        ConsistencyIntent = value.ConsistencyIntent,
        HistoryRepositoryBinding = Copy(value.HistoryRepositoryBinding),
        IsEncrypted = value.IsEncrypted,
        DiscoveryOrigin = Copy(value.DiscoveryOrigin),
        IconGlyph = value.IconGlyph,
        SourceFolders = value.SourceFolders is null ? null! : new ObservableCollection<ManagedFolder>(value.SourceFolders.Select(item0 => Copy(item0))),
        Archive = Copy(value.Archive),
        Automation = Copy(value.Automation),
        Filters = Copy(value.Filters),
        BackupScope = Copy(value.BackupScope),
        Cloud = Copy(value.Cloud),
        SchemaExtensions = value.SchemaExtensions is null ? null! : value.SchemaExtensions.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.SchemaExtensions.Comparer),
    };

    public static BackupPreset Copy(BackupPreset? value) => value is null ? null! : new BackupPreset
    {
        Id = value.Id,
        ShareId = value.ShareId,
        ShareCode = value.ShareCode,
        Name = value.Name,
        Author = value.Author,
        Description = value.Description,
        GameName = value.GameName,
        SteamAppId = value.SteamAppId,
        Version = value.Version,
        SchemaVersion = value.SchemaVersion,
        Kind = Copy(value.Kind),
        IsEncrypted = value.IsEncrypted,
        IconGlyph = value.IconGlyph,
        DefaultConfigName = value.DefaultConfigName,
        CreatedUtc = value.CreatedUtc,
        UpdatedUtc = value.UpdatedUtc,
        Archive = Copy(value.Archive),
        Automation = Copy(value.Automation),
        Filters = Copy(value.Filters),
        BackupScope = Copy(value.BackupScope),
        Cloud = Copy(value.Cloud),
        RequiredPluginIds = value.RequiredPluginIds is null ? null! : new ObservableCollection<string>(value.RequiredPluginIds.Select(item0 => item0)),
        ProviderDefaults = value.ProviderDefaults is null ? null! : value.ProviderDefaults.ToDictionary(pair0 => pair0.Key, pair0 => Copy(pair0.Value), value.ProviderDefaults.Comparer),
        PathRules = value.PathRules is null ? null! : new ObservableCollection<TemplatePathRule>(value.PathRules.Select(item0 => Copy(item0))),
        DiscoverySources = value.DiscoverySources is null ? null! : new ObservableCollection<BackupPresetDiscoverySource>(value.DiscoverySources.Select(item0 => Copy(item0))),
        IsRecommended = value.IsRecommended,
        SchemaExtensions = value.SchemaExtensions is null ? null! : value.SchemaExtensions.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.SchemaExtensions.Comparer),
    };

    public static OpenListRuntimeSettings Copy(OpenListRuntimeSettings? value) => value is null ? null! : new OpenListRuntimeSettings
    {
        ExecutablePath = value.ExecutablePath,
        WorkingDirectory = value.WorkingDirectory,
        DataDirectory = value.DataDirectory,
        ConfigFilePath = value.ConfigFilePath,
        ServiceBaseUri = value.ServiceBaseUri,
        AllowStartOnDemand = value.AllowStartOnDemand,
    };

    public static PluginHostSettings Copy(PluginHostSettings? value) => value is null ? null! : new PluginHostSettings
    {
        AutoCheckUpdates = value.AutoCheckUpdates,
        EnabledIntent = value.EnabledIntent is null ? null! : value.EnabledIntent.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value, value.EnabledIntent.Comparer),
        TypedSettings = value.TypedSettings is null ? null! : value.TypedSettings.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value is null ? null! : pair0.Value.ToDictionary(pair1 => pair1.Key, pair1 => pair1.Value.Clone(), pair0.Value.Comparer), value.TypedSettings.Comparer),
        SchemaExtensions = value.SchemaExtensions is null ? null! : value.SchemaExtensions.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.SchemaExtensions.Comparer),
    };

    public static GameDiscoverySettings Copy(GameDiscoverySettings? value) => value is null ? null! : new GameDiscoverySettings
    {
        PluginRoots = value.PluginRoots is null ? null! : value.PluginRoots.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value is null ? null! : new List<string>(pair0.Value.Select(item1 => item1)), value.PluginRoots.Comparer),
        LibraryRoots = value.LibraryRoots is null ? null! : new ObservableCollection<GameLibraryRootSetting>(value.LibraryRoots.Select(item0 => Copy(item0))),
        SecondaryManifestPath = value.SecondaryManifestPath,
        OverridePath = value.OverridePath,
        SchemaExtensions = value.SchemaExtensions is null ? null! : value.SchemaExtensions.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.SchemaExtensions.Comparer),
    };

    public static HotkeySettings Copy(HotkeySettings? value) => value is null ? null! : new HotkeySettings
    {
        Bindings = value.Bindings is null ? null! : value.Bindings.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value, value.Bindings.Comparer),
        SchemaExtensions = value.SchemaExtensions is null ? null! : value.SchemaExtensions.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.SchemaExtensions.Comparer),
    };

    public static ConfigKindReference Copy(ConfigKindReference? value) => value is null ? null! : new ConfigKindReference
    {
        OwnerId = value.OwnerId,
        KindId = value.KindId,
    };

    public static ProviderStatePayload Copy(ProviderStatePayload? value) => value is null ? null! : new ProviderStatePayload
    {
        SchemaVersion = value.SchemaVersion,
        Data = value.Data.Clone(),
    };

    public static HostConfigOrigin Copy(HostConfigOrigin? value) => value is null ? null! : new HostConfigOrigin
    {
        TemplateId = value.TemplateId,
        TemplateName = value.TemplateName,
        DiscoveryProviderId = value.DiscoveryProviderId,
        DiscoveryCandidateId = value.DiscoveryCandidateId,
    };

    public static LegacyConfigPreservation Copy(LegacyConfigPreservation? value) => value is null ? null! : new LegacyConfigPreservation
    {
        OriginalConfigType = value.OriginalConfigType,
        OriginalPluginMarker = value.OriginalPluginMarker,
        OriginalExtendedProperties = value.OriginalExtendedProperties is null ? null! : value.OriginalExtendedProperties.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.OriginalExtendedProperties.Comparer),
        Warnings = value.Warnings is null ? null! : new List<string>(value.Warnings.Select(item0 => item0)),
        Unknown = value.Unknown is null ? null! : value.Unknown.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.Unknown.Comparer),
    };

    public static ArtifactTransformPolicySettings Copy(ArtifactTransformPolicySettings? value) => value is null ? null! : new ArtifactTransformPolicySettings
    {
        Transformer = Copy(value.Transformer),
        Parameters = value.Parameters is null ? null! : value.Parameters.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.Parameters.Comparer),
        FailureBehavior = value.FailureBehavior,
    };

    public static HistoryRepositoryBinding Copy(HistoryRepositoryBinding? value) => value is null ? null! : new HistoryRepositoryBinding
    {
        FormatVersion = value.FormatVersion,
    };

    public static DiscoveryOrigin Copy(DiscoveryOrigin? value) => value is null ? null! : new DiscoveryOrigin
    {
        Identity = Copy(value.Identity),
        ReviewedBaseline = Copy(value.ReviewedBaseline),
        PresetShareId = value.PresetShareId,
        PresetVersion = value.PresetVersion,
        ManifestRevision = value.ManifestRevision,
    };

    public static ManagedFolder Copy(ManagedFolder? value) => value is null ? null! : new ManagedFolder
    {
        Id = value.Id,
        Path = value.Path,
        DisplayName = value.DisplayName,
        Description = value.Description,
        IsFavorite = value.IsFavorite,
        LastBackupTime = value.LastBackupTime,
        CoverImagePath = value.CoverImagePath,
        SourceScope = Copy(value.SourceScope),
        ProviderStates = value.ProviderStates is null ? null! : value.ProviderStates.ToDictionary(pair0 => pair0.Key, pair0 => Copy(pair0.Value), StringComparer.OrdinalIgnoreCase),
        SchemaExtensions = value.SchemaExtensions is null ? null! : value.SchemaExtensions.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.SchemaExtensions.Comparer),
    };

    public static ArchiveSettings Copy(ArchiveSettings? value) => value is null ? null! : new ArchiveSettings
    {
        Format = value.Format,
        CompressionLevel = value.CompressionLevel,
        Method = value.Method,
        KeepCount = value.KeepCount,
        Mode = value.Mode,
        SkipIfUnchanged = value.SkipIfUnchanged,
        CpuThreads = value.CpuThreads,
        BackupBeforeRestore = value.BackupBeforeRestore,
        SafeRestoreEnabled = value.SafeRestoreEnabled,
        VerifyArchiveBeforeRestore = value.VerifyArchiveBeforeRestore,
        MaxSmartBackupsPerFull = value.MaxSmartBackupsPerFull,
        RunCompressionAtLowPriority = value.RunCompressionAtLowPriority,
        AdditionalSevenZipArguments = value.AdditionalSevenZipArguments,
        FileTypeHandlingEnabled = value.FileTypeHandlingEnabled,
        FileTypeRules = value.FileTypeRules is null ? null! : new ObservableCollection<FileTypeRule>(value.FileTypeRules.Select(item0 => Copy(item0))),
        SchemaExtensions = value.SchemaExtensions is null ? null! : value.SchemaExtensions.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.SchemaExtensions.Comparer),
    };

    public static AutomationSettings Copy(AutomationSettings? value) => value is null ? null! : new AutomationSettings
    {
        AutoBackupEnabled = value.AutoBackupEnabled,
        IntervalMode = value.IntervalMode,
        IntervalMinutes = value.IntervalMinutes,
        RunOnAppStart = value.RunOnAppStart,
        ScheduledMode = value.ScheduledMode,
        Scope = value.Scope,
        TargetFolderPath = value.TargetFolderPath,
        ConditionalModeEnabled = value.ConditionalModeEnabled,
        ConditionType = value.ConditionType,
        ConditionRelativePath = value.ConditionRelativePath,
        ScheduleEntries = value.ScheduleEntries is null ? null! : new ObservableCollection<ScheduleEntry>(value.ScheduleEntries.Select(item0 => Copy(item0))),
        LastAutoBackupUtc = value.LastAutoBackupUtc,
        StopAfterNoChangeEnabled = value.StopAfterNoChangeEnabled,
        StopAfterNoChangeCount = value.StopAfterNoChangeCount,
        ConsecutiveNoChangeCount = value.ConsecutiveNoChangeCount,
        SchemaExtensions = value.SchemaExtensions is null ? null! : value.SchemaExtensions.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.SchemaExtensions.Comparer),
    };

    public static FilterSettings Copy(FilterSettings? value) => value is null ? null! : new FilterSettings
    {
        Blacklist = value.Blacklist is null ? null! : new ObservableCollection<string>(value.Blacklist.Select(item0 => item0)),
        BackupFilterMode = value.BackupFilterMode,
        BackupWhitelist = value.BackupWhitelist is null ? null! : new ObservableCollection<string>(value.BackupWhitelist.Select(item0 => item0)),
        UseRegex = value.UseRegex,
        RestoreWhitelist = value.RestoreWhitelist is null ? null! : new ObservableCollection<string>(value.RestoreWhitelist.Select(item0 => item0)),
        SchemaExtensions = value.SchemaExtensions is null ? null! : value.SchemaExtensions.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.SchemaExtensions.Comparer),
    };

    public static BackupScopeSettings Copy(BackupScopeSettings? value) => value is null ? null! : new BackupScopeSettings
    {
        OwnerId = value.OwnerId,
        ScopeId = value.ScopeId,
        Parameters = value.Parameters is null ? null! : value.Parameters.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value, StringComparer.OrdinalIgnoreCase),
        SchemaExtensions = value.SchemaExtensions is null ? null! : value.SchemaExtensions.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.SchemaExtensions.Comparer),
    };

    public static CloudSettings Copy(CloudSettings? value) => value is null ? null! : new CloudSettings
    {
        Enabled = value.Enabled,
        CommandMode = value.CommandMode,
        TemplateKind = value.TemplateKind,
        ExecutablePath = value.ExecutablePath,
        ArgumentsTemplate = value.ArgumentsTemplate,
        WorkingDirectory = value.WorkingDirectory,
        TimeoutSeconds = value.TimeoutSeconds,
        RetryCount = value.RetryCount,
        RemoteBasePath = value.RemoteBasePath,
        RcloneConfigPath = value.RcloneConfigPath,
        LocalOpenListServiceUri = value.LocalOpenListServiceUri,
        SyncHistoryAfterUpload = value.SyncHistoryAfterUpload,
        LastRunUtc = value.LastRunUtc,
        LastExitCode = value.LastExitCode,
        LastErrorMessage = value.LastErrorMessage,
        SchemaExtensions = value.SchemaExtensions is null ? null! : value.SchemaExtensions.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.SchemaExtensions.Comparer),
    };

    public static PresetProviderDefaults Copy(PresetProviderDefaults? value) => value is null ? null! : new PresetProviderDefaults
    {
        SchemaVersion = value.SchemaVersion,
        Values = value.Values is null ? null! : value.Values.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.Values.Comparer),
    };

    public static TemplatePathRule Copy(TemplatePathRule? value) => value is null ? null! : new TemplatePathRule
    {
        Id = value.Id,
        Name = value.Name,
        Segments = value.Segments is null ? null! : new ObservableCollection<TemplatePathSegment>(value.Segments.Select(item0 => Copy(item0))),
        Markers = value.Markers is null ? null! : new ObservableCollection<TemplatePathMarker>(value.Markers.Select(item0 => Copy(item0))),
        Confidence = value.Confidence,
        AutoAdd = value.AutoAdd,
        SchemaExtensions = value.SchemaExtensions is null ? null! : value.SchemaExtensions.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.SchemaExtensions.Comparer),
    };

    public static BackupPresetDiscoverySource Copy(BackupPresetDiscoverySource? value) => value is null ? null! : new BackupPresetDiscoverySource
    {
        Kind = value.Kind,
        ProviderId = value.ProviderId,
        DefinitionId = value.DefinitionId,
        PathRules = value.PathRules is null ? null! : new ObservableCollection<TemplatePathRule>(value.PathRules.Select(item0 => Copy(item0))),
        ExternalIds = value.ExternalIds is null ? null! : value.ExternalIds.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value, value.ExternalIds.Comparer),
        Properties = value.Properties is null ? null! : value.Properties.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value, value.Properties.Comparer),
        SchemaExtensions = value.SchemaExtensions is null ? null! : value.SchemaExtensions.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.SchemaExtensions.Comparer),
    };

    public static GameLibraryRootSetting Copy(GameLibraryRootSetting? value) => value is null ? null! : new GameLibraryRootSetting
    {
        Store = value.Store,
        Path = value.Path,
        IsEnabled = value.IsEnabled,
        IsAutoDetected = value.IsAutoDetected,
        SchemaExtensions = value.SchemaExtensions is null ? null! : value.SchemaExtensions.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.SchemaExtensions.Comparer),
    };

    public static ArtifactTransformerReference Copy(ArtifactTransformerReference? value) => value is null ? null! : new ArtifactTransformerReference
    {
        PluginId = value.PluginId,
        TransformerId = value.TransformerId,
    };

    public static DiscoverySetIdentity Copy(DiscoverySetIdentity? value) => value is null ? null! : new DiscoverySetIdentity
    {
        ProviderId = value.ProviderId,
        DefinitionId = value.DefinitionId,
        SetId = value.SetId,
        ExternalIds = value.ExternalIds is null ? null! : value.ExternalIds.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value, value.ExternalIds.Comparer),
    };

    public static ReviewedDiscoveryBaseline Copy(ReviewedDiscoveryBaseline? value) => value is null ? null! : new ReviewedDiscoveryBaseline
    {
        Sources = value.Sources is null ? null! : new ObservableCollection<ReviewedDiscoverySource>(value.Sources.Select(item0 => Copy(item0))),
        UserOverrides = value.UserOverrides is null ? null! : new ObservableCollection<ReviewedDiscoveryOverride>(value.UserOverrides.Select(item0 => Copy(item0))),
    };

    public static BackupSourceScope Copy(BackupSourceScope? value) => value is null ? null! : new BackupSourceScope
    {
        Mode = value.Mode,
        IncludePatterns = value.IncludePatterns is null ? null! : new ObservableCollection<string>(value.IncludePatterns.Select(item0 => item0)),
    };

    public static FileTypeRule Copy(FileTypeRule? value) => value is null ? null! : new FileTypeRule
    {
        Pattern = value.Pattern,
        CompressionLevel = value.CompressionLevel,
        SchemaExtensions = value.SchemaExtensions is null ? null! : value.SchemaExtensions.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.SchemaExtensions.Comparer),
    };

    public static ScheduleEntry Copy(ScheduleEntry? value) => value is null ? null! : new ScheduleEntry
    {
        MonthSelection = value.MonthSelection,
        DaySelection = value.DaySelection,
        Hour = value.Hour,
        Minute = value.Minute,
        LastTriggeredUtc = value.LastTriggeredUtc,
        SchemaExtensions = value.SchemaExtensions is null ? null! : value.SchemaExtensions.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.SchemaExtensions.Comparer),
    };

    public static TemplatePathSegment Copy(TemplatePathSegment? value) => value is null ? null! : new TemplatePathSegment
    {
        Type = value.Type,
        Value = value.Value,
        SchemaExtensions = value.SchemaExtensions is null ? null! : value.SchemaExtensions.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.SchemaExtensions.Comparer),
    };

    public static TemplatePathMarker Copy(TemplatePathMarker? value) => value is null ? null! : new TemplatePathMarker
    {
        Type = value.Type,
        Value = value.Value,
        SchemaExtensions = value.SchemaExtensions is null ? null! : value.SchemaExtensions.ToDictionary(pair0 => pair0.Key, pair0 => pair0.Value.Clone(), value.SchemaExtensions.Comparer),
    };

    public static ReviewedDiscoverySource Copy(ReviewedDiscoverySource? value) => value is null ? null! : new ReviewedDiscoverySource
    {
        NormalizedRootPath = value.NormalizedRootPath,
        Mode = value.Mode,
        IncludePatterns = value.IncludePatterns is null ? null! : new ObservableCollection<string>(value.IncludePatterns.Select(item0 => item0)),
        ResourceIds = value.ResourceIds is null ? null! : new ObservableCollection<string>(value.ResourceIds.Select(item0 => item0)),
    };

    public static ReviewedDiscoveryOverride Copy(ReviewedDiscoveryOverride? value) => value is null ? null! : new ReviewedDiscoveryOverride
    {
        NormalizedRootPath = value.NormalizedRootPath,
        UpstreamFingerprint = value.UpstreamFingerprint,
        CurrentFingerprint = value.CurrentFingerprint,
    };

}

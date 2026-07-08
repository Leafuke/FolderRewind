using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FolderRewind.Tests.Plugins;

public class FolderDetailsPluginServiceTests
{
    private sealed class GoodProvider : IFolderRewindPlugin, IFolderRewindFolderDetailsProvider
    {
        public PluginInstallManifest Manifest { get; } = new()
        {
            Id = "good",
            Name = "Good",
            Version = "1.0.0",
            Author = "test",
            Description = "test",
            EntryAssembly = "good.dll",
            EntryType = "GoodProvider"
        };

        public IReadOnlyList<PluginSettingDefinition> GetSettingsDefinitions() => [];

        public void Initialize(IReadOnlyDictionary<string, string> settingsValues) { }

        public string? OnBeforeBackupFolder(
            BackupConfig config,
            ManagedFolder folder,
            IReadOnlyDictionary<string, string> settingsValues) => null;

        public void OnAfterBackupFolder(
            BackupConfig config,
            ManagedFolder folder,
            bool success,
            string? generatedArchiveFileName,
            IReadOnlyDictionary<string, string> settingsValues) { }

        public IReadOnlyList<ManagedFolder> TryDiscoverManagedFolders(
            string selectedRootPath,
            IReadOnlyDictionary<string, string> settingsValues) => [];

        public Task<IReadOnlyList<FolderDetailsSection>> GetFolderDetailsSectionsAsync(
            BackupConfig config,
            ManagedFolder folder,
            IReadOnlyDictionary<string, string> settingsValues,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<FolderDetailsSection>>(
            [
                new FolderDetailsSection
                {
                    Title = "Plugin details",
                    Items =
                    {
                        new FolderDetailsItem
                        {
                            Label = "Mode",
                            Value = settingsValues.TryGetValue("mode", out string? mode) ? mode : "Demo"
                        }
                    }
                }
            ]);
        }
    }

    private sealed class ThrowingProvider : IFolderRewindPlugin, IFolderRewindFolderDetailsProvider
    {
        public PluginInstallManifest Manifest { get; } = new()
        {
            Id = "bad",
            Name = "Bad",
            Version = "1.0.0",
            Author = "test",
            Description = "test",
            EntryAssembly = "bad.dll",
            EntryType = "ThrowingProvider"
        };

        public IReadOnlyList<PluginSettingDefinition> GetSettingsDefinitions() => [];

        public void Initialize(IReadOnlyDictionary<string, string> settingsValues) { }

        public string? OnBeforeBackupFolder(
            BackupConfig config,
            ManagedFolder folder,
            IReadOnlyDictionary<string, string> settingsValues) => null;

        public void OnAfterBackupFolder(
            BackupConfig config,
            ManagedFolder folder,
            bool success,
            string? generatedArchiveFileName,
            IReadOnlyDictionary<string, string> settingsValues) { }

        public IReadOnlyList<ManagedFolder> TryDiscoverManagedFolders(
            string selectedRootPath,
            IReadOnlyDictionary<string, string> settingsValues) => [];

        public Task<IReadOnlyList<FolderDetailsSection>> GetFolderDetailsSectionsAsync(
            BackupConfig config,
            ManagedFolder folder,
            IReadOnlyDictionary<string, string> settingsValues,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task GetFolderDetailsSectionsFromPluginsAsync_continues_after_provider_failure()
    {
        SetCurrentConfig(CreateAppConfig(CreateGlobalSettings(new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["good"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["mode"] = "Demo"
            }
        })));

        var config = CreateBackupConfig("cfg");
        var folder = CreateManagedFolder(@"D:\Demo", "Demo");

        var sections = await PluginService.GetFolderDetailsSectionsFromPluginsAsync(
            [new ThrowingProvider(), new GoodProvider()],
            config,
            folder,
            CancellationToken.None);

        var section = Assert.Single(sections);
        Assert.Equal("Plugin details", section.Title);
        Assert.Collection(
            section.Items,
            item =>
            {
                Assert.Equal("Mode", item.Label);
                Assert.Equal("Demo", item.Value);
            });
    }

    private static BackupConfig CreateBackupConfig(string id)
    {
        var config = (BackupConfig)RuntimeHelpers.GetUninitializedObject(typeof(BackupConfig));
        config.Id = id;
        return config;
    }

    private static ManagedFolder CreateManagedFolder(string path, string displayName)
    {
        var folder = (ManagedFolder)RuntimeHelpers.GetUninitializedObject(typeof(ManagedFolder));
        folder.Path = path;
        folder.DisplayName = displayName;
        return folder;
    }

    private static AppConfig CreateAppConfig(GlobalSettings globalSettings)
    {
        var appConfig = (AppConfig)RuntimeHelpers.GetUninitializedObject(typeof(AppConfig));
        appConfig.GlobalSettings = globalSettings;
        return appConfig;
    }

    private static GlobalSettings CreateGlobalSettings(Dictionary<string, Dictionary<string, string>> pluginSettings)
    {
        var settings = (GlobalSettings)RuntimeHelpers.GetUninitializedObject(typeof(GlobalSettings));
        settings.Plugins = new PluginHostSettings
        {
            Enabled = true,
            PluginSettings = pluginSettings
        };
        return settings;
    }

    private static void SetCurrentConfig(AppConfig appConfig)
    {
        typeof(ConfigService)
            .GetProperty(nameof(ConfigService.CurrentConfig), BindingFlags.Public | BindingFlags.Static)!
            .SetValue(null, appConfig);
    }
}

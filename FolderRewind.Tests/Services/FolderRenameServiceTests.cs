using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace FolderRewind.Tests.Services;

public class FolderRenameServiceTests
{
    [Fact]
    public void PreviewRename_rejects_invalid_leaf_name()
    {
        var folder = CreateManagedFolder(@"D:\Games\Saves\WorldOne", "WorldOne");

        var preview = FolderRenameService.PreviewRename(folder, "bad:name");

        Assert.False(preview.IsValid);
        Assert.Equal(@"D:\Games\Saves\WorldOne", preview.OldPath);
        Assert.Equal(string.Empty, preview.NewPath);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("prn.txt")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT9.zip")]
    [InlineData("WorldTwo.")]
    [InlineData("WorldTwo ")]
    public void PreviewRename_rejects_windows_reserved_or_trailing_leaf_names(string newLeafName)
    {
        var folder = CreateManagedFolder(@"D:\Games\Saves\WorldOne", "WorldOne");

        var preview = FolderRenameService.PreviewRename(folder, newLeafName);

        Assert.False(preview.IsValid);
        Assert.Equal(@"D:\Games\Saves\WorldOne", preview.OldPath);
        Assert.Equal(string.Empty, preview.NewPath);
    }

    [Fact]
    public void PreviewRename_counts_matching_paths_after_normalization()
    {
        var oldPath = @"D:\Games\Saves\WorldOne";
        var folder = CreateManagedFolder(oldPath, "WorldOne");
        var config = CreateBackupConfig("config-one", new[]
        {
            CreateManagedFolder(@"D:\Games\Saves\WorldOne\", "WorldOne"),
            CreateManagedFolder(@"D:\Games\Saves\OtherWorld", "OtherWorld")
        });

        SetCurrentConfig(CreateAppConfig(new[] { config }));
        SetHistoryItems(new[]
        {
            new HistoryItem { ConfigId = "config-one", FolderPath = @"D:\Games\Saves\WorldOne\", FolderName = "WorldOne" },
            new HistoryItem { ConfigId = "config-one", FolderPath = @"D:\Games\Saves\OtherWorld", FolderName = "OtherWorld" }
        });

        var preview = FolderRenameService.PreviewRename(folder, "WorldTwo");

        Assert.True(preview.IsValid);
        Assert.Equal(1, preview.AffectedConfigCount);
        Assert.Equal(1, preview.AffectedHistoryCount);
    }

    [Fact]
    public void ResolveUpdatedDisplayName_only_changes_default_display_name()
    {
        Assert.Equal(
            "WorldTwo",
            FolderRenameService.ResolveUpdatedDisplayName("WorldOne", "WorldOne", "WorldTwo"));

        Assert.Equal(
            "My Favorite World",
            FolderRenameService.ResolveUpdatedDisplayName("My Favorite World", "WorldOne", "WorldTwo"));
    }

    [Fact]
    public void ResolveUpdatedHistoryFolderName_only_changes_matching_storage_name()
    {
        Assert.Equal(
            "WorldTwo",
            FolderRenameService.ResolveUpdatedHistoryFolderName("WorldOne", "WorldOne", "WorldTwo"));

        Assert.Equal(
            "custom-archive-name",
            FolderRenameService.ResolveUpdatedHistoryFolderName("custom-archive-name", "WorldOne", "WorldTwo"));
    }

    private static ManagedFolder CreateManagedFolder(string path, string displayName)
    {
        var folder = (ManagedFolder)RuntimeHelpers.GetUninitializedObject(typeof(ManagedFolder));
        folder.Path = path;
        folder.DisplayName = displayName;
        return folder;
    }

    private static BackupConfig CreateBackupConfig(string id, IEnumerable<ManagedFolder> sourceFolders)
    {
        var config = (BackupConfig)RuntimeHelpers.GetUninitializedObject(typeof(BackupConfig));
        config.Id = id;
        config.SourceFolders = new ObservableCollection<ManagedFolder>(sourceFolders);
        return config;
    }

    private static AppConfig CreateAppConfig(IEnumerable<BackupConfig> backupConfigs)
    {
        var appConfig = (AppConfig)RuntimeHelpers.GetUninitializedObject(typeof(AppConfig));
        appConfig.BackupConfigs = new ObservableCollection<BackupConfig>(backupConfigs);
        return appConfig;
    }

    private static void SetCurrentConfig(AppConfig appConfig)
    {
        typeof(ConfigService)
            .GetProperty(nameof(ConfigService.CurrentConfig), BindingFlags.Public | BindingFlags.Static)!
            .SetValue(null, appConfig);
    }

    private static void SetHistoryItems(IEnumerable<HistoryItem> historyItems)
    {
        typeof(HistoryService)
            .GetField("_allHistory", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, new List<HistoryItem>(historyItems));
        typeof(HistoryService)
            .GetField("_initialized", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, true);
    }
}

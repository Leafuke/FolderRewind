namespace FolderRewind.Tests;

[TestClass]
public sealed class HomePageMvvmArchitectureTests
{
    [TestMethod]
    public void CodeBehindOnlyCoordinatesFormsNavigationAndCommands()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "FolderRewind", "Views", "HomePage.xaml.cs"));
        foreach (var forbidden in new[]
                 {
                     "ConfigService", "BackupService", "NativeHistoryCoreGateway", "EncryptionService",
                     "PluginService", "OfficialTemplateImportService", "BackupPresetService",
                     "Task.Run", "File.Exists", "Directory.", "NotificationService", "XamlReader.Load"
                 })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal), $"Unexpected business dependency: {forbidden}");
        }

        StringAssert.Contains(source, "RunFormInteractionAsync");
        StringAssert.Contains(source, "CreateConfigCommand.ExecuteAsync");
        StringAssert.Contains(source, "CreateConfigFromTemplateCommand.ExecuteAsync");
    }

    [TestMethod]
    public void CreationDeletionAndSortUseTheCheckedSaveTransaction()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "FolderRewind", "ViewModels", "HomePageViewModel.Commands.cs"));
        Assert.AreEqual(3, source.Split("ConfigEditTransaction.ApplyAsync", StringSplitOptions.None).Length - 1);
        Assert.IsFalse(source.Contains("ConfigService.Save(", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("async void", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("GetAwaiter().GetResult()", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FolderRewind.slnx"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}

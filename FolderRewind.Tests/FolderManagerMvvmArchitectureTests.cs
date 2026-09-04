namespace FolderRewind.Tests;

[TestClass]
public sealed class FolderManagerMvvmArchitectureTests
{
    [TestMethod]
    public void PageHasNoPersistenceFileOrPluginBusinessCalls()
    {
        var source = File.ReadAllText(Path.Combine(FindRoot(), "FolderRewind", "Views", "FolderManagerPage.xaml.cs"));
        foreach (var dependency in new[] { "ConfigService", "FolderRenameService", "BackupService", "PluginService", "Task.Run(", "File.", "Directory.", "async void" })
            Assert.IsFalse(source.Contains(dependency, StringComparison.Ordinal), dependency);
    }

    [TestMethod]
    public void FolderRuntimePersistenceIsAsynchronous()
    {
        foreach (var relative in new[] { "ViewModels/FolderManagerPageViewModel.cs", "ViewModels/FolderManagerPageViewModel.Commands.cs", "Services/FolderRenameService.Transaction.cs" })
        {
            var source = File.ReadAllText(Path.Combine(FindRoot(), "FolderRewind", relative));
            Assert.IsFalse(source.Contains("ConfigService.Save(", StringComparison.Ordinal));
            Assert.IsFalse(source.Contains("SaveWithResult(", StringComparison.Ordinal));
        }
    }

    internal static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FolderRewind.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
}

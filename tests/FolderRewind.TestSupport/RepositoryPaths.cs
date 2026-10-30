namespace FolderRewind.Tests;

internal static class RepositoryPaths
{
    internal static string Root
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "FolderRewind.slnx"))) return directory.FullName;
            throw new DirectoryNotFoundException("FolderRewind.slnx was not found above the test output.");
        }
    }
    internal static string Application => Path.Combine(Root, Directory.Exists(Path.Combine(Root, "src", "FolderRewind")) ? "src/FolderRewind" : "FolderRewind");
}

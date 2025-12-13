namespace FolderRewind.Models
{
    public sealed class ManagerNavigationParameter
    {
        public string? ConfigId { get; set; }
        public string? FolderPath { get; set; }

        public static ManagerNavigationParameter ForConfig(string configId)
        {
            return new ManagerNavigationParameter { ConfigId = configId };
        }

        public static ManagerNavigationParameter ForFolder(string configId, string folderPath)
        {
            return new ManagerNavigationParameter { ConfigId = configId, FolderPath = folderPath };
        }
    }
}

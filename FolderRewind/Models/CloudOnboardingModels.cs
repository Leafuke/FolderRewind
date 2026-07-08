namespace FolderRewind.Models
{
    public sealed class CloudOnboardingProviderOption
    {
        public string Id { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public bool RequiresOpenList { get; set; }

        public string SuggestedRemoteBasePath { get; set; } = "remote:FolderRewind";
    }

    public sealed class CloudOnboardingResult
    {
        public bool Success { get; init; }

        public string Message { get; init; } = string.Empty;

        public string RcloneExecutablePath { get; init; } = string.Empty;

        public string OpenListExecutablePath { get; init; } = string.Empty;

        public bool OpenListInstalled => !string.IsNullOrWhiteSpace(OpenListExecutablePath);
    }
}
